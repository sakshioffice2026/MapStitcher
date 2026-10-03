// MapStitcher.Business/Services/MarkingFrameExtractor.cs
using ACadSharp;
using ACadSharp.Entities;
using System;
using System.Collections.Generic;
using System.Linq;

namespace MapStitcher.Business.Services
{
    public class MarkingFrameResult
    {
        public bool Found { get; set; }
        public double MinX { get; set; }
        public double MinY { get; set; }
        public double MaxX { get; set; }
        public double MaxY { get; set; }
        public List<(double X, double Y)> Polygon { get; set; } = new();
    }

    public static class MarkingFrameExtractor
    {
        public static MarkingFrameResult Extract(CadDocument doc)
        {
            return Extract(FlattenEntities(doc.Entities, 0));
        }

        // Works on an already flattened entity list (exploded INSERTs).
        public static MarkingFrameResult Extract(IEnumerable<Entity> entities)
        {
            var rings = new List<List<(double X, double Y)>>();
            var allPoints = new List<(double X, double Y)>();
            var segments = new List<((double X, double Y) A, (double X, double Y) B)>();

            var entityList = entities as IList<Entity> ?? entities.ToList();

            foreach (var entity in entityList)
            {
                if (entity == null)
                    continue;

                if (!IndexMapLayerConfig.IsMarkingFrameLayer(entity.Layer?.Name))
                    continue;

                var points = GetPoints(entity);

                if (points.Count == 0)
                    continue;

                allPoints.AddRange(points);

                var isPolylineLike =
                    entity is LwPolyline ||
                    entity is Polyline2D ||
                    entity is Polyline3D;

                if (entity is Line markLine)
                {
                    segments.Add((
                        (markLine.StartPoint.X, markLine.StartPoint.Y),
                        (markLine.EndPoint.X, markLine.EndPoint.Y)));
                }
                else if (isPolylineLike)
                {
                    for (var i = 0; i < points.Count - 1; i++)
                        segments.Add((points[i], points[i + 1]));

                    if (entity is LwPolyline closedLw &&
                        closedLw.IsClosed &&
                        points.Count > 2)
                    {
                        segments.Add((points[^1], points[0]));
                    }
                }

                if (isPolylineLike && points.Count >= 3)
                    rings.Add(points);
            }
            var fallbackPoints = allPoints;

            if (segments.Count > 0 && segments.Count <= 2000)
            {
                var maxLength = segments.Max(SegmentLength);

                var longSegments = segments
                    .Where(s => SegmentLength(s) >= maxLength * 0.3)
                    .ToList();

                if (longSegments.Count > 0)
                {
                    fallbackPoints = longSegments
                        .SelectMany(s => new[] { s.A, s.B })
                        .ToList();
                }

                var edgeRing = TryBuildFromEdgeLines(longSegments);

                if (edgeRing != null)
                {
                    rings.Clear();
                    rings.Add(edgeRing);
                }
                else
                {
                    rings.AddRange(BuildLoops(longSegments));
                }
            }


            if (allPoints.Count == 0)
                return new MarkingFrameResult { Found = false };

            // Reject degenerate candidates (e.g. an L-shaped open path whose
            // closing chord would cut the sheet diagonally).
            rings = rings
                .Where(r => PolygonArea(r) >= BoxArea(r) * 0.5)
                .ToList();

            List<(double X, double Y)>? best = null;

            if (rings.Count > 0)
            {
                var centers = new List<(double X, double Y)>();

                foreach (var entity in entityList)
                {
                    if (entity == null ||
                        IndexMapLayerConfig.IsMarkingFrameLayer(
                            entity.Layer?.Name))
                    {
                        continue;
                    }

                    var pts = GetPoints(entity);

                    if (pts.Count == 0)
                        continue;

                    centers.Add((
                        (pts.Min(p => p.X) + pts.Max(p => p.X)) / 2.0,
                        (pts.Min(p => p.Y) + pts.Max(p => p.Y)) / 2.0));
                }

                int Score(List<(double X, double Y)> ring)
                {
                    var loX = ring.Min(p => p.X);
                    var hiX = ring.Max(p => p.X);
                    var loY = ring.Min(p => p.Y);
                    var hiY = ring.Max(p => p.Y);

                    return centers.Count(c =>
                        c.X >= loX && c.X <= hiX &&
                        c.Y >= loY && c.Y <= hiY);
                }

                var largestArea = rings.Max(BoxArea);

                var sized = rings
                    .Where(r => BoxArea(r) >= largestArea * 0.25)
                    .ToList();

                if (sized.Count == 0)
                    sized = rings;

                var scores = sized.ToDictionary(r => r, Score);
                var topScore = scores.Values.Max();

                best = sized
                    .Where(r => scores[r] >= topScore * 0.95)
                    .OrderBy(BoxArea)
                    .First();

                if (BoxArea(best) <= 0)
                    best = null;
            }

            // No usable closed polyline / polygon: build the frame from
            // the extent of whatever lines the marking layer holds.
            if (best == null)
            {
                var minX = fallbackPoints.Min(p => p.X);
                var minY = fallbackPoints.Min(p => p.Y);
                var maxX = fallbackPoints.Max(p => p.X);
                var maxY = fallbackPoints.Max(p => p.Y);

                if (maxX <= minX || maxY <= minY)
                    return new MarkingFrameResult { Found = false };

                best = new List<(double X, double Y)>
                {
                    (minX, minY),
                    (maxX, minY),
                    (maxX, maxY),
                    (minX, maxY)
                };
            }

            return new MarkingFrameResult
            {
                Found = true,
                MinX = best.Min(p => p.X),
                MinY = best.Min(p => p.Y),
                MaxX = best.Max(p => p.X),
                MaxY = best.Max(p => p.Y),
                Polygon = best
            };
        }

        // Chains separate edges (LINEs, 2-vertex or open polylines) into
        // closed polygons of any shape. Edge ends within a small tolerance
        // are treated as the same corner; a path with one missing edge is
        // closed by a chord.
        private static List<List<(double X, double Y)>> BuildLoops(
            List<((double X, double Y) A, (double X, double Y) B)> segments)
        {
            var loops = new List<List<(double X, double Y)>>();

            if (segments.Count < 3)
                return loops;

            var minX = segments.Min(s => Math.Min(s.A.X, s.B.X));
            var maxX = segments.Max(s => Math.Max(s.A.X, s.B.X));
            var minY = segments.Min(s => Math.Min(s.A.Y, s.B.Y));
            var maxY = segments.Max(s => Math.Max(s.A.Y, s.B.Y));

            var diagonal = Math.Sqrt(
                (maxX - minX) * (maxX - minX) +
                (maxY - minY) * (maxY - minY));

            var tolerance = Math.Max(1e-6, diagonal * 0.015);

            var nodes = new List<(double X, double Y)>();

            int NodeOf((double X, double Y) p)
            {
                for (var i = 0; i < nodes.Count; i++)
                {
                    var dx = nodes[i].X - p.X;
                    var dy = nodes[i].Y - p.Y;

                    if (Math.Sqrt(dx * dx + dy * dy) <= tolerance)
                        return i;
                }

                nodes.Add(p);
                return nodes.Count - 1;
            }

            var edgeSet = new HashSet<(int, int)>();

            foreach (var s in segments)
            {
                var a = NodeOf(s.A);
                var b = NodeOf(s.B);

                if (a == b)
                    continue;

                edgeSet.Add(a < b ? (a, b) : (b, a));
            }

            if (edgeSet.Count < 3)
                return loops;

            var adjacency = new List<int>[nodes.Count];

            for (var i = 0; i < adjacency.Length; i++)
                adjacency[i] = new List<int>();

            foreach (var (a, b) in edgeSet)
            {
                adjacency[a].Add(b);
                adjacency[b].Add(a);
            }

            var visited = new bool[nodes.Count];

            for (var start = 0; start < nodes.Count; start++)
            {
                if (visited[start] || adjacency[start].Count == 0)
                    continue;

                var component = new List<int>();
                var stack = new Stack<int>();

                stack.Push(start);
                visited[start] = true;

                while (stack.Count > 0)
                {
                    var current = stack.Pop();
                    component.Add(current);

                    foreach (var next in adjacency[current])
                    {
                        if (!visited[next])
                        {
                            visited[next] = true;
                            stack.Push(next);
                        }
                    }
                }

                if (component.Count < 3)
                    continue;

                var endpoints = component
                    .Where(n => adjacency[n].Count == 1)
                    .ToList();

                var allDegreeTwo = component.All(n => adjacency[n].Count == 2);

                var isPath =
                    endpoints.Count == 2 &&
                    component.All(n => adjacency[n].Count <= 2);

                if (!allDegreeTwo && !isPath)
                    continue;

                var walkStart = allDegreeTwo ? component[0] : endpoints[0];

                var ring = new List<(double X, double Y)>();
                var previous = -1;
                var currentNode = walkStart;

                while (true)
                {
                    ring.Add(nodes[currentNode]);

                    var candidates = adjacency[currentNode]
                        .Where(n => n != previous)
                        .ToList();

                    if (candidates.Count == 0)
                        break;

                    var following = candidates[0];

                    if (following == walkStart || ring.Count > component.Count)
                        break;

                    previous = currentNode;
                    currentNode = following;
                }

                if (ring.Count >= 3 && BoxArea(ring) > 0)
                    loops.Add(ring);
            }

            return loops;
        }

        public static bool IsInsideFrame(
            Entity entity,
            MarkingFrameResult frame)
        {
            if (!frame.Found) return true;

            try
            {
                if (entity is LwPolyline lw && lw.Vertices.Count > 0)
                {
                    // All vertices must be inside
                    return lw.Vertices.All(v =>
                        PointInPolygon(v.Location.X, v.Location.Y, frame.Polygon));
                }

                if (entity is Polyline2D p2d && p2d.Vertices.Count > 0)
                {
                    // All vertices must be inside
                    return p2d.Vertices.All(v =>
                        PointInPolygon(v.Location.X, v.Location.Y, frame.Polygon));
                }

                if (entity is Polyline3D p3d && p3d.Vertices.Count > 0)
                {
                    return p3d.Vertices.All(v =>
                        PointInPolygon(v.Location.X, v.Location.Y, frame.Polygon));
                }

                if (entity is Line line)
                {
                    // Both endpoints must be inside
                    return PointInPolygon(line.StartPoint.X, line.StartPoint.Y, frame.Polygon)
                        && PointInPolygon(line.EndPoint.X, line.EndPoint.Y, frame.Polygon);
                }

                if (entity is TextEntity text)
                    return PointInPolygon(text.InsertPoint.X, text.InsertPoint.Y, frame.Polygon);

                if (entity is MText mtext)
                    return PointInPolygon(mtext.InsertPoint.X, mtext.InsertPoint.Y, frame.Polygon);

                if (entity is Circle circle)
                    return PointInPolygon(circle.Center.X, circle.Center.Y, frame.Polygon);

                if (entity is Arc arc)
                    return PointInPolygon(arc.Center.X, arc.Center.Y, frame.Polygon);

                if (entity is ACadSharp.Entities.Point point)
                    return PointInPolygon(point.Location.X, point.Location.Y, frame.Polygon);

                // Unknown type — keep it
                return true;
            }
            catch
            {
                return true;
            }
        }

        private static double SegmentLength(
            ((double X, double Y) A, (double X, double Y) B) s)
        {
            var dx = s.B.X - s.A.X;
            var dy = s.B.Y - s.A.Y;

            return Math.Sqrt(dx * dx + dy * dy);
        }

        private static (double X, double Y)? LineIntersection(
            ((double X, double Y) A, (double X, double Y) B) s1,
            ((double X, double Y) A, (double X, double Y) B) s2)
        {
            var x1 = s1.A.X; var y1 = s1.A.Y;
            var x2 = s1.B.X; var y2 = s1.B.Y;
            var x3 = s2.A.X; var y3 = s2.A.Y;
            var x4 = s2.B.X; var y4 = s2.B.Y;

            var denominator =
                (x1 - x2) * (y3 - y4) - (y1 - y2) * (x3 - x4);

            if (Math.Abs(denominator) < 1e-12)
                return null;

            var t =
                ((x1 - x3) * (y3 - y4) - (y1 - y3) * (x3 - x4)) /
                denominator;

            return (x1 + t * (x2 - x1), y1 + t * (y2 - y1));
        }

        // Frames drawn as four long edge lines (possibly slightly skewed and
        // overshooting the corners): the polygon corners are the
        // intersections of the extended edge lines.
        private static List<(double X, double Y)>? TryBuildFromEdgeLines(
            List<((double X, double Y) A, (double X, double Y) B)> longSegments)
        {
            var horizontals = longSegments
                .Where(s =>
                    Math.Abs(s.B.Y - s.A.Y) <=
                    Math.Abs(s.B.X - s.A.X) * 0.15)
                .ToList();

            var verticals = longSegments
                .Where(s =>
                    Math.Abs(s.B.X - s.A.X) <=
                    Math.Abs(s.B.Y - s.A.Y) * 0.15)
                .ToList();

            if (horizontals.Count < 2 || verticals.Count < 2)
                return null;

            var maxH = horizontals.Max(SegmentLength);
            var maxV = verticals.Max(SegmentLength);

            var hs = horizontals
                .Where(s => SegmentLength(s) >= maxH * 0.5)
                .ToList();

            var vs = verticals
                .Where(s => SegmentLength(s) >= maxV * 0.5)
                .ToList();

            if (hs.Count < 2 || vs.Count < 2)
                return null;

            double MidX(((double X, double Y) A, (double X, double Y) B) s)
                => (s.A.X + s.B.X) / 2.0;

            double MidY(((double X, double Y) A, (double X, double Y) B) s)
                => (s.A.Y + s.B.Y) / 2.0;

            var top = hs.OrderByDescending(MidY).First();
            var bottom = hs.OrderBy(MidY).First();
            var left = vs.OrderBy(MidX).First();
            var right = vs.OrderByDescending(MidX).First();

            if (MidY(top) - MidY(bottom) < maxV * 0.2 ||
                MidX(right) - MidX(left) < maxH * 0.2)
            {
                return null;
            }

            var tl = LineIntersection(top, left);
            var tr = LineIntersection(top, right);
            var br = LineIntersection(bottom, right);
            var bl = LineIntersection(bottom, left);

            if (tl == null || tr == null || br == null || bl == null)
                return null;

            return new List<(double X, double Y)>
            {
                tl.Value,
                tr.Value,
                br.Value,
                bl.Value
            };
        }

        private static double PolygonArea(List<(double X, double Y)> ring)
        {
            double sum = 0;

            for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
            {
                sum += (ring[j].X + ring[i].X) * (ring[j].Y - ring[i].Y);
            }

            return Math.Abs(sum) / 2.0;
        }

        private static double BoxArea(List<(double X, double Y)> ring)
        {
            var w = ring.Max(p => p.X) - ring.Min(p => p.X);
            var h = ring.Max(p => p.Y) - ring.Min(p => p.Y);
            return w * h;
        }

        private static List<(double X, double Y)> GetPoints(Entity entity)
        {
            var points = new List<(double X, double Y)>();

            void Add(double x, double y)
            {
                if (!double.IsNaN(x) && !double.IsInfinity(x) &&
                    !double.IsNaN(y) && !double.IsInfinity(y))
                {
                    points.Add((x, y));
                }
            }

            try
            {
                switch (entity)
                {
                    case LwPolyline lw:
                        foreach (var v in lw.Vertices)
                            Add(v.Location.X, v.Location.Y);
                        break;

                    case Polyline2D p2d:
                        foreach (var v in p2d.Vertices)
                            Add(v.Location.X, v.Location.Y);
                        break;

                    case Polyline3D p3d:
                        foreach (var v in p3d.Vertices)
                            Add(v.Location.X, v.Location.Y);
                        break;

                    case Line line:
                        Add(line.StartPoint.X, line.StartPoint.Y);
                        Add(line.EndPoint.X, line.EndPoint.Y);
                        break;

                    default:
                        var box = entity.GetBoundingBox();
                        Add(box.Min.X, box.Min.Y);
                        Add(box.Max.X, box.Max.Y);
                        break;
                }
            }
            catch
            {
                points.Clear();
            }

            return points;
        }

        // Ray-casting point-in-polygon
        private static bool PointInPolygon(
            double px,
            double py,
            List<(double X, double Y)> polygon)
        {
            bool inside = false;
            int n = polygon.Count;

            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                double xi = polygon[i].X, yi = polygon[i].Y;
                double xj = polygon[j].X, yj = polygon[j].Y;

                bool intersects =
                    ((yi > py) != (yj > py)) &&
                    (px < (xj - xi) * (py - yi) / (yj - yi) + xi);

                if (intersects) inside = !inside;
            }

            return inside;
        }

        private static IEnumerable<Entity> FlattenEntities(
            IEnumerable<Entity> entities,
            int depth)
        {
            if (depth > 32) yield break;

            foreach (var entity in entities)
            {
                if (entity == null) continue;

                if (entity is Insert insert)
                {
                    ACadSharp.Tables.BlockRecord? block = null;
                    try { block = insert.Block; } catch { continue; }
                    if (block == null) continue;

                    List<Entity>? children = null;
                    try { children = block.Entities.Where(x => x != null).ToList(); }
                    catch { continue; }

                    foreach (var child in FlattenEntities(children, depth + 1))
                        yield return child;

                    continue;
                }

                yield return entity;
            }
        }
    }
}