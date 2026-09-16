using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using MapStitcher.Business.Contracts;
using MapStitcher.Model;
using MapStitcher.Utilities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace MapStitcher.Business.Services
{
    public class CadCoordinateInspectionService
        : ICadCoordinateInspectionService
    {
        private const string LaghuLayerName = "Text_Adjacent_No";

        private static readonly Regex LaghuLabelRegex =
            new Regex(
                @"(?:लागू|लागु)\s*(?:शिट|शीट|शिटचा|शीटचा)\s*(?:नं|क्र|क्रमांक|नंबर)\s*[:\.\-]?\s*([0-9०-९]+)",
                RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex SheetLabelRegex =
            new Regex(
                @"(?:sheet|sh\.?)\s*(?:no\.?|number|nr\.?)?\s*[:\.\-]?\s*([0-9०-९]+)",
                RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex NumericOnlyRegex =
            new Regex(
                @"^\s*([0-9०-९]{1,6})\s*$",
                RegexOptions.Compiled);

        private static readonly Regex NumberAtEndRegex =
            new Regex(
                @"(?:^|\s|[:\.\-])([0-9०-९]{1,6})\s*$",
                RegexOptions.Compiled);

        public Task<CadCoordinateInspectionResult> InspectFileAsync(
            string filePath,
            string originalFileName)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("CAD file path is required.", nameof(filePath));

            if (!System.IO.File.Exists(filePath))
                throw new System.IO.FileNotFoundException("CAD file was not found.", filePath);

            var extension = System.IO.Path
                .GetExtension(filePath)
                .ToLowerInvariant();

            if (extension != ".dwg" && extension != ".dxf")
                throw new InvalidOperationException("Only DWG and DXF files are supported.");

            var sheetNumber = System.IO.Path
                .GetFileNameWithoutExtension(originalFileName);

            var result = new CadCoordinateInspectionResult
            {
                SheetNumber = sheetNumber
            };

            CadDocument document = extension == ".dwg"
                ? DwgReader.Read(filePath)
                : DxfReader.Read(filePath);

            int entityCount = 0;

            foreach (var entity in document.Entities)
            {
                try
                {
                    if (ProcessEntity(entity, result))
                        entityCount++;
                }
                catch
                {
                    // Do not allow one bad/unsupported CAD entity to stop the inspection.
                }
            }

            result.EntityCount = entityCount;

            CalculateExtents(result);

            return Task.FromResult(result);
        }

        private bool ProcessEntity(
            Entity entity,
            CadCoordinateInspectionResult result)
        {
            switch (entity)
            {
                case TextEntity textEntity:
                    ProcessText(textEntity, result);
                    return true;

                case MText mText:
                    ProcessMText(mText, result);
                    return true;

                case Insert insert:
                    ProcessInsert(insert, result);
                    return true;

                case LwPolyline lwPolyline:
                    ProcessLwPolyline(lwPolyline, result);
                    return true;

                case Polyline2D polyline2D:
                    ProcessPolyline2D(polyline2D, result);
                    return true;

                case Line line:
                    ProcessLine(line, result);
                    return true;

                case Circle circle:
                    ProcessCircle(circle, result);
                    return true;

                default:
                    return false;
            }
        }

        private void ProcessText(
            TextEntity entity,
            CadCoordinateInspectionResult result)
        {
            var x = entity.InsertPoint.X;
            var y = entity.InsertPoint.Y;
            var text = DecodeText(entity.Value);
            var layerName = entity.Layer?.Name ?? string.Empty;
            var laghuReferenceNumber = ExtractLaghuReferenceNumber(text, layerName);

            AddCoordinate(result, "TEXT", layerName, text, x, y, true, laghuReferenceNumber, "Text");
            TryAddLaghuReference(result, text, layerName, x, y);
        }

        private void ProcessMText(
            MText entity,
            CadCoordinateInspectionResult result)
        {
            var x = entity.InsertPoint.X;
            var y = entity.InsertPoint.Y;
            var text = DecodeText(entity.Value);
            var layerName = entity.Layer?.Name ?? string.Empty;
            var laghuReferenceNumber = ExtractLaghuReferenceNumber(text, layerName);

            AddCoordinate(result, "MTEXT", layerName, text, x, y, true, laghuReferenceNumber, "MText");
            TryAddLaghuReference(result, text, layerName, x, y);
        }

        private void ProcessInsert(
            Insert entity,
            CadCoordinateInspectionResult result)
        {
            var x = entity.InsertPoint.X;
            var y = entity.InsertPoint.Y;
            var layerName = entity.Layer?.Name ?? string.Empty;

            AddCoordinate(result, "INSERT", layerName, null, x, y, true, null, "Block Insert");
        }

        private void ProcessLwPolyline(
            LwPolyline entity,
            CadCoordinateInspectionResult result)
        {
            var layerName = entity.Layer?.Name ?? string.Empty;

            foreach (var vertex in entity.Vertices)
            {
                AddCoordinate(
                    result, "LWPOLYLINE", layerName, null,
                    vertex.Location.X, vertex.Location.Y,
                    false, null, "Polyline Vertex");
            }
        }

        private void ProcessPolyline2D(
            Polyline2D entity,
            CadCoordinateInspectionResult result)
        {
            var layerName = entity.Layer?.Name ?? string.Empty;

            foreach (var vertex in entity.Vertices)
            {
                AddCoordinate(
                    result, "POLYLINE2D", layerName, null,
                    vertex.Location.X, vertex.Location.Y,
                    false, null, "Polyline Vertex");
            }
        }

        private void ProcessLine(
            Line entity,
            CadCoordinateInspectionResult result)
        {
            var layerName = entity.Layer?.Name ?? string.Empty;

            AddCoordinate(result, "LINE", layerName, null,
                entity.StartPoint.X, entity.StartPoint.Y,
                false, null, "Line Start");

            AddCoordinate(result, "LINE", layerName, null,
                entity.EndPoint.X, entity.EndPoint.Y,
                false, null, "Line End");
        }

        private void ProcessCircle(
            Circle entity,
            CadCoordinateInspectionResult result)
        {
            var layerName = entity.Layer?.Name ?? string.Empty;

            AddCoordinate(result, "CIRCLE", layerName, null,
                entity.Center.X, entity.Center.Y,
                false, null, "Circle Center");
        }

        private void AddCoordinate(
            CadCoordinateInspectionResult result,
            string entityType,
            string layerName,
            string? text,
            double x,
            double y,
            bool hasInsertionPoint,
            string? laghuReferenceNumber,
            string shape)
        {
            result.Coordinates.Add(new CadCoordinateViewModel
            {
                SheetID = result.SheetID,
                SheetNumber = result.SheetNumber,
                EntityType = entityType,
                LayerName = layerName,
                Text = text,
                X = x,
                Y = y,
                HasInsertionPoint = hasInsertionPoint,
                LaghuReferenceNumber = laghuReferenceNumber,
                Shape = shape
            });
        }

        private void TryAddLaghuReference(
            CadCoordinateInspectionResult result,
            string? text,
            string layerName,
            double x,
            double y)
        {
            if (string.IsNullOrWhiteSpace(text))
                return;

            // text is already decoded by callers — no second DecodeText call needed.
            var decodedText = text.Trim();

            var referenceNumber = ExtractLaghuReferenceNumber(decodedText, layerName);

            if (string.IsNullOrWhiteSpace(referenceNumber))
                return;

            AddLaghuReference(result, referenceNumber, decodedText, layerName, x, y);
        }

        private void AddLaghuReference(
            CadCoordinateInspectionResult result,
            string referenceNumber,
            string text,
            string layerName,
            double x,
            double y)
        {
            if (string.IsNullOrWhiteSpace(referenceNumber))
                return;

            var alreadyExists = result.LaghuReferences.Any(reference =>
                string.Equals(
                    reference.ReferenceNumber,
                    referenceNumber,
                    StringComparison.OrdinalIgnoreCase)
                && Math.Abs(reference.X - x) < 0.000001
                && Math.Abs(reference.Y - y) < 0.000001);

            if (alreadyExists)
                return;

            result.LaghuReferences.Add(new LaghuReferenceViewModel
            {
                ReferenceNumber = referenceNumber,
                Text = text,
                LayerName = layerName,
                X = x,
                Y = y
            });
        }

        private static string? ExtractLaghuReferenceNumber(
            string? text,
            string? layerName)
        {
            if (string.IsNullOrWhiteSpace(text))
                return null;

            var decoded = DecodeText(text).Trim();

            var match = LaghuLabelRegex.Match(decoded);
            if (match.Success)
                return NormalizeReferenceNumber(match.Groups[1].Value);

            match = SheetLabelRegex.Match(decoded);
            if (match.Success)
                return NormalizeReferenceNumber(match.Groups[1].Value);

            if (IsLaghuLayer(layerName))
            {
                match = NumericOnlyRegex.Match(decoded);
                if (match.Success)
                    return NormalizeReferenceNumber(match.Groups[1].Value);

                match = NumberAtEndRegex.Match(decoded);
                if (match.Success)
                    return NormalizeReferenceNumber(match.Groups[1].Value);
            }

            return null;
        }

        private static bool IsLaghuLayer(string? layerName)
        {
            if (string.IsNullOrWhiteSpace(layerName))
                return false;

            return string.Equals(
                layerName.Trim(),
                LaghuLayerName,
                StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeReferenceNumber(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            var chars = value.Trim().ToCharArray();

            for (var i = 0; i < chars.Length; i++)
            {
                chars[i] = chars[i] switch
                {
                    '०' => '0',
                    '१' => '1',
                    '२' => '2',
                    '३' => '3',
                    '४' => '4',
                    '५' => '5',
                    '६' => '6',
                    '७' => '7',
                    '८' => '8',
                    '९' => '9',
                    _ => chars[i]
                };
            }

            return new string(chars);
        }

        private static string DecodeText(string? text)
        {
            if (string.IsNullOrEmpty(text))
                return string.Empty;

            try
            {
                return DxfUnicodeEscapeDecoder.Decode(text);
            }
            catch
            {
                return text;
            }
        }

        private static void CalculateExtents(CadCoordinateInspectionResult result)
        {
            if (result.Coordinates.Count == 0)
            {
                result.MinX = 0;
                result.MinY = 0;
                result.MaxX = 0;
                result.MaxY = 0;
                return;
            }

            result.MinX = result.Coordinates.Min(c => c.X);
            result.MinY = result.Coordinates.Min(c => c.Y);
            result.MaxX = result.Coordinates.Max(c => c.X);
            result.MaxY = result.Coordinates.Max(c => c.Y);
        }
    }
}