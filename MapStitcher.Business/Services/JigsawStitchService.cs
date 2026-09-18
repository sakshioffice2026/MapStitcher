//using ACadSharp;
//using ACadSharp.Entities;
//using ACadSharp.IO;
//using CSMath;
//using MapStitcher.Business.Contracts;
//using MapStitcher.Repositories.Contracts;

//namespace MapStitcher.Business.Services
//{
//    public class JigsawStitchService : IJigsawStitchService
//    {
//        private readonly ISurveySheetRepository _sheetRepo;
//        private readonly IJigsawIndexExtractionService _indexService;
//        private readonly string _uploadRootPath;

//        private const double SheetWidth = 500.0;
//        private const double SheetHeight = 500.0;

//        public JigsawStitchService(
//            ISurveySheetRepository sheetRepo,
//            IJigsawIndexExtractionService indexService,
//            string uploadRootPath)
//        {
//            _sheetRepo = sheetRepo;
//            _indexService = indexService;
//            _uploadRootPath = uploadRootPath;
//        }

//        public async Task<JigsawMergeResult> MergeSheetsAsync(
//            int projectId,
//            int columnsPerRow,
//            string outputRootPath)
//        {
//            var result = new JigsawMergeResult();

//            if (columnsPerRow <= 0)
//            {
//                result.Errors.Add("columnsPerRow must be greater than zero.");
//                return result;
//            }

//            var sheets = await _sheetRepo.GetByProjectIdAsync(projectId);
//            if (sheets.Count == 0)
//            {
//                result.Errors.Add("No sheets found in this project.");
//                return result;
//            }

//            // --- 1) Metadata & Index Extraction ---
//            var indexInfos = new List<SheetIndexInfo>();
//            foreach (var sheet in sheets)
//            {
//                try
//                {
//                    var info = await _indexService.ExtractAsync(sheet.SheetID);
//                    indexInfos.Add(info);
//                }
//                catch (Exception ex)
//                {
//                    result.Errors.Add($"Sheet {sheet.SheetNumber}: index extraction failed ({ex.Message}).");
//                }
//            }

//            var resolvable = indexInfos.Where(i => i.Extracted).ToList();
//            if (resolvable.Count == 0)
//            {
//                result.Errors.Add("No sheet contained a recognizable index/row-col marker.");
//                return result;
//            }

//            // --- 2) Virtual Grid Matrix Allocation ---
//            var placed = new Dictionary<(int Row, int Col), SheetIndexInfo>();

//            foreach (var info in resolvable)
//            {
//                int row, col;

//                if (info.ExplicitRow.HasValue && info.ExplicitCol.HasValue)
//                {
//                    row = info.ExplicitRow.Value;
//                    col = info.ExplicitCol.Value;
//                }
//                else
//                {
//                    var zeroBased = info.SheetIndexNumber!.Value - 1;
//                    row = zeroBased / columnsPerRow;
//                    col = zeroBased % columnsPerRow;
//                }

//                placed[(row, col)] = info;
//            }

//            int minRow = placed.Keys.Min(k => k.Row);
//            int maxRow = placed.Keys.Max(k => k.Row);
//            int minCol = placed.Keys.Min(k => k.Col);
//            int maxCol = placed.Keys.Max(k => k.Col);

//            // --- 3) Handling Missing Sheets (Gap Preservation) ---
//            for (int row = minRow; row <= maxRow; row++)
//            {
//                for (int col = minCol; col <= maxCol; col++)
//                {
//                    placed.TryGetValue((row, col), out var info);

//                    result.Grid.Add(new JigsawSlot
//                    {
//                        Row = row,
//                        Col = col,
//                        SheetID = info?.SheetID,
//                        SheetNumber = info?.SheetNumber,
//                        IsMissing = info == null
//                    });
//                }
//            }

//            result.TotalSlots = result.Grid.Count;
//            result.PlacedCount = result.Grid.Count(s => !s.IsMissing);
//            result.MissingCount = result.Grid.Count(s => s.IsMissing);

//            // --- 4) Full Entity Cloning & Transformation ---
//            var masterDocument = new CadDocument();

//            foreach (var slot in result.Grid.Where(s => !s.IsMissing))
//            {
//                var sheet = sheets.First(s => s.SheetID == slot.SheetID);

//                try
//                {
//                    CloneSheetIntoMaster(sheet.FilePath, slot.Row, slot.Col, masterDocument);
//                }
//                catch (Exception ex)
//                {
//                    result.Errors.Add($"Sheet {slot.SheetNumber}: clone failed ({ex.Message}).");
//                }
//            }

//            // --- 5) Export master DXF ---
//            Directory.CreateDirectory(outputRootPath);
//            var fileName = $"project_{projectId}_jigsaw_{DateTime.UtcNow:yyyyMMdd_HHmmss}.dxf";
//            var fullPath = Path.Combine(outputRootPath, fileName);

//            DxfWriter.Write(fullPath, masterDocument);

//            result.Success = true;
//            result.OutputFilePath = fullPath;
//            return result;
//        }

//        private void CloneSheetIntoMaster(
//            string relativeFilePath,
//            int gridRow,
//            int gridCol,
//            CadDocument masterDocument)
//        {
//            var fullPath = Path.Combine(_uploadRootPath, relativeFilePath);
//            if (!File.Exists(fullPath))
//                throw new FileNotFoundException("Source DWG/DXF not found.", fullPath);

//            var ext = Path.GetExtension(fullPath).ToLowerInvariant();
//            var sourceDocument = ext == ".dxf" ? DxfReader.Read(fullPath) : DwgReader.Read(fullPath);

//            double offsetX = gridCol * SheetWidth;
//            double offsetY = -gridRow * SheetHeight; // row increases downward, DXF Y increases upward

//            var translation = Transform.CreateTranslation(new XYZ(offsetX, offsetY, 0));

//            // ModelSpace only, per spec — every entity type (lines, text,
//            // polylines, blocks/inserts, hatches, point-symbols) goes through
//            // the same Entity.Clone() + ApplyTransform() path with no
//            // per-type branching, so nothing is left behind.
//            foreach (var sourceEntity in sourceDocument.Entities.ToList())
//            {
//                var clone = (Entity)sourceEntity.Clone();
//                clone.ApplyTransform(translation);
//                masterDocument.Entities.Add(clone);
//            }
//        }
//    }
//}