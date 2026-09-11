using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using MapStitcher.Business.Contracts;
using MapStitcher.Database;
using MapStitcher.Repositories.Contracts;
using NetTopologySuite.Geometries;

namespace MapStitcher.Business.Services
{
    public class CadastralParsingService : ICadastralParsingService
    {
        private readonly ISurveySheetRepository _sheetRepo;
        private readonly ITiePointRepository _tiePointRepo;
        private readonly ISheetBoundaryRepository _boundaryRepo;
        private readonly string _uploadRootPath;

        private const double SpatialThreshold = 5.0;

        private static readonly GeometryFactory _geomFactory = new GeometryFactory(new PrecisionModel(), 0);

        public CadastralParsingService(
            ISurveySheetRepository sheetRepo,
            ITiePointRepository tiePointRepo,
            ISheetBoundaryRepository boundaryRepo,
            string uploadRootPath)
        {
            _sheetRepo = sheetRepo;
            _tiePointRepo = tiePointRepo;
            _boundaryRepo = boundaryRepo;
            _uploadRootPath = uploadRootPath;
        }

        public async Task ParseSheetAsync(int sheetId)
        {
            var sheet = await _sheetRepo.GetByIdAsync(sheetId);
            if (sheet == null)
                throw new InvalidOperationException($"SurveySheet {sheetId} not found.");

            var fullPath = System.IO.Path.Combine(_uploadRootPath, sheet.FilePath);
            if (!System.IO.File.Exists(fullPath))
                throw new System.IO.FileNotFoundException("DWG/DXF file not found on disk.", fullPath);

            CadDocument document;
            var ext = System.IO.Path.GetExtension(fullPath).ToLowerInvariant();

            try
            {
                document = ext == ".dxf"
                    ? DxfReader.Read(fullPath)
                    : DwgReader.Read(fullPath);
            }
            catch (Exception ex)
            {
                sheet.Status = SheetStatus.Failed;
                await _sheetRepo.SaveChangesAsync();
                throw new InvalidOperationException($"Failed to parse CAD file: {ex.Message}", ex);
            }

            sheet.DwgVersion = document.Header?.Version.ToString();

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
                            text.InsertPoint.X,
                            text.InsertPoint.Y,
                            sheet,
                            textCandidates);
                        break;

                    case MText mtext:
                        ExtractTextCandidate(
                            mtext.Value,
                            mtext.InsertPoint.X,
                            mtext.InsertPoint.Y,
                            sheet,
                            textCandidates);
                        break;

                    case Insert insert:
                        rawPoints.Add((insert.InsertPoint.X, insert.InsertPoint.Y));
                        break;

                    case LwPolyline lwPoly:
                        await ProcessBoundaryAsync(
                            lwPoly.Vertices.Select(v => new Coordinate(v.Location.X, v.Location.Y)),
                            sheet);
                        break;

                    case Polyline2D poly2d:
                        await ProcessBoundaryAsync(
                            poly2d.Vertices.Select(v => new Coordinate(v.Location.X, v.Location.Y)),
                            sheet);
                        break;
                }
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

        private void ExtractTextCandidate(
            string? rawText,
            double x, double y,
            SurveySheet sheet,
            List<(string, double, double)> candidates)
        {
            if (string.IsNullOrWhiteSpace(rawText))
                return;

            if (rawText.Contains("शिट") || rawText.ToLower().Contains("laghu"))
            {
                var digits = new string(rawText.Where(char.IsDigit).ToArray());
                if (!string.IsNullOrEmpty(digits))
                    sheet.LaghuReferenceNumber = digits;

                return;
            }

            if (rawText.All(char.IsDigit) && rawText.Length <= 10)
                candidates.Add((rawText.Trim(), x, y));
        }

        private async Task ProcessBoundaryAsync(IEnumerable<Coordinate> rawCoords, SurveySheet sheet)
        {
            var coords = rawCoords.ToList();
            if (coords.Count < 3)
                return;

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