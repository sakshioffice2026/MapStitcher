// MapStitcher.Business/Services/NeatlineExtractionService.cs
using ACadSharp;
using ACadSharp.Entities;
using MapStitcher.Business.Contracts;
using MapStitcher.Model;

namespace MapStitcher.Business.Services
{
    public class NeatlineExtractionService : INeatlineExtractionService
    {
        private const int MaxBlockDepth = 8;

        public NeatlineExtent Extract(CadDocument document, string neatlineLayerName)
        {
            var points = new List<(double X, double Y)>();

            // Poly_Survey_Bndry geometry, like Sym_Hatch/Sym_Title, is often
            // authored inside a block definition rather than directly in
            // model space, so entities are walked through every INSERT
            // (with the transform that maps each entity's local coordinates
            // to world space) instead of reading document.Entities alone.
            foreach (var (entity, ctx) in FlattenEntities(document.Entities, FlattenContext.Identity, 0))
            {
                var layerName = entity.Layer?.Name ?? string.Empty;
                if (!string.Equals(layerName, neatlineLayerName, StringComparison.OrdinalIgnoreCase))
                    continue;

                switch (entity)
                {
                    case Line line:
                        points.Add(ctx.ToWorld(line.StartPoint.X, line.StartPoint.Y));
                        points.Add(ctx.ToWorld(line.EndPoint.X, line.EndPoint.Y));
                        break;

                    case LwPolyline poly:
                        foreach (var v in poly.Vertices)
                            points.Add(ctx.ToWorld(v.Location.X, v.Location.Y));
                        break;

                    case Polyline2D poly2d:
                        foreach (var v in poly2d.Vertices)
                            points.Add(ctx.ToWorld(v.Location.X, v.Location.Y));
                        break;

                    case Circle circle:
                        var (cx, cy) = ctx.ToWorld(circle.Center.X, circle.Center.Y);
                        points.Add((cx - circle.Radius, cy - circle.Radius));
                        points.Add((cx + circle.Radius, cy + circle.Radius));
                        break;
                }
            }

            if (points.Count == 0)
                return new NeatlineExtent();

            return new NeatlineExtent
            {
                MinX = points.Min(p => p.X),
                MinY = points.Min(p => p.Y),
                MaxX = points.Max(p => p.X),
                MaxY = points.Max(p => p.Y)
            };
        }

        private readonly struct FlattenContext
        {
            public double OriginX { get; }
            public double OriginY { get; }
            public double Rotation { get; }
            public double ScaleX { get; }
            public double ScaleY { get; }
            public double BaseX { get; }
            public double BaseY { get; }

            public FlattenContext(
                double originX, double originY, double rotation,
                double scaleX, double scaleY, double baseX, double baseY)
            {
                OriginX = originX;
                OriginY = originY;
                Rotation = rotation;
                ScaleX = scaleX;
                ScaleY = scaleY;
                BaseX = baseX;
                BaseY = baseY;
            }

            public static FlattenContext Identity => new(0, 0, 0, 1, 1, 0, 0);

            public (double X, double Y) ToWorld(double localX, double localY)
            {
                double cos = Math.Cos(Rotation);
                double sin = Math.Sin(Rotation);
                double lx = (localX - BaseX) * ScaleX;
                double ly = (localY - BaseY) * ScaleY;
                double rx = lx * cos - ly * sin;
                double ry = lx * sin + ly * cos;
                return (OriginX + rx, OriginY + ry);
            }
        }

        private static IEnumerable<(Entity Entity, FlattenContext Context)> FlattenEntities(
            IEnumerable<Entity> entities,
            FlattenContext ctx,
            int depth)
        {
            if (depth > MaxBlockDepth)
                yield break;

            foreach (var entity in entities)
            {
                if (entity is Insert insert)
                {
                    List<Entity>? children = null;
                    double baseX = 0;
                    double baseY = 0;

                    try
                    {
                        var block = insert.Block;
                        var blockEntities = block?.Entities;

                        if (blockEntities != null)
                        {
                            children = blockEntities.Cast<Entity>().ToList();

                            var basePoint = block?.BlockEntity?.BasePoint;
                            if (basePoint != null)
                            {
                                baseX = basePoint.Value.X;
                                baseY = basePoint.Value.Y;
                            }
                        }
                    }
                    catch
                    {
                        children = null;
                    }

                    var (wx, wy) = ctx.ToWorld(insert.InsertPoint.X, insert.InsertPoint.Y);

                    var childCtx = new FlattenContext(
                        wx, wy,
                        ctx.Rotation + insert.Rotation,
                        ctx.ScaleX * insert.XScale,
                        ctx.ScaleY * insert.YScale,
                        baseX, baseY);

                    if (children != null)
                    {
                        foreach (var item in FlattenEntities(children, childCtx, depth + 1))
                            yield return item;
                    }

                    continue;
                }

                yield return (entity, ctx);
            }
        }
    }
}