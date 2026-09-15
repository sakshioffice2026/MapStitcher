using MapStitcher.Business.Contracts;
using MapStitcher.Database;
using MapStitcher.Repositories.Contracts;

namespace MapStitcher.Business.Services
{
    public class StitchOrchestrationService
        : IStitchOrchestrationService
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

        public async Task<StitchResult> StitchAllAsync(
            int projectId)
        {
            var sheets =
                await _sheetRepo.GetByProjectIdAsync(
                    projectId);

            var result =
                new StitchResult
                {
                    TotalSheets = sheets.Count
                };

            await PlaceAllPossibleAsync(
                sheets);

            await MergeAllPlacedNeighborsAsync(
                sheets,
                result);

            result.PlacedCount =
                sheets.Count(
                    s =>
                        s.GridRow.HasValue &&
                        s.GridCol.HasValue);

            result.MergedCount =
                sheets.Count(
                    s =>
                        s.Status ==
                        SheetStatus.Merged);

            result.NeedsManualCheckCount =
                result.Outcomes.Count(
                    o =>
                        o.FailureReason != null);

            return result;
        }

        private async Task PlaceAllPossibleAsync(
            List<SurveySheet> sheets)
        {
            var maxPasses =
                Math.Max(
                    sheets.Count * 2,
                    1);

            for (var pass = 0;
                 pass < maxPasses;
                 pass++)
            {
                var progress = false;

                foreach (
                    var sheet in sheets.Where(
                        s =>
                            !s.GridRow.HasValue ||
                            !s.GridCol.HasValue))
                {
                    var placement =
                        await _placementService
                            .AutoPlaceSheetAsync(
                                sheet.SheetID);

                    if (placement.Success)
                        progress = true;
                }

                if (!progress)
                    break;
            }
        }

        private async Task MergeAllPlacedNeighborsAsync(
            List<SurveySheet> sheets,
            StitchResult result)
        {
            var placedSheets =
                sheets
                    .Where(
                        s =>
                            s.GridRow.HasValue &&
                            s.GridCol.HasValue)
                    .ToList();

            var processed =
                new HashSet<string>();

            var maxPasses =
                Math.Max(
                    placedSheets.Count * 2,
                    1);

            for (var pass = 0;
                 pass < maxPasses;
                 pass++)
            {
                var progress = false;

                foreach (var sheet in placedSheets)
                {
                    var neighbor =
                        FindBestPlacedGridNeighbor(
                            sheet,
                            placedSheets);

                    if (neighbor == null)
                        continue;

                    var pairKey =
                        CreatePairKey(
                            neighbor.SheetID,
                            sheet.SheetID);

                    if (processed.Contains(pairKey))
                        continue;

                    processed.Add(pairKey);

                    /*
                     * The merge service composes the base sheet's global
                     * translation with the adjacent sheet's local
                     * translation.
                     */
                    var mergeResult =
                        await _mergeService
                            .MergeSheetsAsync(
                                neighbor.SheetID,
                                sheet.SheetID);

                    var outcome =
                        new SheetStitchOutcome
                        {
                            SheetID =
                                sheet.SheetID,

                            SheetNumber =
                                sheet.SheetNumber,

                            Placed = true,

                            Merged =
                                mergeResult.Success,

                            FailureReason =
                                mergeResult.FailureReason
                        };

                    result.Outcomes.Add(
                        outcome);

                    if (mergeResult.Success)
                        progress = true;
                }

                if (!progress)
                    break;
            }

            /*
             * Add outcomes for sheets that have no currently available
             * grid neighbor.
             */
            foreach (var sheet in placedSheets)
            {
                var alreadyReported =
                    result.Outcomes.Any(
                        o =>
                            o.SheetID ==
                            sheet.SheetID);

                if (alreadyReported)
                    continue;

                var neighbor =
                    FindBestPlacedGridNeighbor(
                        sheet,
                        placedSheets);

                if (neighbor == null)
                {
                    result.Outcomes.Add(
                        new SheetStitchOutcome
                        {
                            SheetID =
                                sheet.SheetID,

                            SheetNumber =
                                sheet.SheetNumber,

                            Placed = true,

                            Merged = false,

                            FailureReason =
                                "Waiting on neighboring sheet upload"
                        });
                }
            }
        }

        private static SurveySheet?
            FindBestPlacedGridNeighbor(
                SurveySheet sheet,
                List<SurveySheet> placedSheets)
        {
            var neighbors =
                placedSheets
                    .Where(
                        n =>
                            n.SheetID != sheet.SheetID &&
                            IsAdjacent(
                                sheet,
                                n))
                    .ToList();

            if (neighbors.Count == 0)
                return null;

            /*
             * Prefer an already merged/global sheet as the base because
             * its TransformTranslateX/Y already represents the global
             * coordinate system.
             */
            var mergedNeighbor =
                neighbors
                    .Where(
                        n =>
                            n.Status ==
                            SheetStatus.Merged)
                    .OrderByDescending(
                        n =>
                            n.TransformTranslateX *
                            n.TransformTranslateX +
                            n.TransformTranslateY *
                            n.TransformTranslateY)
                    .FirstOrDefault();

            if (mergedNeighbor != null)
                return mergedNeighbor;

            return neighbors.First();
        }

        private static bool IsAdjacent(
            SurveySheet a,
            SurveySheet b)
        {
            if (!a.GridRow.HasValue ||
                !a.GridCol.HasValue ||
                !b.GridRow.HasValue ||
                !b.GridCol.HasValue)
            {
                return false;
            }

            var rowDifference =
                Math.Abs(
                    a.GridRow.Value -
                    b.GridRow.Value);

            var colDifference =
                Math.Abs(
                    a.GridCol.Value -
                    b.GridCol.Value);

            return
                rowDifference + colDifference ==
                1;
        }

        private static string CreatePairKey(
            int first,
            int second)
        {
            var min =
                Math.Min(first, second);

            var max =
                Math.Max(first, second);

            return $"{min}:{max}";
        }
    }
}