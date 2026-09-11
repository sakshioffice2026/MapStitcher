using MapStitcher.Business.Contracts;
using MapStitcher.Database;
using MapStitcher.Repositories.Contracts;

namespace MapStitcher.Business.Services
{
    public class SheetPlacementService : ISheetPlacementService
    {
        private readonly ISurveySheetRepository _sheetRepo;
        private readonly ITiePointRepository _tiePointRepo;

        public SheetPlacementService(
            ISurveySheetRepository sheetRepo,
            ITiePointRepository tiePointRepo)
        {
            _sheetRepo = sheetRepo;
            _tiePointRepo = tiePointRepo;
        }

        // Auto placement: finds grid position by matching TiePoint labels
        // with already-placed sheets in the same project.
        public async Task<PlacementResult> AutoPlaceSheetAsync(int sheetId)
        {
            var sheet = await _sheetRepo.GetByIdAsync(sheetId);
            if (sheet == null)
                return Fail(sheetId, "Sheet not found.");

            var incomingPoints = await _tiePointRepo.GetBySheetIdAsync(sheetId);
            var labeledPoints = incomingPoints.Where(p => !string.IsNullOrWhiteSpace(p.PointLabel)).ToList();

            if (!labeledPoints.Any())
                return Fail(sheetId, "No labeled tie points found. Run parsing first or use manual placement.");

            var placedSheets = await _sheetRepo.GetByProjectIdAsync(sheet.ProjectID);
            var placedAndPositioned = placedSheets
                .Where(s => s.SheetID != sheetId && s.GridRow.HasValue && s.GridCol.HasValue)
                .ToList();

            if (!placedAndPositioned.Any())
            {
                // First sheet in project — anchor at origin (0,0)
                return await CommitPlacement(sheet, 0, 0, PlacementMode.Auto);
            }

            // For each already-placed sheet, check for shared TiePoint labels
            foreach (var placedSheet in placedAndPositioned)
            {
                var placedPoints = await _tiePointRepo.GetBySheetIdAsync(placedSheet.SheetID);
                var sharedLabels = labeledPoints
                    .Select(p => p.PointLabel)
                    .Intersect(placedPoints
                        .Where(p => !string.IsNullOrWhiteSpace(p.PointLabel))
                        .Select(p => p.PointLabel))
                    .ToList();

                if (!sharedLabels.Any())
                    continue;

                // Determine spatial direction based on centroid offset
                double incomingCentroidX = labeledPoints.Average(p => p.SourceX);
                double incomingCentroidY = labeledPoints.Average(p => p.SourceY);

                var sharedPlacedPoints = placedPoints
                    .Where(p => sharedLabels.Contains(p.PointLabel))
                    .ToList();

                double placedCentroidX = sharedPlacedPoints.Average(p => p.SourceX);
                double placedCentroidY = sharedPlacedPoints.Average(p => p.SourceY);

                double dx = incomingCentroidX - placedCentroidX;
                double dy = incomingCentroidY - placedCentroidY;

                int targetRow = placedSheet.GridRow!.Value;
                int targetCol = placedSheet.GridCol!.Value;

                // Determine neighbor direction from centroid delta
                if (Math.Abs(dx) >= Math.Abs(dy))
                    targetCol += dx > 0 ? 1 : -1;  // East or West
                else
                    targetRow += dy > 0 ? -1 : 1;  // North or South (Y-axis inverted for grid)

                // Collision check — if target cell is occupied, skip this neighbor
                bool occupied = placedAndPositioned.Any(s => s.GridRow == targetRow && s.GridCol == targetCol);
                if (occupied)
                    continue;

                return await CommitPlacement(sheet, targetRow, targetCol, PlacementMode.Auto);
            }

            return Fail(sheetId, "Could not determine grid position from shared tie points. Use manual placement.");
        }

        // Returns every valid empty neighbor cell for this sheet, based on shared tie point
        // labels with already-placed sheets. Used by the UI to highlight "Connect Here" cells
        // when a sheet card is selected — no directional (N/S/E/W) language involved.
        public async Task<List<OpenSlot>> GetOpenTargetSlotsAsync(int sheetId)
        {
            var slots = new List<OpenSlot>();

            var sheet = await _sheetRepo.GetByIdAsync(sheetId);
            if (sheet == null)
                return slots;

            var incomingPoints = await _tiePointRepo.GetBySheetIdAsync(sheetId);
            var labeledPoints = incomingPoints.Where(p => !string.IsNullOrWhiteSpace(p.PointLabel)).ToList();
            if (!labeledPoints.Any())
                return slots;

            var placedSheets = (await _sheetRepo.GetByProjectIdAsync(sheet.ProjectID))
                .Where(s => s.SheetID != sheetId && s.GridRow.HasValue && s.GridCol.HasValue)
                .ToList();

            if (!placedSheets.Any())
                return slots; // Nothing placed yet — this would anchor at (0,0), not a "connect" case.

            foreach (var placedSheet in placedSheets)
            {
                var placedPoints = await _tiePointRepo.GetBySheetIdAsync(placedSheet.SheetID);
                var sharedLabels = labeledPoints
                    .Select(p => p.PointLabel)
                    .Intersect(placedPoints
                        .Where(p => !string.IsNullOrWhiteSpace(p.PointLabel))
                        .Select(p => p.PointLabel))
                    .ToList();

                if (!sharedLabels.Any())
                    continue;

                double incomingCentroidX = labeledPoints.Average(p => p.SourceX);
                double incomingCentroidY = labeledPoints.Average(p => p.SourceY);

                var sharedPlacedPoints = placedPoints
                    .Where(p => sharedLabels.Contains(p.PointLabel))
                    .ToList();

                double placedCentroidX = sharedPlacedPoints.Average(p => p.SourceX);
                double placedCentroidY = sharedPlacedPoints.Average(p => p.SourceY);

                double dx = incomingCentroidX - placedCentroidX;
                double dy = incomingCentroidY - placedCentroidY;

                int targetRow = placedSheet.GridRow!.Value;
                int targetCol = placedSheet.GridCol!.Value;

                if (Math.Abs(dx) >= Math.Abs(dy))
                    targetCol += dx > 0 ? 1 : -1;
                else
                    targetRow += dy > 0 ? -1 : 1;

                bool occupied = placedSheets.Any(s => s.GridRow == targetRow && s.GridCol == targetCol);
                bool alreadyListed = slots.Any(s => s.GridRow == targetRow && s.GridCol == targetCol);

                if (!occupied && !alreadyListed)
                    slots.Add(new OpenSlot { GridRow = targetRow, GridCol = targetCol });
            }

            return slots;
        }

        // Manual placement: user explicitly assigns grid row/col from the jigsaw board UI.
        public async Task<PlacementResult> ManualPlaceSheetAsync(int sheetId, int gridRow, int gridCol)
        {
            var sheet = await _sheetRepo.GetByIdAsync(sheetId);
            if (sheet == null)
                return Fail(sheetId, "Sheet not found.");

            var siblings = await _sheetRepo.GetByProjectIdAsync(sheet.ProjectID);
            bool occupied = siblings.Any(s => s.SheetID != sheetId && s.GridRow == gridRow && s.GridCol == gridCol);

            if (occupied)
                return Fail(sheetId, $"Grid cell ({gridRow},{gridCol}) is already occupied by another sheet.");

            return await CommitPlacement(sheet, gridRow, gridCol, PlacementMode.Manual);
        }

        private async Task<PlacementResult> CommitPlacement(SurveySheet sheet, int row, int col, PlacementMode mode)
        {
            sheet.GridRow = row;
            sheet.GridCol = col;
            sheet.Status = SheetStatus.Parsed;

            await _sheetRepo.SaveChangesAsync();

            return new PlacementResult
            {
                Success = true,
                SheetID = sheet.SheetID,
                GridRow = row,
                GridCol = col,
                Mode = mode,
                Message = $"Sheet placed at ({row},{col}) via {mode}."
            };
        }

        private static PlacementResult Fail(int sheetId, string message) => new PlacementResult
        {
            Success = false,
            SheetID = sheetId,
            Message = message
        };
    }
}