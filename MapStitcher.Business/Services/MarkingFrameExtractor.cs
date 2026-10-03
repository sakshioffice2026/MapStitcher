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

            foreach (var entity in entities)
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

                if (isPolylineLike && points.Count >= 3)
                    rings.Add(points);
            }

            if (allPoints.Count == 0)
                return new MarkingFrameResult { Found = false };

            List<(double X, double Y)>? best = null;

            if (rings.Count > 0)
            {
                best = rings
                    .OrderByDescending(BoxArea)
                    .First();

                if (BoxArea(best) <= 0)
                    best = null;
            }

            // No usable closed polyline / polygon: build the frame from
            // the extent of whatever lines the marking layer holds.
            if (best == null)
            {
                var minX = allPoints.Min(p => p.X);
                var minY = allPoints.Min(p => p.Y);
                var maxX = allPoints.Max(p => p.X);
                var maxY = allPoints.Max(p => p.Y);

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