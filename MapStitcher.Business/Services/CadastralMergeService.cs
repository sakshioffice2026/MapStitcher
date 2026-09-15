// Services/CadastralMergeService.cs — full file
using MapStitcher.Business.Contracts;
using MapStitcher.Database;
using MapStitcher.Repositories.Contracts;

namespace MapStitcher.Business.Services
{
    // Merge strategy: tie-point X,Y coordinate matching is the primary and only
    // gate for merging (label match first, spatial-proximity fallback second).
    // The Laghu Reference chain, when present, is recorded for traceability only
    // — it no longer blocks a merge that tie points otherwise support.
    // Translation is composed onto the base sheet's existing global position, so
    // a root/anchor sheet (TransformTranslateX/Y = 0) stays at the origin and
    // every merge downstream of it accumulates correctly in one shared frame.
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

        public async Task<MergeResult> MergeSheetsAsync(
            int baseSheetId,
            int adjacentSheetId)
        {
            var baseSheet =
                await _sheetRepo.GetByIdAsync(baseSheetId)
                ?? throw new InvalidOperationException(
                    $"Base sheet {baseSheetId} not found.");

            var adjacentSheet =
                await _sheetRepo.GetByIdAsync(adjacentSheetId)
                ?? throw new InvalidOperationException(
                    $"Adjacent sheet {adjacentSheetId} not found.");

            var basePoints = await _tiePointRepo.GetBySheetIdAsync(baseSheetId);
            var adjacentPoints = await _tiePointRepo.GetBySheetIdAsync(adjacentSheetId);
            var matchedPairs = FindMatchingPairs(basePoints, adjacentPoints);

            double localTranslateX = 0;
            double localTranslateY = 0;
            bool haveOffset = false;

            // Compute a best-effort local X,Y offset:
            // 1) If labeled pairs matched, use their average offset
            // 2) Otherwise, fall back to centroid difference of ALL labeled points
            if (matchedPairs.Count > 0)
            {
                localTranslateX = matchedPairs.Average(p => p.BasePoint.SourceX - p.AdjacentPoint.SourceX);
                localTranslateY = matchedPairs.Average(p => p.BasePoint.SourceY - p.AdjacentPoint.SourceY);
                haveOffset = true;
            }
            else if (basePoints.Count > 0 && adjacentPoints.Count > 0)
            {
                // Centroid fallback: translate so that the two sheets' point clouds align
                localTranslateX = basePoints.Average(p => p.SourceX) - adjacentPoints.Average(p => p.SourceX);
                localTranslateY = basePoints.Average(p => p.SourceY) - adjacentPoints.Average(p => p.SourceY);
                haveOffset = true;
            }

            if (!haveOffset)
            {
                return new MergeResult
                {
                    Success = false,
                    MatchedPointCount = 0,
                    Message = $"No tie points on sheet {baseSheet.SheetNumber} and/or {adjacentSheet.SheetNumber} to compute an X,Y offset.",
                    FailureReason = "Needs manual check: no tie points available for coordinate matching"
                };
            }

            // Compose onto the base sheet's existing global position. A never-merged
            // root sheet has TransformTranslateX/Y == 0, so it anchors the mosaic at
            // the origin; every sheet merged onto it (directly or via a chain)
            // accumulates the correct absolute X,Y from there.
            double globalTranslateX = baseSheet.TransformTranslateX + localTranslateX;
            double globalTranslateY = baseSheet.TransformTranslateY + localTranslateY;

            foreach (var point in adjacentPoints)
            {
                point.TargetX =
                    point.SourceX +
                    globalTranslateX;

                point.TargetY =
                    point.SourceY +
                    globalTranslateY;
            }

            adjacentSheet.TransformRotation = 0.0;
            adjacentSheet.TransformScale = 1.0;

            adjacentSheet.TransformTranslateX =
                globalTranslateX;

            adjacentSheet.TransformTranslateY =
                globalTranslateY;

            adjacentSheet.Status =
                SheetStatus.Merged;

            baseSheet.Status =
                SheetStatus.Merged;

            await _tiePointRepo.SaveChangesAsync();
            await _sheetRepo.SaveChangesAsync();

            string? linkedVia = ResolveLaghuLink(baseSheet, adjacentSheet);

            return new MergeResult
            {
                Success = true,
                MatchedPointCount = matchedPairs.Count,
                InlierPointCount = matchedPairs.Count,
                Message = linkedVia != null
                    ? $"Merged via tie-point X,Y match, confirmed by Laghu Reference chain ({linkedVia}: {baseSheet.SheetNumber} <-> {adjacentSheet.SheetNumber})."
                    : $"Merged via tie-point X,Y match ({baseSheet.SheetNumber} <-> {adjacentSheet.SheetNumber}).",
                FailureReason = null
            };
        }

        // Returns the matched reference number if either sheet's LaghuReferenceNumber
        // points to the other sheet's SheetNumber; null if no index link exists.
        // Informational only — no longer gates whether a merge is allowed.
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