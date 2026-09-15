using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using MapStitcher.Business.Contracts;
using MapStitcher.Database;
using MapStitcher.Repositories.Contracts;
using MapStitcher.Utilities;
using Microsoft.Extensions.Logging;
using NetTopologySuite.Geometries;
using System.Reflection;
using System.Text.RegularExpressions;

namespace MapStitcher.Business.Services
{
    public class CadastralParsingService : ICadastralParsingService
    {
        private readonly ISurveySheetRepository _sheetRepo;
        private readonly ITiePointRepository _tiePointRepo;
        private readonly ISheetBoundaryRepository _boundaryRepo;
        private readonly string _uploadRootPath;
        private readonly ILogger<CadastralParsingService>? _logger;

        private int _skippedShortPolylineCount;
        private const double SpatialThreshold = 5.0;
        private const string AdjacentSheetLayerName = "Text_Adjacent_No";

        private static readonly Regex AdjacentSheetPattern =
            new Regex(@"लागू.*?शिट.*?नं.*?(\d+)", RegexOptions.Compiled);

        private static readonly GeometryFactory _geomFactory =
            new GeometryFactory(new PrecisionModel(), 0);

        public CadastralParsingService(
            ISurveySheetRepository sheetRepo,
            ITiePointRepository tiePointRepo,
            ISheetBoundaryRepository boundaryRepo,
            string uploadRootPath,
            ILogger<CadastralParsingService>? logger = null)
        {
            _sheetRepo = sheetRepo;
            _tiePointRepo = tiePointRepo;
            _boundaryRepo = boundaryRepo;
            _uploadRootPath = uploadRootPath;
            _logger = logger;
        }

        public async Task ParseSheetAsync(int sheetId)
        {
            var sheet = await _sheetRepo.GetByIdAsync(sheetId)
                ?? throw new InvalidOperationException($"SurveySheet {sheetId} not found.");

            CadDocument document;
            try
            {
                document = LoadDocument(sheet);
            }
            catch (Exception ex)
            {
                sheet.Status = SheetStatus.Failed;
                sheet.FailureReason = ex.Message;
                await _sheetRepo.SaveChangesAsync();
                throw new InvalidOperationException($"Failed to parse CAD file: {ex.Message}", ex);
            }

            sheet.DwgVersion = document.Header?.Version.ToString();
            sheet.FailureReason = null;
            _skippedShortPolylineCount = 0;

            var textCandidates = new List<(string Label, double X, double Y)>();
            var rawPoints = new List<(double X, double Y)>();
            var extent = new CadExtent();
            var hasExplicitBoundary = false;
            var entityCount = 0;

            // ACadSharp exposes the complete model-space entity collection and every
            // entity has GetBoundingBox(). We use the exact polygon where a usable
            // closed/open polyline exists, and otherwise persist the union of all
            // entity extents so the viewer never starts with an empty geometry state.
            foreach (var entity in document.Entities)
            {
                entityCount++;
                TryAccumulateEntityExtent(entity, extent);

                switch (entity)
                {
                    case TextEntity text:
                        ExtractTextCandidate(text.Value, text.Layer?.Name,
                            text.InsertPoint.X, text.InsertPoint.Y, sheet, textCandidates);
                        break;

                    case MText mtext:
                        ExtractTextCandidate(mtext.Value, mtext.Layer?.Name,
                            mtext.InsertPoint.X, mtext.InsertPoint.Y, sheet, textCandidates);
                        break;

                    case Insert insert:
                        rawPoints.Add((insert.InsertPoint.X, insert.InsertPoint.Y));
                        break;

                    case LwPolyline lwPoly:
                        if (await ProcessBoundaryAsync(
                                lwPoly.Vertices.Select(v => new Coordinate(v.Location.X, v.Location.Y)),
                                sheet))
                            hasExplicitBoundary = true;
                        break;

                    case Polyline2D poly2d:
                        if (await ProcessBoundaryAsync(
                                poly2d.Vertices.Select(v => new Coordinate(v.Location.X, v.Location.Y)),
                                sheet))
                            hasExplicitBoundary = true;
                        break;
                }
            }

            // Many production DWGs contain boundaries as LINE/SPLINE/HATCH/blocks
            // rather than LWPOLYLINE. A document-level extent is still reliable for
            // preview and placement, so materialize it as a renderable polygon when
            // no real polyline boundary was found.
            if (!hasExplicitBoundary && extent.IsValid)
            {
                await AddExtentBoundaryAsync(sheet, extent);
                _logger?.LogInformation(
                    "Sheet {SheetId}: persisted CAD extent fallback ({MinX},{MinY})-({MaxX},{MaxY}) from {EntityCount} entities.",
                    sheetId, extent.MinX, extent.MinY, extent.MaxX, extent.MaxY, entityCount);
            }

            if (entityCount == 0)
            {
                sheet.Status = SheetStatus.Failed;
                sheet.FailureReason = "CAD file parsed but contains no model-space entities.";
                await _sheetRepo.SaveChangesAsync();
                throw new InvalidOperationException(sheet.FailureReason);
            }

            if (!extent.IsValid && !hasExplicitBoundary)
            {
                sheet.Status = SheetStatus.Failed;
                sheet.FailureReason = "CAD file contains no renderable entity extents.";
                await _sheetRepo.SaveChangesAsync();
                throw new InvalidOperationException(sheet.FailureReason);
            }

            var candidatePairs = new List<(int PointIndex, int TextIndex, double Distance)>();
            for (int pi = 0; pi < rawPoints.Count; pi++)
            {
                for (int ti = 0; ti < textCandidates.Count; ti++)
                {
                    double distance = Math.Sqrt(
                        Math.Pow(rawPoints[pi].X - textCandidates[ti].X, 2) +
                        Math.Pow(rawPoints[pi].Y - textCandidates[ti].Y, 2));
                    if (distance <= SpatialThreshold)
                        candidatePairs.Add((pi, ti, distance));
                }
            }

            candidatePairs.Sort((a, b) => a.Distance.CompareTo(b.Distance));
            var assignedLabel = new string?[rawPoints.Count];
            var usedTextIndex = new HashSet<int>();
            var usedPointIndex = new HashSet<int>();

            foreach (var (pointIndex, textIndex, _) in candidatePairs)
            {
                if (usedPointIndex.Contains(pointIndex) || usedTextIndex.Contains(textIndex))
                    continue;
                assignedLabel[pointIndex] = textCandidates[textIndex].Label;
                usedPointIndex.Add(pointIndex);
                usedTextIndex.Add(textIndex);
            }

            foreach (var point in rawPoints)
            {
                var index = rawPoints.IndexOf(point);
                await _tiePointRepo.AddAsync(new TiePoint
                {
                    SheetID = sheet.SheetID,
                    SourceX = point.X,
                    SourceY = point.Y,
                    PointLabel = assignedLabel[index],
                    CreatedDate = DateTime.UtcNow
                });
            }

            // Geometry, not tie-point extraction, determines parse success. A valid
            // DWG with zero INSERT markers must still be immediately renderable.
            sheet.Status = SheetStatus.Parsed;
            sheet.FailureReason = null;

            await _tiePointRepo.SaveChangesAsync();
            await _boundaryRepo.SaveChangesAsync();
            await _sheetRepo.SaveChangesAsync();
        }

        private CadDocument LoadDocument(SurveySheet sheet)
        {
            var fullPath = Path.Combine(_uploadRootPath, sheet.FilePath);
            if (!File.Exists(fullPath))
                throw new FileNotFoundException("DWG/DXF file not found on disk.", fullPath);

            var ext = Path.GetExtension(fullPath).ToLowerInvariant();
            return ext == ".dxf" ? DxfReader.Read(fullPath) : DwgReader.Read(fullPath);
        }

        public async Task<List<RawTextDump>> DumpRawTextAsync(int sheetId)
        {
            var sheet = await _sheetRepo.GetByIdAsync(sheetId)
                ?? throw new InvalidOperationException($"SurveySheet {sheetId} not found.");

            var document = LoadDocument(sheet);
            var results = new List<RawTextDump>();

            foreach (var entity in document.Entities)
            {
                string? raw = entity switch
                {
                    TextEntity text => text.Value,
                    MText mtext => mtext.Value,
                    _ => null
                };

                if (string.IsNullOrWhiteSpace(raw))
                    continue;

                var (x, y, layer) = entity switch
                {
                    TextEntity text => (text.InsertPoint.X, text.InsertPoint.Y, text.Layer?.Name),
                    MText mtext => (mtext.InsertPoint.X, mtext.InsertPoint.Y, mtext.Layer?.Name),
                    _ => (0.0, 0.0, (string?)null)
                };

                results.Add(new RawTextDump
                {
                    EntityType = entity.GetType().Name,
                    LayerName = layer ?? "",
                    RawValue = raw,
                    DecodedValue = DxfUnicodeEscapeDecoder.Decode(raw),
                    X = x,
                    Y = y
                });
            }

            return results;
        }

        private void ExtractTextCandidate(
            string? rawText,
            string? layerName,
            double x, double y,
            SurveySheet sheet,
            List<(string, double, double)> candidates)
        {
            if (string.IsNullOrWhiteSpace(rawText))
                return;

            if (string.Equals(layerName, AdjacentSheetLayerName, StringComparison.OrdinalIgnoreCase))
            {
                var decoded = DxfUnicodeEscapeDecoder.Decode(rawText);
                var match = AdjacentSheetPattern.Match(decoded);
                if (match.Success)
                    sheet.LaghuReferenceNumber = match.Groups[1].Value;
                return;
            }

            if (rawText.Any(char.IsDigit) && rawText.Length >= 2 && rawText.Length <= 30)
                candidates.Add((rawText.Trim(), x, y));
        }

        private async Task<bool> ProcessBoundaryAsync(
            IEnumerable<Coordinate> rawCoords,
            SurveySheet sheet)
        {
            var coords = rawCoords.ToList();
            if (coords.Count < 3)
            {
                _skippedShortPolylineCount++;
                return false;
            }

            if (!coords[0].Equals2D(coords[^1]))
                coords.Add(coords[0]);

            try
            {
                var polygon = _geomFactory.CreatePolygon(coords.ToArray());
                if (!polygon.IsValid || polygon.Area <= 0)
                {
                    _skippedShortPolylineCount++;
                    return false;
                }

                await _boundaryRepo.AddAsync(new SheetBoundary
                {
                    SheetID = sheet.SheetID,
                    PlotLabel = null,
                    Geometry = polygon,
                    CreatedDate = DateTime.UtcNow
                });
                return true;
            }
            catch (ArgumentException)
            {
                _skippedShortPolylineCount++;
                return false;
            }
        }

        private async Task AddExtentBoundaryAsync(SurveySheet sheet, CadExtent extent)
        {
            var coordinates = new[]
            {
                new Coordinate(extent.MinX, extent.MinY),
                new Coordinate(extent.MaxX, extent.MinY),
                new Coordinate(extent.MaxX, extent.MaxY),
                new Coordinate(extent.MinX, extent.MaxY),
                new Coordinate(extent.MinX, extent.MinY)
            };

            await _boundaryRepo.AddAsync(new SheetBoundary
            {
                SheetID = sheet.SheetID,
                PlotLabel = "CAD_EXTENT",
                Geometry = _geomFactory.CreatePolygon(coordinates),
                CreatedDate = DateTime.UtcNow
            });
        }

        private static void TryAccumulateEntityExtent(Entity entity, CadExtent target)
        {
            try
            {
                var box = entity.GetBoundingBox();
                if (box == null)
                    return;

                // Keep this adapter reflection-based so a minor ACadSharp BoundingBox
                // API shape change does not make the upload pipeline fail to compile.
                var min = box.GetType().GetProperty("Min")?.GetValue(box);
                var max = box.GetType().GetProperty("Max")?.GetValue(box);
                if (min == null || max == null)
                    return;

                var minX = ReadCoordinate(min, "X");
                var minY = ReadCoordinate(min, "Y");
                var maxX = ReadCoordinate(max, "X");
                var maxY = ReadCoordinate(max, "Y");

                if (minX.HasValue && minY.HasValue && maxX.HasValue && maxY.HasValue)
                    target.Include(minX.Value, minY.Value, maxX.Value, maxY.Value);
            }
            catch
            {
                // Proxy/unsupported entities are allowed; other entities still define
                // the document extent. Do not make one bad entity abort upload.
            }
        }

        private static double? ReadCoordinate(object point, string name)
        {
            var property = point.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            var value = property?.GetValue(point);
            return value == null ? null : Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture);
        }

        private sealed class CadExtent
        {
            public double MinX { get; private set; } = double.MaxValue;
            public double MinY { get; private set; } = double.MaxValue;
            public double MaxX { get; private set; } = double.MinValue;
            public double MaxY { get; private set; } = double.MinValue;
            public bool IsValid => MinX <= MaxX && MinY <= MaxY;

            public void Include(double minX, double minY, double maxX, double maxY)
            {
                MinX = Math.Min(MinX, Math.Min(minX, maxX));
                MinY = Math.Min(MinY, Math.Min(minY, maxY));
                MaxX = Math.Max(MaxX, Math.Max(minX, maxX));
                MaxY = Math.Max(MaxY, Math.Max(minY, maxY));
            }
        }
    }
}