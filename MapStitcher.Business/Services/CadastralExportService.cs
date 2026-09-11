using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using ACadSharp.Tables;
using MapStitcher.Business.Contracts;
using MapStitcher.Repositories.Contracts;

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
                        var (x, y) = ApplyTransform(sheet, coord.X, coord.Y);
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
                    var (lx, ly) = ApplyTransform(sheet, firstCoord.X, firstCoord.Y);
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

        private static (double X, double Y) ApplyTransform(MapStitcher.Database.SurveySheet sheet, double x, double y)
        {
            // Reproduces the similarity transform saved during merge:
            // scale -> rotate -> translate. Identity for never-merged sheets.
            double scaledX = x * sheet.TransformScale;
            double scaledY = y * sheet.TransformScale;

            double cos = Math.Cos(sheet.TransformRotation);
            double sin = Math.Sin(sheet.TransformRotation);

            double rotatedX = scaledX * cos - scaledY * sin;
            double rotatedY = scaledX * sin + scaledY * cos;

            // If the sheet has an identity transform (never merged), apply a
            // default grid-based separation offset so all sheets stay spatially
            // separated in the export rather than collapsing at the origin.
            bool isIdentity = sheet.TransformScale == 1.0
                              && sheet.TransformRotation == 0.0
                              && sheet.TransformTranslateX == 0.0
                              && sheet.TransformTranslateY == 0.0;

            if (isIdentity && sheet.GridRow.HasValue && sheet.GridCol.HasValue)
            {
                // Use grid position as a deterministic offset to prevent overlap.
                // 100.0 units per grid cell keeps sheets visibly separated.
                rotatedX += sheet.GridCol.Value * 100.0;
                rotatedY += sheet.GridRow.Value * 100.0;
            }

            return (rotatedX + sheet.TransformTranslateX, rotatedY + sheet.TransformTranslateY);
        }
    }
}