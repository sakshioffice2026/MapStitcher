// Services/CadastralMergeService.cs — full file
using MapStitcher.Business.Contracts;
using MapStitcher.Database;
using MapStitcher.Repositories.Contracts;

namespace MapStitcher.Business.Services
{
    // Merge strategy: pure Laghu Reference index chaining for the link,
// with tie-point label matching as the primary pairing method, and a
// centroid-offset spatial fallback when no labeled pairs are found.
// This ensures at least a best-effort translation is always computed,
// preventing sheets from keeping identity transforms and collapsing at origin.
    public class CadastralMergeService : ICadastralMergeService
    {
        private const double SpatialThreshold = 25.0; // pixels — used for centroid fallback matching

        private readonly ITiePointRepository _tiePointRepo;
        private readonly ISurveySheetRepository _sheetRepo;

        public CadastralMergeService(
            ITiePointRepository tiePointRepo,
            ISurveySheetRepository sheetRepo)
        {
            _tiePointRepo = tiePointRepo;
            _sheetRepo = sheetRepo;
        }

        public async Task<MergeResult> MergeSheetsAsync(int baseSheetId, int adjacentSheetId)
        {
            var baseSheet = await _sheetRepo.GetByIdAsync(baseSheetId)
                ?? throw new InvalidOperationException($"Base sheet {baseSheetId} not found.");

            var adjacentSheet = await _sheetRepo.GetByIdAsync(adjacentSheetId)
                ?? throw new InvalidOperationException($"Adjacent sheet {adjacentSheetId} not found.");

            string? linkedVia = ResolveLaghuLink(baseSheet, adjacentSheet);

            if (linkedVia == null)
            {
                return new MergeResult
                {
                    Success = false,
                    MatchedPointCount = 0,
                    Message = $"No Laghu Reference match: base='{baseSheet.LaghuReferenceNumber}', adjacent='{adjacentSheet.LaghuReferenceNumber}'.",
                    FailureReason = "Needs manual check: Laghu Sheet No. does not match adjacent sheet"
                };
            }

            var basePoints = await _tiePointRepo.GetBySheetIdAsync(baseSheetId);
            var adjacentPoints = await _tiePointRepo.GetBySheetIdAsync(adjacentSheetId);
            var matchedPairs = FindMatchingPairs(basePoints, adjacentPoints);

            double translateX = 0;
            double translateY = 0;

            // Always compute a best-effort translation:
            // 1) If labeled pairs matched, use their average offset
            // 2) Otherwise, fall back to centroid difference of ALL labeled points
            if (matchedPairs.Count > 0)
            {
                translateX = matchedPairs.Average(p => p.BasePoint.SourceX - p.AdjacentPoint.SourceX);
                translateY = matchedPairs.Average(p => p.BasePoint.SourceY - p.AdjacentPoint.SourceY);
            }
            else if (basePoints.Count > 0 && adjacentPoints.Count > 0)
            {
                // Centroid fallback: translate so that the two sheets' point clouds align
                translateX = basePoints.Average(p => p.SourceX) - adjacentPoints.Average(p => p.SourceX);
                translateY = basePoints.Average(p => p.SourceY) - adjacentPoints.Average(p => p.SourceY);
            }

            foreach (var point in adjacentPoints)
            {
                point.TargetX = point.SourceX + translateX;
                point.TargetY = point.SourceY + translateY;
            }

            adjacentSheet.TransformRotation = 0.0;
            adjacentSheet.TransformScale = 1.0;
            adjacentSheet.TransformTranslateX = translateX;
            adjacentSheet.TransformTranslateY = translateY;

            adjacentSheet.Status = SheetStatus.Merged;
            baseSheet.Status = SheetStatus.Merged;

            await _tiePointRepo.SaveChangesAsync();
            await _sheetRepo.SaveChangesAsync();

            return new MergeResult
            {
                Success = true,
                MatchedPointCount = matchedPairs.Count,
                InlierPointCount = matchedPairs.Count,
                Message = $"Merged via Laghu Reference chain ({linkedVia}: {baseSheet.SheetNumber} <-> {adjacentSheet.SheetNumber}).",
                FailureReason = null
            };
        }

        // Returns the matched reference number if either sheet's LaghuReferenceNumber
        // points to the other sheet's SheetNumber; null if no index link exists.
        private static string? ResolveLaghuLink(SurveySheet baseSheet, SurveySheet adjacentSheet)
        {
            if (!string.IsNullOrWhiteSpace(adjacentSheet.LaghuReferenceNumber) &&
                adjacentSheet.LaghuReferenceNumber == baseSheet.SheetNumber)
                return adjacentSheet.LaghuReferenceNumber;

            if (!string.IsNullOrWhiteSpace(baseSheet.LaghuReferenceNumber) &&
                baseSheet.LaghuReferenceNumber == adjacentSheet.SheetNumber)
                return baseSheet.LaghuReferenceNumber;

            return null;
        }

        private static List<(TiePoint BasePoint, TiePoint AdjacentPoint)> FindMatchingPairs(
            List<TiePoint> basePoints, List<TiePoint> adjacentPoints)
        {
            var pairs = new List<(TiePoint, TiePoint)>();

            // 1) Primary: exact PointLabel matching
            foreach (var basePoint in basePoints)
            {
                if (string.IsNullOrWhiteSpace(basePoint.PointLabel))
                    continue;

                var match = adjacentPoints.FirstOrDefault(a => a.PointLabel == basePoint.PointLabel);
                if (match != null)
                    pairs.Add((basePoint, match));
            }

            // 2) Spatial fallback: if no labeled pairs matched, match by closest
            // centroid proximity using the spatial threshold. This ensures at least
            // some translation is computed even when PointLabels are missing or mismatched.
            if (pairs.Count == 0)
            {
                var labeledBase = basePoints.Where(p => !string.IsNullOrWhiteSpace(p.PointLabel)).ToList();
                var labeledAdjacent = adjacentPoints.Where(p => !string.IsNullOrWhiteSpace(p.PointLabel)).ToList();

                if (labeledBase.Any() && labeledAdjacent.Any())
                {
                    // Try matching each base point to its closest adjacent point within threshold
                    foreach (var basePoint in labeledBase)
                    {
                        var bestMatch = labeledAdjacent
                            .OrderBy(a => Math.Sqrt(
                                Math.Pow(a.SourceX - basePoint.SourceX, 2) +
                                Math.Pow(a.SourceY - basePoint.SourceY, 2)))
                            .FirstOrDefault();

                        if (bestMatch != null &&
                            Math.Sqrt(
                                Math.Pow(bestMatch.SourceX - basePoint.SourceX, 2) +
                                Math.Pow(bestMatch.SourceY - basePoint.SourceY, 2)) <= SpatialThreshold)
                        {
                            pairs.Add((basePoint, bestMatch));
                        }
                    }
                }
            }

            return pairs;
        }
    }
}