//using MapStitcher.Business.Contracts;
//using MapStitcher.Database;
//using MapStitcher.Repositories.Contracts;

//namespace MapStitcher.Business.Services
//{
//    // Preview-only grid layout. Assigns each sheet a GridRow/GridCol and a
//    // translation offset (OffsetX/OffsetY) sized from its own true bounding
//    // box (BoundsMinX/MinY/MaxX/MaxY), so sheets of different sizes never
//    // overlap on the jigsaw board. Persists only to SurveySheet — no file
//    // I/O, no CAD entity access. ExportMasterDxf consumes these offsets later.
//    public class SheetGridArrangementService : ISheetGridArrangementService
//    {
//        private const double DefaultCellSize = 500.0;
//        private const double CellGap = 25.0;

//        private readonly ISurveySheetRepository _sheetRepo;

//        public SheetGridArrangementService(ISurveySheetRepository sheetRepo)
//        {
//            _sheetRepo = sheetRepo;
//        }

//        public async Task<GridArrangementResult> ArrangeSheetsGrid(int projectId, int columnsPerRow)
//        {
//            var result = new GridArrangementResult { ColumnsPerRow = columnsPerRow };

//            if (columnsPerRow <= 0)
//            {
//                result.Message = "columnsPerRow must be greater than zero.";
//                return result;
//            }

//            var sheets = await _sheetRepo.GetByProjectIdAsync(projectId);
//            if (sheets.Count == 0)
//            {
//                result.Message = "No sheets found in this project.";
//                return result;
//            }

//            // Deterministic order so repeated calls produce a stable layout.
//            var ordered = sheets.OrderBy(s => s.SheetNumber, StringComparer.OrdinalIgnoreCase).ToList();

//            int rowCount = (int)Math.Ceiling(ordered.Count / (double)columnsPerRow);
//            var colWidth = new double[columnsPerRow];
//            var rowHeight = new double[rowCount];

//            var cellIndex = new (int Row, int Col, double Width, double Height)[ordered.Count];

//            for (int i = 0; i < ordered.Count; i++)
//            {
//                var sheet = ordered[i];
//                int row = i / columnsPerRow;
//                int col = i % columnsPerRow;

//                double width = sheet.HasGeometry ? Math.Max(sheet.BoundsMaxX - sheet.BoundsMinX, 1.0) : DefaultCellSize;
//                double height = sheet.HasGeometry ? Math.Max(sheet.BoundsMaxY - sheet.BoundsMinY, 1.0) : DefaultCellSize;

//                cellIndex[i] = (row, col, width, height);

//                if (width > colWidth[col]) colWidth[col] = width;
//                if (height > rowHeight[row]) rowHeight[row] = height;
//            }

//            // Cumulative offsets per column/row, including the gap between cells.
//            var colOffset = new double[columnsPerRow];
//            for (int c = 1; c < columnsPerRow; c++)
//                colOffset[c] = colOffset[c - 1] + colWidth[c - 1] + CellGap;

//            var rowOffset = new double[rowCount];
//            for (int r = 1; r < rowCount; r++)
//                rowOffset[r] = rowOffset[r - 1] + rowHeight[r - 1] + CellGap;

//            for (int i = 0; i < ordered.Count; i++)
//            {
//                var sheet = ordered[i];
//                var (row, col, width, height) = cellIndex[i];

//                double targetMinX = colOffset[col];
//                double targetMaxY = -rowOffset[row]; // row increases downward, CAD Y increases upward

//                double offsetX = sheet.HasGeometry ? targetMinX - sheet.BoundsMinX : targetMinX;
//                double offsetY = sheet.HasGeometry ? targetMaxY - sheet.BoundsMaxY : targetMaxY - height;

//                sheet.GridRow = row;
//                sheet.GridCol = col;
//                sheet.OffsetX = offsetX;
//                sheet.OffsetY = offsetY;

//                result.Placements.Add(new SheetGridPlacement
//                {
//                    SheetID = sheet.SheetID,
//                    SheetNumber = sheet.SheetNumber,
//                    GridRow = row,
//                    GridCol = col,
//                    OffsetX = offsetX,
//                    OffsetY = offsetY,
//                    Width = width,
//                    Height = height,
//                    HasGeometry = sheet.HasGeometry
//                });
//            }

//            await _sheetRepo.SaveChangesAsync();

//            result.Success = true;
//            result.TotalSheets = ordered.Count;
//            return result;
//        }
//    }
//}