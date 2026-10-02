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
            var candidates = new List<List<(double X, double Y)>>();

            foreach (var entity in FlattenEntities(doc.Entities, 0))
            {
                var layerName = entity.Layer?.Name ?? string.Empty;

                if (!string.Equals(
                        layerName,
                        IndexMapLayerConfig.MarkingFrameLayer,
                        StringComparison.OrdinalIgnoreCase))
                    continue;

                List<(double X, double Y)>? ring = null;

                if (entity is LwPolyline lw && lw.Vertices.Count >= 3)
                    ring = lw.Vertices.Select(v => (v.Location.X, v.Location.Y)).ToList();
                else if (entity is Polyline2D p2d && p2d.Vertices.Count >= 3)
                    ring = p2d.Vertices.Select(v => (v.Location.X, v.Location.Y)).ToList();

                if (ring != null && ring.Count >= 3)
                    candidates.Add(ring);
            }

            if (candidates.Count == 0)
                return new MarkingFrameResult { Found = false };

            // Pick largest closed polygon by bounding-box area
            var best = candidates
                .OrderByDescending(r =>
                {
                    var w = r.Max(p => p.X) - r.Min(p => p.X);
                    var h = r.Max(p => p.Y) - r.Min(p => p.Y);
                    return w * h;
                })
                .First();

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

                if (entity is Point point)
                    return PointInPolygon(point.Location.X, point.Location.Y, frame.Polygon);

                // Unknown type — keep it
                return true;
            }
            catch
            {
                return true;
            }
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