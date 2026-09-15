using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using CSMath;
using MapStitcher.Business.Contracts;
using MapStitcher.Database;
using MapStitcher.Repositories.Contracts;
using MapStitcher.Utilities;
using Microsoft.Extensions.Logging;
using NetTopologySuite.Geometries;
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

        // Counts boundary-capable entities skipped this parse run because they
        // had fewer than 3 vertices, purely for diagnostics/logging.
        private int _skippedShortPolylineCount;

        private const double SpatialThreshold = 5.0;
        private const string AdjacentSheetLayerName = "Text_Adjacent_No";

        // Matches "लागू ... शिट ... नं ... <digits>" allowing arbitrary whitespace/punctuation
        // between the words, as produced by CAD text entry (e.g. "लागू    शिट     नं .   3").
        private static readonly Regex AdjacentSheetPattern =
            new Regex(@"लागू.*?शिट.*?नं.*?(\d+)", RegexOptions.Compiled);

        private static readonly GeometryFactory _geomFactory = new GeometryFactory(new PrecisionModel(), 0);

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
            var sheet = await _sheetRepo.GetByIdAsync(sheetId);
            if (sheet == null)
                throw new InvalidOperationException($"SurveySheet {sheetId} not found.");

            CadDocument document;
            try
            {
                document = LoadDocument(sheet);
            }
            catch (Exception ex)
            {
                sheet.Status = SheetStatus.Failed;
                await _sheetRepo.SaveChangesAsync();
                throw new InvalidOperationException($"Failed to parse CAD file: {ex.Message}", ex);
            }

            sheet.DwgVersion = document.Header?.Version.ToString();
            ComputeSheetExtents(document, sheet);
            _skippedShortPolylineCount = 0;
            int boundaryCandidateCount = 0;

            // Pass 1: Collect text candidates with their InsertPoint positions
            var textCandidates = new List<(string Label, double X, double Y)>();

            // Pass 2: Collect cross-hair tie-point marker positions (block inserts)
            var rawPoints = new List<(double X, double Y)>();

            foreach (var entity in document.Entities)
            {
                switch (entity)
                {
                    case TextEntity text:
                        ExtractTextCandidate(
                            text.Value,
                            text.Layer?.Name,
                            text.InsertPoint.X,
                            text.InsertPoint.Y,
                            sheet,
                            textCandidates);
                        break;

                    case MText mtext:
                        ExtractTextCandidate(
                            mtext.Value,
                            mtext.Layer?.Name,
                            mtext.InsertPoint.X,
                            mtext.InsertPoint.Y,
                            sheet,
                            textCandidates);
                        break;

                    case Insert insert:
                        rawPoints.Add((insert.InsertPoint.X, insert.InsertPoint.Y));
                        break;

                    case LwPolyline lwPoly:
                        boundaryCandidateCount++;
                        await ProcessBoundaryAsync(
                            lwPoly.Vertices.Select(v => new Coordinate(v.Location.X, v.Location.Y)),
                            sheet);
                        break;

                    case Polyline2D poly2d:
                        boundaryCandidateCount++;
                        await ProcessBoundaryAsync(
                            poly2d.Vertices.Select(v => new Coordinate(v.Location.X, v.Location.Y)),
                            sheet);
                        break;
                }
            }

            if (boundaryCandidateCount == 0)
            {
                _logger?.LogWarning(
                    "Sheet {SheetId}: no LwPolyline/Polyline2D entities found in the CAD file. " +
                    "The boundary may be drawn with an unsupported entity type (e.g. LINE, SPLINE, 3D POLYLINE, HATCH) " +
                    "or on a block insert.",
                    sheetId);
            }
            else if (_skippedShortPolylineCount == boundaryCandidateCount)
            {
                _logger?.LogWarning(
                    "Sheet {SheetId}: found {Count} polyline entity(ies) but all had fewer than 3 vertices; " +
                    "no boundary geometry was saved.",
                    sheetId, boundaryCandidateCount);
            }

            // Pass 3: Greedy unique nearest-neighbor assignment
            // Each TEXT label can be claimed by at most one POINT, and vice versa —
            // closest pairs are matched first, preventing one label from being
            // reused across multiple points (which was corrupting merge accuracy).
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

            int extractedCount = 0;

            for (int pi = 0; pi < rawPoints.Count; pi++)
            {
                var tiePoint = new TiePoint
                {
                    SheetID = sheet.SheetID,
                    SourceX = rawPoints[pi].X,
                    SourceY = rawPoints[pi].Y,
                    PointLabel = assignedLabel[pi],
                    CreatedDate = DateTime.UtcNow
                };

                await _tiePointRepo.AddAsync(tiePoint);
                extractedCount++;
            }

            sheet.Status = extractedCount > 0 ? SheetStatus.Parsed : SheetStatus.Failed;

            await _tiePointRepo.SaveChangesAsync();
            await _boundaryRepo.SaveChangesAsync();
            await _sheetRepo.SaveChangesAsync();
        }

        // Computes true CAD extents from every ModelSpace entity (not just
        // closed LwPolyline/Polyline2D boundaries), so the UI viewer has
        // reliable bounds even when the sheet's boundary is drawn with LINE,
        // ARC, SPLINE, HATCH, or block-insert geometry. Falls back to
        // HasGeometry = false with zeroed bounds when no entity yields a
        // finite bounding box, instead of leaving the sheet with no
        // usable extents at all.
        private void ComputeSheetExtents(CadDocument document, SurveySheet sheet)
        {
            double minX = double.MaxValue, minY = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue;
            bool found = false;

            foreach (var entity in document.Entities)
            {
                BoundingBox box;
                try
                {
                    box = entity.GetBoundingBox();
                }
                catch (Exception ex)
                {
                    _logger?.LogDebug(
                        "Sheet {SheetId}: entity {EntityType} bounding box unavailable ({Message}); skipped for extents.",
                        sheet.SheetID, entity.GetType().Name, ex.Message);
                    continue;
                }

                if (!IsFinite(box.Min.X) || !IsFinite(box.Min.Y) ||
                    !IsFinite(box.Max.X) || !IsFinite(box.Max.Y))
                    continue;

                if (box.Min.X > box.Max.X || box.Min.Y > box.Max.Y)
                    continue;

                if (box.Min.X < minX) minX = box.Min.X;
                if (box.Min.Y < minY) minY = box.Min.Y;
                if (box.Max.X > maxX) maxX = box.Max.X;
                if (box.Max.Y > maxY) maxY = box.Max.Y;
                found = true;
            }

            if (!found)
            {
                sheet.HasGeometry = false;
                sheet.BoundsMinX = 0;
                sheet.BoundsMinY = 0;
                sheet.BoundsMaxX = 0;
                sheet.BoundsMaxY = 0;

                _logger?.LogWarning(
                    "Sheet {SheetId}: no entity produced a finite bounding box; extents fell back to (0,0,0,0).",
                    sheet.SheetID);

                return;
            }

            sheet.HasGeometry = true;
            sheet.BoundsMinX = minX;
            sheet.BoundsMinY = minY;
            sheet.BoundsMaxX = maxX;
            sheet.BoundsMaxY = maxY;
        }

        private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

        private CadDocument LoadDocument(SurveySheet sheet)
        {
            var fullPath = System.IO.Path.Combine(_uploadRootPath, sheet.FilePath);
            if (!System.IO.File.Exists(fullPath))
                throw new System.IO.FileNotFoundException("DWG/DXF file not found on disk.", fullPath);

            var ext = System.IO.Path.GetExtension(fullPath).ToLowerInvariant();

            return ext == ".dxf"
                ? DxfReader.Read(fullPath)
                : DwgReader.Read(fullPath);
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

            // Accept alphanumeric text containing at least one digit, length 2-30.
            // This allows "Sheet 5", "Plot 12A", "HOUSE-3" etc. to become candidates
            // and tie-point labels, without which merge and grid placement fail.
            if (rawText.Any(char.IsDigit) && rawText.Length >= 2 && rawText.Length <= 30)
                candidates.Add((rawText.Trim(), x, y));
        }

        private async Task ProcessBoundaryAsync(IEnumerable<Coordinate> rawCoords, SurveySheet sheet)
        {
            var coords = rawCoords.ToList();
            if (coords.Count < 3)
            {
                _skippedShortPolylineCount++;
                _logger?.LogWarning(
                    "Sheet {SheetId}: skipped a polyline with only {VertexCount} vertex(es); at least 3 are required to form a boundary.",
                    sheet.SheetID, coords.Count);
                return;
            }

            if (!coords[0].Equals2D(coords[^1]))
                coords.Add(coords[0]);

            var polygon = _geomFactory.CreatePolygon(coords.ToArray());

            var boundary = new SheetBoundary
            {
                SheetID = sheet.SheetID,
                PlotLabel = null,
                Geometry = polygon,
                CreatedDate = DateTime.UtcNow
            };

            await _boundaryRepo.AddAsync(boundary);
        }
    }
}