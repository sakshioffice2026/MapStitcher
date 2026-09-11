using MapStitcher.Business.Contracts;
using MapStitcher.Database;
using MapStitcher.Repositories.Contracts;

namespace MapStitcher.Business.Services
{
    // Placement strategy: prefer Laghu Reference index chaining for consistent
    // grid adjacency, but fall back to centroid-offset spatial placement when no
    // Laghu link exists. Offsets follow compass order (E, S, W, N) with the
    // incoming sheet's centroid delta determining preferred direction.
    public class SheetPlacementService : ISheetPlacementService
    {
        private static readonly (int dRow, int dCol)[] AdjacencyOffsets =
        {
            (0, 1),   // East
            (1, 0),   // South
            (0, -1),  // West
            (-1, 0),  // North
        };

        private static readonly (int dRow, int dCol)[] CentroidOffsets =
        {
            (0, 1),   // East  — dx dominates, dx > 0
            (0, -1),  // West  — dx dominates, dx < 0
            (1, 0),   // South — dy dominates, dy > 0
            (-1, 0),  // North — dy dominates, dy < 0
        };

        private readonly ISurveySheetRepository _sheetRepo;
        private readonly ITiePointRepository _tiePointRepo;

        public SheetPlacementService(ISurveySheetRepository sheetRepo, ITiePointRepository tiePointRepo)
        {
            _sheetRepo = sheetRepo;
            _tiePointRepo = tiePointRepo;
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

            // 1) Try Laghu Reference link first
            var linkedSheet = FindLaghuLinkedSheet(sheet, placedSheets);
            if (linkedSheet != null)
            {
                var openCell = FindFirstOpenAdjacentCell(linkedSheet, placedSheets);
                if (openCell != null)
                    return await CommitPlacement(sheet, openCell.Value.row, openCell.Value.col, PlacementMode.Auto);
            }

            // 2) Fallback: centroid-offset spatial placement when no Laghu link exists
            var placement = TryCentroidPlacement(sheet, placedSheets);
            if (placement != null)
                return placement;

            // 3) Last resort: find any empty cell in a simple row-major sweep
            for (int r = 0; r < 20; r++)
            {
                for (int c = 0; c < 20; c++)
                {
                    if (!placedSheets.Any(s => s.GridRow == r && s.GridCol == c))
                        return await CommitPlacement(sheet, r, c, PlacementMode.Auto);
                }
            }

            return Fail(sheetId, "Could not find an empty grid cell. Increase grid size or use manual placement.");
        }

        private static PlacementResult? TryCentroidPlacement(SurveySheet sheet, List<SurveySheet> placedSheets)
        {
            var incomingPoints = await _tiePointRepo.GetBySheetIdAsync(sheet.SheetID);
            var labeledPoints = incomingPoints.Where(p => !string.IsNullOrWhiteSpace(p.PointLabel)).ToList();
            if (!labeledPoints.Any())
                return null;

            var incomingCentroidX = labeledPoints.Average(p => p.SourceX);
            var incomingCentroidY = labeledPoints.Average(p => p.SourceY);

            foreach (var placedSheet in placedSheets)
            {
                var placedPoints = await _tiePointRepo.GetBySheetIdAsync(placedSheet.SheetID);
                var placedLabeled = placedPoints.Where(p => !string.IsNullOrWhiteSpace(p.PointLabel)).ToList();
                if (!placedLabeled.Any())
                    continue;

                var sharedLabels = labeledPoints
                    .Select(p => p.PointLabel)
                    .Intersect(placedLabeled.Select(p => p.PointLabel))
                    .ToList();

                if (!sharedLabels.Any())
                    continue;

                var sharedIncoming = labeledPoints.Where(p => sharedLabels.Contains(p.PointLabel)).ToList();
                var sharedPlaced = placedPoints.Where(p => sharedLabels.Contains(p.PointLabel)).ToList();

                double placedCentroidX = sharedPlaced.Average(p => p.SourceX);
                double placedCentroidY = sharedPlaced.Average(p => p.SourceY);

                double dx = incomingCentroidX - placedCentroidX;
                double dy = incomingCentroidY - placedCentroidY;

                // Determine preferred direction from centroid delta
                (int dRow, int dCol) preferred;
                if (Math.Abs(dx) >= Math.Abs(dy))
                    preferred = dx > 0 ? (0, -1) : (0, 1);  // West or East
                else
                    preferred = dy > 0 ? (1, 0) : (-1, 0);  // South or North

                // Try preferred direction, then alternate
                foreach (var (tdRow, tdCol) in new[] { preferred, (0, 1), (0, -1), (1, 0), (-1, 0) })
                {
                    int targetRow = placedSheet.GridRow!.Value + tdRow;
                    int targetCol = placedSheet.GridCol!.Value + tdCol;

                    if (placedSheets.Any(s => s.GridRow == targetRow && s.GridCol == targetCol))
                        continue;

                    return await CommitPlacement(sheet, targetRow, targetCol, PlacementMode.Auto);
                }
            }

            return null;
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
                return slots; // Nothing placed yet — would anchor at (0,0), no "connect" slots

            var incomingPoints = await _tiePointRepo.GetBySheetIdAsync(sheet.SheetID);
            var labeledPoints = incomingPoints.Where(p => !string.IsNullOrWhiteSpace(p.PointLabel)).ToList();
            if (!labeledPoints.Any())
                return slots;

            foreach (var placedSheet in placedSheets)
            {
                var placedPoints = await _tiePointRepo.GetBySheetIdAsync(placedSheet.SheetID);
                var placedLabeled = placedPoints.Where(p => !string.IsNullOrWhiteSpace(p.PointLabel)).ToList();
                if (!placedLabeled.Any())
                    continue;

                var sharedLabels = labeledPoints
                    .Select(p => p.PointLabel)
                    .Intersect(placedLabeled.Select(p => p.PointLabel))
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

                // Determine preferred direction from centroid delta
                var preferred = Math.Abs(dx) >= Math.Abs(dy)
                    ? (dx > 0 ? (0, -1) : (0, 1))  // West or East
                    : (dy > 0 ? (1, 0) : (-1, 0));  // South or North

                // Add slots in preferred direction, then alternate
                foreach (var (tdRow, tdCol) in new[] { preferred, (0, 1), (0, -1), (1, 0), (-1, 0) })
                {
                    int targetRow = placedSheet.GridRow!.Value + tdRow;
                    int targetCol = placedSheet.GridCol!.Value + tdCol;

                    bool occupied = placedSheets.Any(s => s.GridRow == targetRow && s.GridCol == targetCol);
                    bool alreadyListed = slots.Any(s => s.GridRow == targetRow && s.GridCol == targetCol);

                    if (!occupied && !alreadyListed)
                        slots.Add(new OpenSlot { GridRow = targetRow, GridCol = targetCol });
                }
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