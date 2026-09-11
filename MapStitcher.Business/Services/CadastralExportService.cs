using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using ACadSharp.Tables;
using MapStitcher.Business.Contracts;
using MapStitcher.Repositories.Contracts;
using MapStitcher.Utilities;

namespace MapStitcher.Business.Services
{
    public class CadastralExportService : ICadastralExportService
    {
        private readonly ISurveySheetRepository _sheetRepo;
        private readonly ISheetBoundaryRepository _boundaryRepo;

        public CadastralExportService(
            ISurveySheetRepository sheetRepo,
            ISheetBoundaryRepository boundaryRepo)
        {
            _sheetRepo = sheetRepo;
            _boundaryRepo = boundaryRepo;
        }

        // Exports every sheet currently in the project — merged sheets use their
        // saved alignment transform, unmerged/base sheets export at native coords.
        // No completeness requirement: works with 1 sheet or N sheets.
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
    }
}