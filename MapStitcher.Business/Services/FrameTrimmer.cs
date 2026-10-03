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
        private const int CircleSegments = 180;

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
                frameGeometry = GeometryFactory.Default.ToGeometry(
                    new Envelope(
                        frame.MinX,
                        frame.MaxX,
                        frame.MinY,
                        frame.MaxY));
            }


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
                    if (MarkingEntityBelongsToFrame(entity, frame))
                        output.Add(entity);

                    continue;
                }

                try
                {
                    TrimEntity(entity, frameGeometry, prepared, output);
                }
                catch
                {
                    output.Add(entity);
                }
            }

            var env = frameGeometry.EnvelopeInternal;

            output.RemoveAll(e =>
            {
                if (e == null ||
                    IndexMapLayerConfig.IsMarkingFrameLayer(e.Layer?.Name))
                {
                    return false;
                }

                try
                {
                    var b = e.GetBoundingBox();

                    if (double.IsNaN(b.Min.X) || double.IsInfinity(b.Min.X) ||
                        double.IsNaN(b.Min.Y) || double.IsInfinity(b.Min.Y) ||
                        double.IsNaN(b.Max.X) || double.IsInfinity(b.Max.X) ||
                        double.IsNaN(b.Max.Y) || double.IsInfinity(b.Max.Y))
                    {
                        return false;
                    }

                    return b.Max.X < env.MinX || b.Min.X > env.MaxX ||
                           b.Max.Y < env.MinY || b.Min.Y > env.MaxY;
                }
                catch
                {
                    return false;
                }
            });

            return output;
        }

        private static bool MarkingEntityBelongsToFrame(
            Entity entity,
            MarkingFrameResult frame)
        {
            try
            {
                var box = entity.GetBoundingBox();

                if (double.IsNaN(box.Min.X) || double.IsInfinity(box.Min.X) ||
                    double.IsNaN(box.Min.Y) || double.IsInfinity(box.Min.Y) ||
                    double.IsNaN(box.Max.X) || double.IsInfinity(box.Max.X) ||
                    double.IsNaN(box.Max.Y) || double.IsInfinity(box.Max.Y))
                {
                    return true;
                }

                var tol =
                    Math.Max(
                        frame.MaxX - frame.MinX,
                        frame.MaxY - frame.MinY) * 0.01;

                var overlapTol = tol * 8.0;

                return !(box.Max.X < frame.MinX - overlapTol ||
                         box.Min.X > frame.MaxX + overlapTol ||
                         box.Max.Y < frame.MinY - overlapTol ||
                         box.Min.Y > frame.MaxY + overlapTol);
            }
            catch
            {
                return true;
            }
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

                case Arc arc:
                    TrimLinear(
                        entity,
                        SampleArc(arc),
                        false,
                        frame,
                        prepared,
                        output);
                    return;

                case Circle circle:
                    TrimLinear(
                        entity,
                        SampleCircle(circle),
                        true,
                        frame,
                        prepared,
                        output);
                    return;

                default:
                    TrimByExtent(entity, frame, prepared, output);
                    return;
            }
        }

        private static List<Coordinate> SampleArc(Arc arc)
        {
            var start = arc.StartAngle;
            var end = arc.EndAngle;

            while (end <= start)
                end += 2.0 * Math.PI;

            var sweep = end - start;

            var steps = Math.Max(
                8,
                (int)Math.Ceiling(sweep / (2.0 * Math.PI) * CircleSegments));

            var points = new List<Coordinate>(steps + 1);

            for (var i = 0; i <= steps; i++)
            {
                var a = start + sweep * i / steps;

                points.Add(new Coordinate(
                    arc.Center.X + arc.Radius * Math.Cos(a),
                    arc.Center.Y + arc.Radius * Math.Sin(a)));
            }

            return points;
        }

        private static List<Coordinate> SampleCircle(Circle circle)
        {
            var points = new List<Coordinate>(CircleSegments + 1);

            for (var i = 0; i < CircleSegments; i++)
            {
                var a = 2.0 * Math.PI * i / CircleSegments;

                points.Add(new Coordinate(
                    circle.Center.X + circle.Radius * Math.Cos(a),
                    circle.Center.Y + circle.Radius * Math.Sin(a)));
            }

            points.Add(new Coordinate(points[0].X, points[0].Y));

            return points;
        }

        private static void TrimByExtent(
            Entity entity,
            Geometry frame,
            IPreparedGeometry prepared,
            List<Entity> output)
        {
            try
            {
                var box = entity.GetBoundingBox();

                var minX = box.Min.X;
                var minY = box.Min.Y;
                var maxX = box.Max.X;
                var maxY = box.Max.Y;

                var valid =
                    !double.IsNaN(minX) && !double.IsInfinity(minX) &&
                    !double.IsNaN(minY) && !double.IsInfinity(minY) &&
                    !double.IsNaN(maxX) && !double.IsInfinity(maxX) &&
                    !double.IsNaN(maxY) && !double.IsInfinity(maxY);

                if (valid)
                {
                    var envelope = GeometryFactory.Default.ToGeometry(
                        new Envelope(minX, maxX, minY, maxY));

                    if (prepared.Covers(envelope))
                    {
                        output.Add(entity);
                        return;
                    }

                    if (!prepared.Intersects(envelope))
                        return;
                }
            }
            catch
            {
            }

            if (ReferencePointInside(entity, prepared))
                output.Add(entity);
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

            if (prepared.Covers(lineString))
            {
                output.Add(source);
                return;
            }

            if (!prepared.Intersects(lineString))
                return;

            Geometry clipped;

            try
            {
                clipped = frame.Intersection(lineString);
            }
            catch
            {
                clipped = ClipBySegments(clean, frame, prepared, factory);
            }

            foreach (var piece in ExtractLines(clipped))
                output.Add(CreatePiece(source, piece));
        }

        private static Geometry ClipBySegments(
            List<Coordinate> coordinates,
            Geometry frame,
            IPreparedGeometry prepared,
            GeometryFactory factory)
        {
            var pieces = new List<LineString>();

            for (var i = 0; i < coordinates.Count - 1; i++)
            {
                var segment = factory.CreateLineString(
                    new[] { coordinates[i], coordinates[i + 1] });

                try
                {
                    if (!prepared.Intersects(segment))
                        continue;

                    if (prepared.Covers(segment))
                    {
                        pieces.Add(segment);
                        continue;
                    }

                    foreach (var piece in ExtractLines(
                                 frame.Intersection(segment)))
                    {
                        pieces.Add(piece);
                    }
                }
                catch
                {
                    var mid = factory.CreatePoint(new Coordinate(
                        (coordinates[i].X + coordinates[i + 1].X) / 2.0,
                        (coordinates[i].Y + coordinates[i + 1].Y) / 2.0));

                    if (prepared.Covers(mid))
                        pieces.Add(segment);
                }
            }

            return factory.CreateMultiLineString(pieces.ToArray());
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