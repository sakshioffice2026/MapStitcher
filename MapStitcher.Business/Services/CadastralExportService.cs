using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using ACadSharp.Tables;
using MapStitcher.Business.Contracts;
using MapStitcher.Database;
using MapStitcher.Repositories.Contracts;
using MapStitcher.Utilities;

namespace MapStitcher.Business.Services
{
    public class CadastralExportService : ICadastralExportService
    {
        private readonly ISurveySheetRepository _sheetRepo;
        private readonly ISheetBoundaryRepository _boundaryRepo;
        private readonly string _uploadRootPath;

        public CadastralExportService(
            ISurveySheetRepository sheetRepo,
            ISheetBoundaryRepository boundaryRepo,
            string uploadRootPath)
        {
            _sheetRepo = sheetRepo;
            _boundaryRepo = boundaryRepo;
            _uploadRootPath = uploadRootPath;
        }

        public async Task<string> ExportProjectAsync(int projectId, string outputRootPath)
        {
            var sheets = await _sheetRepo.GetByProjectIdAsync(projectId);
            if (sheets.Count == 0)
                throw new InvalidOperationException("No sheets found in this project.");

            var document = new CadDocument();

            foreach (var sheet in sheets)
            {
                var boundaries = await _boundaryRepo.GetBySheetIdAsync(sheet.SheetID);

                foreach (var boundary in boundaries)
                {
                    var coords = boundary.Geometry.Coordinates;
                    if (coords.Length < 2)
                        continue;

                    var polyline = new LwPolyline();

                    foreach (var coord in coords)
                    {
                        var (x, y) = SheetTransformHelper.Apply(sheet, coord.X, coord.Y);
                        polyline.Vertices.Add(new LwPolyline.Vertex(new CSMath.XY(x, y)));
                    }

                    polyline.IsClosed = true;
                    document.Entities.Add(polyline);
                }

                var sheetLabel = new TextEntity
                {
                    Value = $"Sheet {sheet.SheetNumber}" +
                            (string.IsNullOrEmpty(sheet.LaghuReferenceNumber) ? "" : $" (Laghu {sheet.LaghuReferenceNumber})"),
                    Height = 2.5
                };

                if (boundaries.Any())
                {
                    var firstCoord = boundaries.First().Geometry.Coordinates.First();
                    var (lx, ly) = SheetTransformHelper.Apply(sheet, firstCoord.X, firstCoord.Y);
                    sheetLabel.InsertPoint = new CSMath.XYZ(lx, ly, 0);
                }

                document.Entities.Add(sheetLabel);
            }

            Directory.CreateDirectory(outputRootPath);
            var fileName = $"project_{projectId}_export_{DateTime.UtcNow:yyyyMMdd_HHmmss}.dxf";
            var fullPath = Path.Combine(outputRootPath, fileName);

            DxfWriter.Write(fullPath, document);

            return fullPath;
        }

        public async Task<string> ExportMasterDxfAsync(int projectId, string outputRootPath)
        {
            var sheets = await _sheetRepo.GetByProjectIdAsync(projectId);
            if (sheets.Count == 0)
                throw new InvalidOperationException("No sheets found in this project.");

            if (!sheets.Any(s => s.GridRow.HasValue && s.GridCol.HasValue))
                throw new InvalidOperationException(
                    "No sheet has a grid arrangement yet. Call ArrangeSheetsGrid before exporting.");

            var masterDocument = new CadDocument();
            var errors = new List<string>();
            int clonedSheetCount = 0;

            foreach (var sheet in sheets)
            {
                if (!sheet.GridRow.HasValue || !sheet.GridCol.HasValue)
                {
                    errors.Add($"Sheet {sheet.SheetNumber}: not arranged on the grid, skipped.");
                    continue;
                }

                try
                {
                    int entityCount = CloneSheetEntitiesIntoMaster(sheet, masterDocument);
                    if (entityCount > 0)
                        clonedSheetCount++;
                    else
                        errors.Add($"Sheet {sheet.SheetNumber}: source file contained no cloneable entities.");
                }
                catch (Exception ex)
                {
                    errors.Add($"Sheet {sheet.SheetNumber}: clone failed ({ex.Message}).");
                }
            }

            if (clonedSheetCount == 0)
            {
                throw new InvalidOperationException(
                    "Master DXF export produced no entities. " +
                    (errors.Count > 0 ? string.Join(" | ", errors) : "No sheets could be cloned."));
            }

            Directory.CreateDirectory(outputRootPath);
            var fileName = $"project_{projectId}_master_{DateTime.UtcNow:yyyyMMdd_HHmmss}.dxf";
            var fullPath = Path.Combine(outputRootPath, fileName);

            DxfWriter.Write(fullPath, masterDocument);

            return fullPath;
        }

        private int CloneSheetEntitiesIntoMaster(SurveySheet sheet, CadDocument masterDocument)
        {
            var fullPath = Path.Combine(_uploadRootPath, sheet.FilePath);
            if (!File.Exists(fullPath))
                throw new FileNotFoundException($"Source DWG/DXF not found at '{fullPath}'.", fullPath);

            var ext = Path.GetExtension(fullPath).ToLowerInvariant();
            var sourceDocument = ext == ".dxf" ? DxfReader.Read(fullPath) : DwgReader.Read(fullPath);

            // MergeSheets arranges the project into a non-overlapping grid and
            // persists that layout in OffsetX/OffsetY. The previous implementation
            // used TransformTranslateX/Y here, which belongs to the tie-point stitch
            // pipeline and could contain stale values from an earlier stitch.
            double tx = sheet.OffsetX;
            double ty = sheet.OffsetY;

            var translation = CSMath.Transform.CreateTranslation(
                new CSMath.XYZ(tx, ty, 0));

            int count = 0;
            foreach (var sourceEntity in sourceDocument.Entities.ToList())
            {
                var clone = (Entity)sourceEntity.Clone();
                clone.ApplyTransform(translation);
                masterDocument.Entities.Add(clone);
                count++;
            }

            return count;
        }
    }
}