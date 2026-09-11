// Services/CadastralMergeService.cs — full file
using MapStitcher.Business.Contracts;
using MapStitcher.Database;
using MapStitcher.Repositories.Contracts;

namespace MapStitcher.Business.Services
{
    // Merge strategy: pure Laghu Reference index chaining.
    // No georeference (TargetX/TargetY) data available; RMS/tie-point spatial
    // validation removed. Two sheets are considered a valid, mergeable match
    // when one sheet's LaghuReferenceNumber equals the other's SheetNumber.
    // Shared tie points (if any) are used only to compute a simple visual
    // translation offset — never as a pass/fail gate.
    public class CadastralMergeService : ICadastralMergeService
    {
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

            if (matchedPairs.Count > 0)
            {
                translateX = matchedPairs.Average(p => p.BasePoint.SourceX - p.AdjacentPoint.SourceX);
                translateY = matchedPairs.Average(p => p.BasePoint.SourceY - p.AdjacentPoint.SourceY);
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

            foreach (var basePoint in basePoints)
            {
                if (string.IsNullOrWhiteSpace(basePoint.PointLabel))
                    continue;

                var match = adjacentPoints.FirstOrDefault(a => a.PointLabel == basePoint.PointLabel);
                if (match != null)
                    pairs.Add((basePoint, match));
            }

            return pairs;
        }
    }
}