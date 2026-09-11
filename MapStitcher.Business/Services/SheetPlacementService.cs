using MapStitcher.Business.Contracts;
using MapStitcher.Database;
using MapStitcher.Repositories.Contracts;

namespace MapStitcher.Business.Services
{
    // Placement strategy: pure Laghu Reference index chaining.
    // No reliable tie-point labels or georeference data are available, so grid
    // adjacency is derived solely from LaghuReferenceNumber <-> SheetNumber links.
    // A linked sheet is dropped into the first open cell (E, S, W, N order)
    // around its linked, already-placed neighbor — direction has no real-world
    // meaning here since no spatial source data exists; it only keeps sheets
    // visually adjacent on the jigsaw grid.
    public class SheetPlacementService : ISheetPlacementService
    {
        private static readonly (int dRow, int dCol)[] AdjacencyOffsets =
        {
            (0, 1),   // East
            (1, 0),   // South
            (0, -1),  // West
            (-1, 0),  // North
        };

        private readonly ISurveySheetRepository _sheetRepo;

        public SheetPlacementService(ISurveySheetRepository sheetRepo)
        {
            _sheetRepo = sheetRepo;
        }

        public async Task<PlacementResult> AutoPlaceSheetAsync(int sheetId)
        {
            var sheet = await _sheetRepo.GetByIdAsync(sheetId);
            if (sheet == null)
                return Fail(sheetId, "Sheet not found.");

            var siblings = await _sheetRepo.GetByProjectIdAsync(sheet.ProjectID);
            var placedSheets = siblings
                .Where(s => s.SheetID != sheetId && s.GridRow.HasValue && s.GridCol.HasValue)
                .ToList();

            if (!placedSheets.Any())
            {
                // First sheet in project — anchor at origin (0,0)
                return await CommitPlacement(sheet, 0, 0, PlacementMode.Auto);
            }

            var linkedSheet = FindLaghuLinkedSheet(sheet, placedSheets);
            if (linkedSheet == null)
                return Fail(sheetId, "No Laghu Reference link found to any placed sheet. Use manual placement.");

            var openCell = FindFirstOpenAdjacentCell(linkedSheet, placedSheets);
            if (openCell == null)
                return Fail(sheetId, $"All grid cells around linked sheet {linkedSheet.SheetNumber} are occupied. Use manual placement.");

            return await CommitPlacement(sheet, openCell.Value.row, openCell.Value.col, PlacementMode.Auto);
        }

        // Returns every open grid cell adjacent to a Laghu-linked, already-placed
        // sheet, for "Connect Here" highlighting in the UI.
        public async Task<List<OpenSlot>> GetOpenTargetSlotsAsync(int sheetId)
        {
            var slots = new List<OpenSlot>();

            var sheet = await _sheetRepo.GetByIdAsync(sheetId);
            if (sheet == null)
                return slots;

            var placedSheets = (await _sheetRepo.GetByProjectIdAsync(sheet.ProjectID))
                .Where(s => s.SheetID != sheetId && s.GridRow.HasValue && s.GridCol.HasValue)
                .ToList();

            if (!placedSheets.Any())
                return slots; // Nothing placed yet — this would anchor at (0,0), not a "connect" case.

            var linkedSheet = FindLaghuLinkedSheet(sheet, placedSheets);
            if (linkedSheet == null)
                return slots;

            foreach (var (dRow, dCol) in AdjacencyOffsets)
            {
                int targetRow = linkedSheet.GridRow!.Value + dRow;
                int targetCol = linkedSheet.GridCol!.Value + dCol;

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

        // A link exists when either sheet's LaghuReferenceNumber equals the other's SheetNumber.
        private static SurveySheet? FindLaghuLinkedSheet(SurveySheet sheet, List<SurveySheet> placedSheets)
        {
            return placedSheets.FirstOrDefault(placed =>
                (!string.IsNullOrWhiteSpace(sheet.LaghuReferenceNumber) && sheet.LaghuReferenceNumber == placed.SheetNumber) ||
                (!string.IsNullOrWhiteSpace(placed.LaghuReferenceNumber) && placed.LaghuReferenceNumber == sheet.SheetNumber));
        }

        private static (int row, int col)? FindFirstOpenAdjacentCell(SurveySheet anchor, List<SurveySheet> placedSheets)
        {
            foreach (var (dRow, dCol) in AdjacencyOffsets)
            {
                int row = anchor.GridRow!.Value + dRow;
                int col = anchor.GridCol!.Value + dCol;

                bool occupied = placedSheets.Any(s => s.GridRow == row && s.GridCol == col);
                if (!occupied)
                    return (row, col);
            }

            return null;
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