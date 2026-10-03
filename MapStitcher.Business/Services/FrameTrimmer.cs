// MapStitcher.Business/Services/FrameTrimmer.cs
using ACadSharp.Entities;
using CSMath;
using NetTopologySuite.Geometries;
using NetTopologySuite.Geometries.Prepared;
using System;
using System.Collections.Generic;
using System.Linq;

namespace MapStitcher.Business.Services
{
    public static class FrameTrimmer
    {
        // Removes everything outside the sheet's marking frame.
        // Marking-frame entities themselves are always kept.
        // If no frame is found the list is returned unchanged.
        public static List<Entity> Trim(
            List<Entity> entities,
            out bool frameFound)
        {
            var frame = MarkingFrameExtractor.Extract(entities);

            frameFound = frame.Found;

            if (!frame.Found || frame.Polygon.Count < 3)
            {
                frameFound = false;
                return entities;
            }

            var frameGeometry = BuildFrameGeometry(frame);

            if (frameGeometry == null || frameGeometry.IsEmpty)
            {
                frameFound = false;
                return entities;
            }

            var prepared = PreparedGeometryFactory.Prepare(frameGeometry);

            var output = new List<Entity>(entities.Count);

            foreach (var entity in entities)
            {
                if (entity == null)
                    continue;

                if (IndexMapLayerConfig.IsMarkingFrameLayer(entity.Layer?.Name))
                {
                    output.Add(entity);
                    continue;
                }

                try
                {
                    TrimEntity(entity, frameGeometry, prepared, output);
                }
                catch
                {
                    // Never lose data because a single entity failed.
                    output.Add(entity);
                }
            }

            return output;
        }

        private static Geometry? BuildFrameGeometry(MarkingFrameResult frame)
        {
            var coords = frame.Polygon
                .Select(p => new Coordinate(p.X, p.Y))
                .ToList();

            if (coords.Count < 3)
                return null;

            if (!coords[0].Equals2D(coords[^1]))
                coords.Add(new Coordinate(coords[0].X, coords[0].Y));

            if (coords.Count < 4)
                return null;

            var factory = GeometryFactory.Default;

            Geometry polygon = factory.CreatePolygon(coords.ToArray());

            if (!polygon.IsValid)
            {
                var repaired = polygon.Buffer(0);

                if (repaired is Polygon repairedPolygon)
                {
                    polygon = repairedPolygon;
                }
                else if (repaired is MultiPolygon multi &&
                         multi.NumGeometries > 0)
                {
                    polygon = multi.Geometries
                        .OrderByDescending(g => g.Area)
                        .First();
                }
                else
                {
                    return null;
                }
            }

            var width = frame.MaxX - frame.MinX;
            var height = frame.MaxY - frame.MinY;

            var tolerance =
                Math.Max(
                    1e-6,
                    Math.Sqrt(width * width + height * height) * 1e-5);

            return polygon.Buffer(tolerance);
        }

        private static void TrimEntity(
            Entity entity,
            Geometry frame,
            IPreparedGeometry prepared,
            List<Entity> output)
        {
            switch (entity)
            {
                case LwPolyline lw:
                    TrimLinear(
                        entity,
                        lw.Vertices
                            .Select(v => new Coordinate(
                                v.Location.X,
                                v.Location.Y))
                            .ToList(),
                        lw.IsClosed,
                        frame,
                        prepared,
                        output);
                    return;

                case Polyline2D p2d:
                    TrimLinear(
                        entity,
                        p2d.Vertices
                            .Select(v => new Coordinate(
                                v.Location.X,
                                v.Location.Y))
                            .ToList(),
                        false,
                        frame,
                        prepared,
                        output);
                    return;

                case Polyline3D p3d:
                    TrimLinear(
                        entity,
                        p3d.Vertices
                            .Select(v => new Coordinate(
                                v.Location.X,
                                v.Location.Y))
                            .ToList(),
                        false,
                        frame,
                        prepared,
                        output);
                    return;

                case Line line:
                    TrimLinear(
                        entity,
                        new List<Coordinate>
                        {
                            new Coordinate(
                                line.StartPoint.X,
                                line.StartPoint.Y),
                            new Coordinate(
                                line.EndPoint.X,
                                line.EndPoint.Y)
                        },
                        false,
                        frame,
                        prepared,
                        output);
                    return;

                default:
                    if (ReferencePointInside(entity, prepared))
                        output.Add(entity);
                    return;
            }
        }

        private static void TrimLinear(
            Entity source,
            List<Coordinate> rawCoordinates,
            bool declaredClosed,
            Geometry frame,
            IPreparedGeometry prepared,
            List<Entity> output)
        {
            var clean = new List<Coordinate>();

            foreach (var c in rawCoordinates)
            {
                if (double.IsNaN(c.X) || double.IsInfinity(c.X) ||
                    double.IsNaN(c.Y) || double.IsInfinity(c.Y))
                {
                    continue;
                }

                if (clean.Count == 0 || clean[^1].Distance(c) > 1e-12)
                    clean.Add(c);
            }

            if (clean.Count == 0)
                return;

            var factory = GeometryFactory.Default;

            if (clean.Count == 1)
            {
                if (prepared.Covers(factory.CreatePoint(clean[0])))
                    output.Add(source);

                return;
            }

            var closed =
                declaredClosed ||
                (clean.Count >= 3 && clean[0].Equals2D(clean[^1]));

            if (closed &&
                clean.Count >= 3 &&
                !clean[0].Equals2D(clean[^1]))
            {
                clean.Add(new Coordinate(clean[0].X, clean[0].Y));
            }

            var lineString = factory.CreateLineString(clean.ToArray());

            // Fully inside: keep the original entity untouched (bulges,
            // widths, closed flag and everything else are preserved).
            if (prepared.Covers(lineString))
            {
                output.Add(source);
                return;
            }

            // Fully outside: drop.
            if (!prepared.Intersects(lineString))
                return;

            // Crossing the frame: keep only the portion(s) inside.
            var clipped = frame.Intersection(lineString);

            foreach (var piece in ExtractLines(clipped))
                output.Add(CreatePiece(source, piece));
        }

        private static IEnumerable<LineString> ExtractLines(Geometry geometry)
        {
            if (geometry == null || geometry.IsEmpty)
                yield break;

            if (geometry is LineString ls)
            {
                if (ls.NumPoints >= 2 && ls.Length > 1e-9)
                    yield return ls;

                yield break;
            }

            for (var i = 0; i < geometry.NumGeometries; i++)
            {
                var child = geometry.GetGeometryN(i);

                if (ReferenceEquals(child, geometry))
                    yield break;

                foreach (var line in ExtractLines(child))
                    yield return line;
            }
        }

        private static Entity CreatePiece(Entity source, LineString line)
        {
            var piece = new LwPolyline();

            piece.Layer = source.Layer;
            piece.Color = source.Color;
            piece.LineType = source.LineType;
            piece.LineWeight = source.LineWeight;

            var coordinates = line.Coordinates.ToList();

            var closed =
                line.IsClosed &&
                coordinates.Count >= 4;

            if (closed)
                coordinates.RemoveAt(coordinates.Count - 1);

            foreach (var c in coordinates)
            {
                piece.Vertices.Add(
                    new LwPolyline.Vertex(
                        new XY(c.X, c.Y)));
            }

            if (closed)
                piece.IsClosed = true;

            return piece;
        }

        private static bool ReferencePointInside(
            Entity entity,
            IPreparedGeometry prepared)
        {
            double x;
            double y;

            try
            {
                switch (entity)
                {
                    case TextEntity text:
                        x = text.InsertPoint.X;
                        y = text.InsertPoint.Y;
                        break;

                    case MText mtext:
                        x = mtext.InsertPoint.X;
                        y = mtext.InsertPoint.Y;
                        break;

                    case Circle circle:
                        x = circle.Center.X;
                        y = circle.Center.Y;
                        break;

                    case ACadSharp.Entities.Point point:
                        x = point.Location.X;
                        y = point.Location.Y;
                        break;

                    default:
                        var box = entity.GetBoundingBox();

                        x = (box.Min.X + box.Max.X) / 2.0;
                        y = (box.Min.Y + box.Max.Y) / 2.0;
                        break;
                }
            }
            catch
            {
                return true;
            }

            if (double.IsNaN(x) || double.IsInfinity(x) ||
                double.IsNaN(y) || double.IsInfinity(y))
            {
                return true;
            }

            return prepared.Covers(
                GeometryFactory.Default.CreatePoint(
                    new Coordinate(x, y)));
        }
    }
}