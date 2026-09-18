//using System.Globalization;
//using System.Text;
//using MapStitcher.Business.Contracts;
//using MapStitcher.Database;
//using MapStitcher.Repositories.Contracts;
//using MapStitcher.Utilities;

//namespace MapStitcher.Business.Services
//{
//    // Builds an inline SVG preview of the true vector mosaic: every sheet's
//    // actual parcel boundary geometry, transformed via the same
//    // SheetTransformHelper used by the DXF exporter, so the SVG preview and
//    // the DXF export always agree on sheet placement.
//    public class SvgMosaicExportService : ISvgMosaicExportService
//    {
//        private const double ViewportPadding = 20.0;
//        private const double StrokeWidth = 0.6;
//        private const double LabelFontSize = 4.0;

//        // Cycled per sheet purely for visual distinction between adjoining sheets.
//        private static readonly string[] FillPalette =
//        {
//            "#cfe8ff", "#ffe6cc", "#d9f2d9", "#f2d9e8",
//            "#fff2b3", "#d9e0f2", "#f2e0d9", "#e0f2ec"
//        };

//        private readonly ISurveySheetRepository _sheetRepo;
//        private readonly ISheetBoundaryRepository _boundaryRepo;

//        public SvgMosaicExportService(
//            ISurveySheetRepository sheetRepo,
//            ISheetBoundaryRepository boundaryRepo)
//        {
//            _sheetRepo = sheetRepo;
//            _boundaryRepo = boundaryRepo;
//        }

//        public async Task<string> BuildProjectSvgAsync(int projectId)
//        {
//            var sheets = await _sheetRepo.GetByProjectIdAsync(projectId);
//            if (sheets.Count == 0)
//                throw new InvalidOperationException("No sheets found in this project.");

//            var sheetPolygons = new List<(SurveySheet Sheet, List<(double X, double Y)[]> Rings)>();

//            double minX = double.MaxValue, minY = double.MaxValue;
//            double maxX = double.MinValue, maxY = double.MinValue;
//            bool hasGeometry = false;

//            foreach (var sheet in sheets)
//            {
//                var boundaries = await _boundaryRepo.GetBySheetIdAsync(sheet.SheetID);
//                var rings = new List<(double X, double Y)[]>();

//                foreach (var boundary in boundaries)
//                {
//                    var coords = boundary.Geometry.Coordinates;
//                    if (coords.Length < 2)
//                        continue;

//                    var ring = new (double X, double Y)[coords.Length];
//                    for (int i = 0; i < coords.Length; i++)
//                    {
//                        var (x, y) = SheetTransformHelper.Apply(sheet, coords[i].X, coords[i].Y);
//                        ring[i] = (x, y);

//                        if (x < minX) minX = x;
//                        if (x > maxX) maxX = x;
//                        if (y < minY) minY = y;
//                        if (y > maxY) maxY = y;
//                        hasGeometry = true;
//                    }

//                    rings.Add(ring);
//                }

//                sheetPolygons.Add((sheet, rings));
//            }

//            if (!hasGeometry)
//                throw new InvalidOperationException("No boundary geometry available to render for this project.");

//            minX -= ViewportPadding;
//            minY -= ViewportPadding;
//            maxX += ViewportPadding;
//            maxY += ViewportPadding;

//            double width = maxX - minX;
//            double height = maxY - minY;

//            var sb = new StringBuilder();
//            sb.Append(CultureInfo.InvariantCulture, $"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {width:F2} {height:F2}\" font-family=\"sans-serif\">");

//            int colorIndex = 0;

//            foreach (var (sheet, rings) in sheetPolygons)
//            {
//                string fill = FillPalette[colorIndex % FillPalette.Length];
//                colorIndex++;

//                foreach (var ring in rings)
//                {
//                    // Flip Y: source geometry is Y-up (CAD convention), SVG is Y-down.
//                    var points = string.Join(" ", ring.Select(p =>
//                        $"{(p.X - minX).ToString("F2", CultureInfo.InvariantCulture)},{(maxY - p.Y).ToString("F2", CultureInfo.InvariantCulture)}"));

//                    sb.Append(CultureInfo.InvariantCulture,
//                        $"<polygon points=\"{points}\" fill=\"{fill}\" stroke=\"#333333\" stroke-width=\"{StrokeWidth:F2}\" />");
//                }

//                if (rings.Count > 0 && rings[0].Length > 0)
//                {
//                    var (lx, ly) = rings[0][0];
//                    string label = BuildSheetLabel(sheet);

//                    sb.Append(CultureInfo.InvariantCulture,
//                        $"<text x=\"{(lx - minX):F2}\" y=\"{(maxY - ly):F2}\" font-size=\"{LabelFontSize:F1}\" fill=\"#111111\">{System.Security.SecurityElement.Escape(label)}</text>");
//                }
//            }

//            sb.Append("</svg>");
//            return sb.ToString();
//        }

//        private static string BuildSheetLabel(SurveySheet sheet)
//        {
//            return string.IsNullOrEmpty(sheet.LaghuReferenceNumber)
//                ? $"Sheet {sheet.SheetNumber}"
//                : $"Sheet {sheet.SheetNumber} (Laghu {sheet.LaghuReferenceNumber})";
//        }
//    }
//}