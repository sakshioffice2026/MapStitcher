// MapStitcher.Business/Services/NeatlineExtractionService.cs
using ACadSharp;
using ACadSharp.Entities;
using MapStitcher.Business.Contracts;
using MapStitcher.Model;

namespace MapStitcher.Business.Services
{
    public class NeatlineExtractionService : INeatlineExtractionService
    {
        public NeatlineExtent Extract(CadDocument document, string neatlineLayerName)
        {
            var points = new List<(double X, double Y)>();

            foreach (var entity in document.Entities)
            {
                var layerName = entity.Layer?.Name ?? string.Empty;
                if (!string.Equals(layerName, neatlineLayerName, StringComparison.OrdinalIgnoreCase))
                    continue;

                switch (entity)
                {
                    case Line line:
                        points.Add((line.StartPoint.X, line.StartPoint.Y));
                        points.Add((line.EndPoint.X, line.EndPoint.Y));
                        break;

                    case LwPolyline poly:
                        foreach (var v in poly.Vertices)
                            points.Add((v.Location.X, v.Location.Y));
                        break;

                    case Polyline2D poly2d:
                        foreach (var v in poly2d.Vertices)
                            points.Add((v.Location.X, v.Location.Y));
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
    }
}