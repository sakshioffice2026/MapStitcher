using MapStitcher.Business.Contracts;
using MapStitcher.Database;
using MapStitcher.Repositories.Contracts;

namespace MapStitcher.Business.Services
{
    public class StitchOrchestrationService : IStitchOrchestrationService
    {
        private readonly ISurveySheetRepository _sheetRepo;
        private readonly ISheetPlacementService _placementService;
        private readonly ICadastralMergeService _mergeService;

        public StitchOrchestrationService(
            ISurveySheetRepository sheetRepo,
            ISheetPlacementService placementService,
            ICadastralMergeService mergeService)
        {
            _sheetRepo = sheetRepo;
            _placementService = placementService;
            _mergeService = mergeService;
        }

        public async Task<StitchResult> StitchAllAsync(int projectId)
        {
            var sheets = await _sheetRepo.GetByProjectIdAsync(projectId);
            var result = new StitchResult { TotalSheets = sheets.Count };

            await PlaceAllPossibleAsync(sheets);
            await MergeAllPlacedNeighborsAsync(sheets, result);

            result.PlacedCount = sheets.Count(s => s.GridRow.HasValue && s.GridCol.HasValue);
            result.MergedCount = sheets.Count(s => s.Status == SheetStatus.Merged);
            result.NeedsManualCheckCount = result.Outcomes.Count(o => o.FailureReason != null);

            return result;
        }

        // Multiple passes: a sheet may only become placeable once a neighbor sheet
        // (that shares tie points with it) has itself been placed in this same run.
        private async Task PlaceAllPossibleAsync(List<SurveySheet> sheets)
        {
            bool progress = true;
            while (progress)
            {
                progress = false;

                foreach (var sheet in sheets.Where(s => !s.GridRow.HasValue || !s.GridCol.HasValue))
                {
                    var placement = await _placementService.AutoPlaceSheetAsync(sheet.SheetID);
                    if (placement.Success)
                        progress = true;
                }
            }
        }

        private async Task MergeAllPlacedNeighborsAsync(List<SurveySheet> sheets, StitchResult result)
        {
            var placedSheets = sheets.Where(s => s.GridRow.HasValue && s.GridCol.HasValue).ToList();

            foreach (var sheet in placedSheets)
            {
                if (sheet.Status == SheetStatus.Merged)
                    continue;

                var neighbor = FindPlacedGridNeighbor(sheet, placedSheets);

                var outcome = new SheetStitchOutcome
                {
                    SheetID = sheet.SheetID,
                    SheetNumber = sheet.SheetNumber,
                    Placed = true
                };

                if (neighbor == null)
                {
                    // No uploaded sheet occupies an adjacent cell yet — this is a gap, not a failure.
                    outcome.FailureReason = "Waiting on neighboring sheet upload";
                    result.Outcomes.Add(outcome);
                    continue;
                }

                var mergeResult = await _mergeService.MergeSheetsAsync(neighbor.SheetID, sheet.SheetID);
                outcome.Merged = mergeResult.Success;
                outcome.FailureReason = mergeResult.FailureReason;
                result.Outcomes.Add(outcome);
            }
        }

        private static SurveySheet? FindPlacedGridNeighbor(SurveySheet sheet, List<SurveySheet> placedSheets)
        {
            return placedSheets.FirstOrDefault(n =>
                n.SheetID != sheet.SheetID &&
                ((n.GridRow == sheet.GridRow && Math.Abs(n.GridCol!.Value - sheet.GridCol!.Value) == 1) ||
                 (n.GridCol == sheet.GridCol && Math.Abs(n.GridRow!.Value - sheet.GridRow!.Value) == 1)));
        }
    }
}