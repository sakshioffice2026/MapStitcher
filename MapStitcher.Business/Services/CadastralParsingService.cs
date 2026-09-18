//using ACadSharp;
//using ACadSharp.Entities;
//using ACadSharp.IO;
//using CSMath;
//using MapStitcher.Business.Contracts;
//using MapStitcher.Database;
//using MapStitcher.Repositories.Contracts;
//using MapStitcher.Utilities;
//using Microsoft.Extensions.Logging;
//using NetTopologySuite.Geometries;
//using System.Text.RegularExpressions;

//namespace MapStitcher.Business.Services
//{
//    public class CadastralParsingService : ICadastralParsingService
//    {
//        private readonly ISurveySheetRepository _sheetRepo;
//        private readonly ITiePointRepository _tiePointRepo;
//        private readonly ISheetBoundaryRepository _boundaryRepo;
//        private readonly string _uploadRootPath;
//        private readonly ILogger<CadastralParsingService>? _logger;

//        // Counts boundary-capable entities skipped this parse run because they
//        // had fewer than 3 vertices, purely for diagnostics/logging.
//        private int _skippedShortPolylineCount;

//        private const double SpatialThreshold = 5.0;
//        private const string AdjacentSheetLayerName = "Text_Adjacent_No";

//        // Layer that carries the sheet's title block, including the small
//        // plus-shaped adjacency index box (this sheet's own number plus up to
//        // 4 neighbor sheet numbers, one per cardinal direction).
//        private const string TitleBlockLayerName = "Sym_Title";

//        // Layers that are print/plot scaffolding (A0/A1 scale rulers, 20x20
//        // reference grid) rather than real survey geometry. These sit at an
//        // arbitrary offset far from the actual drawing and, if included,
//        // blow the sheet's extents up to many times the true size and get
//        // saved as bogus "boundary" rectangles. Excluded from both extent
//        // computation and boundary/shape extraction.
//        private static readonly HashSet<string> NonSurveyLayers = new(StringComparer.OrdinalIgnoreCase)
//        {
//            "Grid",
//            "Line_20_20_Grid",
//            "Image"
//        };

//        // Layers that actually represent the sheet's own boundary (as opposed
//        // to individual plot outlines, the legend, or the print frame). Used
//        // for the "Shape" (rectangle) check — we only judge sheet shape from
//        // geometry drawn on these layers.
//        private static readonly HashSet<string> SheetBoundaryLayers = new(StringComparer.OrdinalIgnoreCase)
//        {
//            "Poly_Survey_Bndry",
//            "Poly_Village_Bndry",
//            "Poly_Off",
//            "Poly_Survey_Bndry_Cancel",
//            "Poly_Builtup"
//        };

//        // How far (in drawing units) a candidate neighbor number may sit from
//        // the sheet's own number in the title-block index box before it's
//        // considered unrelated text rather than part of the cross layout.
//        private const double IndexBoxProximity = 250.0;

//        // Tolerance, in degrees, for treating a boundary's corner angle as
//        // a right angle when deciding whether it is a clean rectangle.
//        private const double RectangleAngleToleranceDegrees = 2.0;

//        // Matches "लागू ... शिट ... नं ... <digits>" allowing arbitrary whitespace/punctuation
//        // between the words, as produced by CAD text entry (e.g. "लागू    शिट     नं .   3").
//        private static readonly Regex AdjacentSheetPattern =
//            new Regex(@"लागू.*?शिट.*?नं.*?(\d+)", RegexOptions.Compiled);

//        // A title-block index-box cell holds nothing but a bare sheet number
//        // (e.g. "1", "23"), unlike the surrounding title text.
//        private static readonly Regex BareNumberPattern =
//            new Regex(@"^\d{1,6}$", RegexOptions.Compiled);

//        // Matches "शीट क्र .1)" / "शीट क्र. 1" / "शीट क्रं (12)" allowing arbitrary
//        // whitespace/punctuation between the words, same tolerant style as
//        // AdjacentSheetPattern. This is the sheet's real, DWG-authored number —
//        // used to overwrite the filename-derived placeholder SheetNumber
//        // assigned at upload time, and required for ResolveIndexBoxNeighbors'
//        // self-cell match against the title-block index box.
//        private static readonly Regex SheetTitleNumberPattern =
//            new Regex(@"शीट.*?क्र.*?(\d+)", RegexOptions.Compiled);

//        private static readonly GeometryFactory _geomFactory = new GeometryFactory(new PrecisionModel(), 0);

//        public CadastralParsingService(
//            ISurveySheetRepository sheetRepo,
//            ITiePointRepository tiePointRepo,
//            ISheetBoundaryRepository boundaryRepo,
//            string uploadRootPath,
//            ILogger<CadastralParsingService>? logger = null)
//        {
//            _sheetRepo = sheetRepo;
//            _tiePointRepo = tiePointRepo;
//            _boundaryRepo = boundaryRepo;
//            _uploadRootPath = uploadRootPath;
//            _logger = logger;
//        }

//        public async Task ParseSheetAsync(int sheetId)
//        {
//            var sheet = await _sheetRepo.GetByIdAsync(sheetId);
//            if (sheet == null)
//                throw new InvalidOperationException($"SurveySheet {sheetId} not found.");

//            CadDocument document;
//            try
//            {
//                document = LoadDocument(sheet);
//            }
//            catch (Exception ex)
//            {
//                sheet.Status = SheetStatus.Failed;
//                await _sheetRepo.SaveChangesAsync();
//                throw new InvalidOperationException($"Failed to parse CAD file: {ex.Message}", ex);
//            }

//            sheet.DwgVersion = document.Header?.Version.ToString();
//            ComputeSheetExtents(document, sheet);
//            _skippedShortPolylineCount = 0;
//            int boundaryCandidateCount = 0;

//            // Pass 1: Collect text candidates with their InsertPoint positions
//            var textCandidates = new List<(string Label, double X, double Y)>();

//            // Pass 2: Collect cross-hair tie-point marker positions (block inserts)
//            var rawPoints = new List<(double X, double Y)>();

//            // Pass 3: Collect bare-number labels from the title-block layer —
//            // these are the sheet's own number plus its plus-shaped index-box
//            // neighbor numbers (N/S/E/W). Resolved into directions after the
//            // main loop, once we have every candidate's position.
//            var titleBlockNumbers = new List<(string Value, double X, double Y)>();

//            // The sheet's real number as printed in the title caption
//            // ("शीट क्र .1)"), captured once found. Overwrites the
//            // filename-derived placeholder SheetNumber after the loop.
//            string? titleSheetNumber = null;

//            // Boundary geometry actually drawn on the sheet's own boundary
//            // layers (as opposed to plot outlines, legend boxes, or the
//            // print-frame rectangle) — used for the rectangle "Shape" check.
//            var sheetBoundaryRings = new List<List<Coordinate>>();

//            foreach (var entity in document.Entities)
//            {
//                var layerName = entity.Layer?.Name;

//                switch (entity)
//                {
//                    case TextEntity text:
//                        if (string.Equals(layerName, TitleBlockLayerName, StringComparison.OrdinalIgnoreCase))
//                        {
//                            CollectTitleBlockNumber(text.Value, text.InsertPoint.X, text.InsertPoint.Y, titleBlockNumbers);
//                            titleSheetNumber ??= TryExtractSheetTitleNumber(text.Value);
//                        }

//                        ExtractTextCandidate(
//                            text.Value,
//                            layerName,
//                            text.InsertPoint.X,
//                            text.InsertPoint.Y,
//                            sheet,
//                            textCandidates);
//                        break;

//                    case MText mtext:
//                        if (string.Equals(layerName, TitleBlockLayerName, StringComparison.OrdinalIgnoreCase))
//                        {
//                            CollectTitleBlockNumber(mtext.Value, mtext.InsertPoint.X, mtext.InsertPoint.Y, titleBlockNumbers);
//                            titleSheetNumber ??= TryExtractSheetTitleNumber(mtext.Value);
//                        }

//                        ExtractTextCandidate(
//                            mtext.Value,
//                            layerName,
//                            mtext.InsertPoint.X,
//                            mtext.InsertPoint.Y,
//                            sheet,
//                            textCandidates);
//                        break;

//                    case Insert insert:
//                        rawPoints.Add((insert.InsertPoint.X, insert.InsertPoint.Y));
//                        break;

//                    case LwPolyline lwPoly:
//                        // Skip print-frame / reference-grid rectangles entirely —
//                        // they are not survey geometry and must never become a
//                        // tie-point-bearing "boundary".
//                        if (NonSurveyLayers.Contains(layerName ?? string.Empty))
//                            break;

//                        boundaryCandidateCount++;
//                        var lwCoords = lwPoly.Vertices.Select(v => new Coordinate(v.Location.X, v.Location.Y)).ToList();
//                        await ProcessBoundaryAsync(lwCoords, sheet);

//                        if (SheetBoundaryLayers.Contains(layerName ?? string.Empty))
//                            sheetBoundaryRings.Add(lwCoords);
//                        break;

//                    case Polyline2D poly2d:
//                        if (NonSurveyLayers.Contains(layerName ?? string.Empty))
//                            break;

//                        boundaryCandidateCount++;
//                        var poly2dCoords = poly2d.Vertices.Select(v => new Coordinate(v.Location.X, v.Location.Y)).ToList();
//                        await ProcessBoundaryAsync(poly2dCoords, sheet);

//                        if (SheetBoundaryLayers.Contains(layerName ?? string.Empty))
//                            sheetBoundaryRings.Add(poly2dCoords);
//                        break;
//                }
//            }

//            // Real DWG sheet number takes over from the filename-derived
//            // placeholder assigned at upload time. Must happen before
//            // ResolveIndexBoxNeighbors, which matches its "self" cell
//            // against sheet.SheetNumber.
//            if (!string.IsNullOrWhiteSpace(titleSheetNumber))
//                sheet.SheetNumber = titleSheetNumber;

//            ResolveIndexBoxNeighbors(titleBlockNumbers, sheet);
//            sheet.BoundaryIsRectangle = DetermineIsRectangle(sheetBoundaryRings);

//            if (boundaryCandidateCount == 0)
//            {
//                _logger?.LogWarning(
//                    "Sheet {SheetId}: no LwPolyline/Polyline2D entities found in the CAD file. " +
//                    "The boundary may be drawn with an unsupported entity type (e.g. LINE, SPLINE, 3D POLYLINE, HATCH) " +
//                    "or on a block insert.",
//                    sheetId);
//            }
//            else if (_skippedShortPolylineCount == boundaryCandidateCount)
//            {
//                _logger?.LogWarning(
//                    "Sheet {SheetId}: found {Count} polyline entity(ies) but all had fewer than 3 vertices; " +
//                    "no boundary geometry was saved.",
//                    sheetId, boundaryCandidateCount);
//            }

//            // Pass 3: Greedy unique nearest-neighbor assignment
//            // Each TEXT label can be claimed by at most one POINT, and vice versa —
//            // closest pairs are matched first, preventing one label from being
//            // reused across multiple points (which was corrupting merge accuracy).
//            var candidatePairs = new List<(int PointIndex, int TextIndex, double Distance)>();

//            for (int pi = 0; pi < rawPoints.Count; pi++)
//            {
//                for (int ti = 0; ti < textCandidates.Count; ti++)
//                {
//                    double distance = Math.Sqrt(
//                        Math.Pow(rawPoints[pi].X - textCandidates[ti].X, 2) +
//                        Math.Pow(rawPoints[pi].Y - textCandidates[ti].Y, 2));

//                    if (distance <= SpatialThreshold)
//                        candidatePairs.Add((pi, ti, distance));
//                }
//            }

//            candidatePairs.Sort((a, b) => a.Distance.CompareTo(b.Distance));

//            var assignedLabel = new string?[rawPoints.Count];
//            var usedTextIndex = new HashSet<int>();
//            var usedPointIndex = new HashSet<int>();

//            foreach (var (pointIndex, textIndex, _) in candidatePairs)
//            {
//                if (usedPointIndex.Contains(pointIndex) || usedTextIndex.Contains(textIndex))
//                    continue;

//                assignedLabel[pointIndex] = textCandidates[textIndex].Label;
//                usedPointIndex.Add(pointIndex);
//                usedTextIndex.Add(textIndex);
//            }

//            int extractedCount = 0;

//            for (int pi = 0; pi < rawPoints.Count; pi++)
//            {
//                var tiePoint = new TiePoint
//                {
//                    SheetID = sheet.SheetID,
//                    SourceX = rawPoints[pi].X,
//                    SourceY = rawPoints[pi].Y,
//                    PointLabel = assignedLabel[pi],
//                    CreatedDate = DateTime.UtcNow
//                };

//                await _tiePointRepo.AddAsync(tiePoint);
//                extractedCount++;
//            }

//            sheet.Status = extractedCount > 0 ? SheetStatus.Parsed : SheetStatus.Failed;

//            await _tiePointRepo.SaveChangesAsync();
//            await _boundaryRepo.SaveChangesAsync();
//            await _sheetRepo.SaveChangesAsync();
//        }

//        // Computes true CAD extents from every real-survey ModelSpace entity
//        // (not just closed LwPolyline/Polyline2D boundaries), so the UI
//        // viewer has reliable bounds even when the sheet's boundary is drawn
//        // with LINE, ARC, SPLINE, HATCH, or block-insert geometry. Entities
//        // on print/plot scaffolding layers (NonSurveyLayers) are skipped —
//        // otherwise a scale-reference block pasted far outside the actual
//        // drawing inflates the sheet to many times its real size. Falls back
//        // to HasGeometry = false with zeroed bounds when no surviving entity
//        // yields a finite bounding box, instead of leaving the sheet with no
//        // usable extents at all.
//        private void ComputeSheetExtents(CadDocument document, SurveySheet sheet)
//        {
//            double minX = double.MaxValue, minY = double.MaxValue;
//            double maxX = double.MinValue, maxY = double.MinValue;
//            bool found = false;

//            foreach (var entity in document.Entities)
//            {
//                if (NonSurveyLayers.Contains(entity.Layer?.Name ?? string.Empty))
//                    continue;

//                BoundingBox box;
//                try
//                {
//                    box = entity.GetBoundingBox();
//                }
//                catch (Exception ex)
//                {
//                    _logger?.LogDebug(
//                        "Sheet {SheetId}: entity {EntityType} bounding box unavailable ({Message}); skipped for extents.",
//                        sheet.SheetID, entity.GetType().Name, ex.Message);
//                    continue;
//                }

//                if (!IsFinite(box.Min.X) || !IsFinite(box.Min.Y) ||
//                    !IsFinite(box.Max.X) || !IsFinite(box.Max.Y))
//                    continue;

//                if (box.Min.X > box.Max.X || box.Min.Y > box.Max.Y)
//                    continue;

//                if (box.Min.X < minX) minX = box.Min.X;
//                if (box.Min.Y < minY) minY = box.Min.Y;
//                if (box.Max.X > maxX) maxX = box.Max.X;
//                if (box.Max.Y > maxY) maxY = box.Max.Y;
//                found = true;
//            }

//            if (!found)
//            {
//                sheet.HasGeometry = false;
//                sheet.BoundsMinX = 0;
//                sheet.BoundsMinY = 0;
//                sheet.BoundsMaxX = 0;
//                sheet.BoundsMaxY = 0;

//                _logger?.LogWarning(
//                    "Sheet {SheetId}: no entity produced a finite bounding box; extents fell back to (0,0,0,0).",
//                    sheet.SheetID);

//                return;
//            }

//            sheet.HasGeometry = true;
//            sheet.BoundsMinX = minX;
//            sheet.BoundsMinY = minY;
//            sheet.BoundsMaxX = maxX;
//            sheet.BoundsMaxY = maxY;
//        }

//        private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

//        private CadDocument LoadDocument(SurveySheet sheet)
//        {
//            var fullPath = System.IO.Path.Combine(_uploadRootPath, sheet.FilePath);
//            if (!System.IO.File.Exists(fullPath))
//                throw new System.IO.FileNotFoundException("DWG/DXF file not found on disk.", fullPath);

//            var ext = System.IO.Path.GetExtension(fullPath).ToLowerInvariant();

//            return ext == ".dxf"
//                ? DxfReader.Read(fullPath)
//                : DwgReader.Read(fullPath);
//        }

//        public async Task<List<RawTextDump>> DumpRawTextAsync(int sheetId)
//        {
//            var sheet = await _sheetRepo.GetByIdAsync(sheetId)
//                ?? throw new InvalidOperationException($"SurveySheet {sheetId} not found.");

//            var document = LoadDocument(sheet);
//            var results = new List<RawTextDump>();

//            foreach (var entity in document.Entities)
//            {
//                string? raw = entity switch
//                {
//                    TextEntity text => text.Value,
//                    MText mtext => mtext.Value,
//                    _ => null
//                };

//                if (string.IsNullOrWhiteSpace(raw))
//                    continue;

//                var (x, y, layer) = entity switch
//                {
//                    TextEntity text => (text.InsertPoint.X, text.InsertPoint.Y, text.Layer?.Name),
//                    MText mtext => (mtext.InsertPoint.X, mtext.InsertPoint.Y, mtext.Layer?.Name),
//                    _ => (0.0, 0.0, (string?)null)
//                };

//                results.Add(new RawTextDump
//                {
//                    EntityType = entity.GetType().Name,
//                    LayerName = layer ?? "",
//                    RawValue = raw,
//                    DecodedValue = DxfUnicodeEscapeDecoder.Decode(raw),
//                    X = x,
//                    Y = y
//                });
//            }

//            return results;
//        }

//        private void ExtractTextCandidate(
//            string? rawText,
//            string? layerName,
//            double x, double y,
//            SurveySheet sheet,
//            List<(string, double, double)> candidates)
//        {
//            if (string.IsNullOrWhiteSpace(rawText))
//                return;

//            if (string.Equals(layerName, AdjacentSheetLayerName, StringComparison.OrdinalIgnoreCase))
//            {
//                var decoded = DxfUnicodeEscapeDecoder.Decode(rawText);
//                var match = AdjacentSheetPattern.Match(decoded);
//                if (match.Success)
//                    sheet.LaghuReferenceNumber = match.Groups[1].Value;

//                return;
//            }

//            // Accept alphanumeric text containing at least one digit, length 2-30.
//            // This allows "Sheet 5", "Plot 12A", "HOUSE-3" etc. to become candidates
//            // and tie-point labels, without which merge and grid placement fail.
//            if (rawText.Any(char.IsDigit) && rawText.Length >= 2 && rawText.Length <= 30)
//                candidates.Add((rawText.Trim(), x, y));
//        }

//        // Extracts the sheet's real number from title-caption text like
//        // "शीट क्र .1)"; returns null for any other text on the layer
//        // (including the plus-shaped index-box's own bare-number cells,
//        // which CollectTitleBlockNumber handles separately).
//        private static string? TryExtractSheetTitleNumber(string? rawText)
//        {
//            if (string.IsNullOrWhiteSpace(rawText))
//                return null;

//            var decoded = DxfUnicodeEscapeDecoder.Decode(rawText);
//            var match = SheetTitleNumberPattern.Match(decoded);
//            return match.Success ? match.Groups[1].Value : null;
//        }

//        // Records a title-block text as a neighbor-number candidate if, once
//        // decoded, it is nothing but digits (e.g. "1", "23"). Title text like
//        // "शीट क्र .1)" or "मौजा - शाहापूर" never matches, so the title/caption
//        // strings around the index box are naturally excluded.
//        private void CollectTitleBlockNumber(
//            string? rawText,
//            double x, double y,
//            List<(string Value, double X, double Y)> titleBlockNumbers)
//        {
//            if (string.IsNullOrWhiteSpace(rawText))
//                return;

//            var decoded = DxfUnicodeEscapeDecoder.Decode(rawText).Trim();
//            if (BareNumberPattern.IsMatch(decoded))
//                titleBlockNumbers.Add((decoded, x, y));
//        }

//        // The title-block index box is a plus/cross layout: the sheet's own
//        // number sits at the center, and up to 4 neighbor sheet numbers sit
//        // directly above/below/left/right of it — one cell is simply absent
//        // (no TEXT entity at all) wherever there is no neighbor (edge of the
//        // village). The "self" cell is identified by matching sheet.SheetNumber;
//        // every other bare-number candidate within IndexBoxProximity of it is
//        // then classified by whichever axis (X or Y) dominates the offset.
//        private void ResolveIndexBoxNeighbors(
//            List<(string Value, double X, double Y)> titleBlockNumbers,
//            SurveySheet sheet)
//        {
//            if (string.IsNullOrWhiteSpace(sheet.SheetNumber))
//                return;

//            var self = titleBlockNumbers.FirstOrDefault(c =>
//                string.Equals(c.Value, sheet.SheetNumber.Trim(), StringComparison.Ordinal));

//            if (self.Value == null)
//            {
//                _logger?.LogInformation(
//                    "Sheet {SheetId}: no title-block index-box cell matched SheetNumber '{SheetNumber}'; " +
//                    "adjacency neighbors were not extracted from the DWG.",
//                    sheet.SheetID, sheet.SheetNumber);
//                return;
//            }

//            (string Value, double Distance)? north = null, south = null, east = null, west = null;

//            foreach (var candidate in titleBlockNumbers)
//            {
//                if (ReferenceEquals(candidate.Value, self.Value) && candidate.X == self.X && candidate.Y == self.Y)
//                    continue;

//                double dx = candidate.X - self.X;
//                double dy = candidate.Y - self.Y;
//                double distance = Math.Sqrt(dx * dx + dy * dy);

//                if (distance > IndexBoxProximity)
//                    continue;

//                if (Math.Abs(dy) >= Math.Abs(dx))
//                {
//                    if (dy > 0 && (north == null || distance < north.Value.Distance))
//                        north = (candidate.Value, distance);
//                    else if (dy < 0 && (south == null || distance < south.Value.Distance))
//                        south = (candidate.Value, distance);
//                }
//                else
//                {
//                    if (dx > 0 && (east == null || distance < east.Value.Distance))
//                        east = (candidate.Value, distance);
//                    else if (dx < 0 && (west == null || distance < west.Value.Distance))
//                        west = (candidate.Value, distance);
//                }
//            }

//            sheet.NeighborSheetNumberNorth = north?.Value;
//            sheet.NeighborSheetNumberSouth = south?.Value;
//            sheet.NeighborSheetNumberEast = east?.Value;
//            sheet.NeighborSheetNumberWest = west?.Value;
//        }

//        // "Shape" check: does the sheet's own boundary (drawn on
//        // SheetBoundaryLayers only — never the print frame, legend, or
//        // individual plot outlines) resolve to a clean rectangle? Used
//        // upstream to gate a sheet out of auto-merge and into manual review
//        // when its extracted boundary is irregular, which usually means the
//        // boundary-polygon extraction picked up the wrong entity.
//        private bool DetermineIsRectangle(List<List<Coordinate>> boundaryRings)
//        {
//            if (boundaryRings.Count == 0)
//                return false;

//            // Judge the largest ring — the sheet's outer boundary — rather
//            // than an arbitrary one, in case more than one shape was found
//            // on a boundary layer.
//            var ring = boundaryRings.OrderByDescending(RingArea).First();

//            var pts = new List<Coordinate>(ring);
//            if (pts.Count > 1 && pts[0].Equals2D(pts[^1]))
//                pts.RemoveAt(pts.Count - 1);

//            if (pts.Count != 4)
//                return false;

//            for (int i = 0; i < pts.Count; i++)
//            {
//                var prev = pts[(i - 1 + pts.Count) % pts.Count];
//                var curr = pts[i];
//                var next = pts[(i + 1) % pts.Count];

//                var v1 = new Coordinate(prev.X - curr.X, prev.Y - curr.Y);
//                var v2 = new Coordinate(next.X - curr.X, next.Y - curr.Y);

//                double dot = v1.X * v2.X + v1.Y * v2.Y;
//                double mag1 = Math.Sqrt(v1.X * v1.X + v1.Y * v1.Y);
//                double mag2 = Math.Sqrt(v2.X * v2.X + v2.Y * v2.Y);

//                if (mag1 == 0 || mag2 == 0)
//                    return false;

//                double cosAngle = Math.Clamp(dot / (mag1 * mag2), -1.0, 1.0);
//                double angleDegrees = Math.Acos(cosAngle) * (180.0 / Math.PI);

//                if (Math.Abs(angleDegrees - 90.0) > RectangleAngleToleranceDegrees)
//                    return false;
//            }

//            return true;
//        }

//        private static double RingArea(List<Coordinate> ring)
//        {
//            double area = 0;
//            for (int i = 0; i < ring.Count; i++)
//            {
//                var a = ring[i];
//                var b = ring[(i + 1) % ring.Count];
//                area += a.X * b.Y - b.X * a.Y;
//            }
//            return Math.Abs(area) / 2.0;
//        }

//        private async Task ProcessBoundaryAsync(IEnumerable<Coordinate> rawCoords, SurveySheet sheet)
//        {
//            var coords = rawCoords.ToList();
//            if (coords.Count < 3)
//            {
//                _skippedShortPolylineCount++;
//                _logger?.LogWarning(
//                    "Sheet {SheetId}: skipped a polyline with only {VertexCount} vertex(es); at least 3 are required to form a boundary.",
//                    sheet.SheetID, coords.Count);
//                return;
//            }

//            if (!coords[0].Equals2D(coords[^1]))
//                coords.Add(coords[0]);

//            var polygon = _geomFactory.CreatePolygon(coords.ToArray());

//            var boundary = new SheetBoundary
//            {
//                SheetID = sheet.SheetID,
//                PlotLabel = null,
//                Geometry = polygon,
//                CreatedDate = DateTime.UtcNow
//            };

//            await _boundaryRepo.AddAsync(boundary);
//        }
//    }
//}