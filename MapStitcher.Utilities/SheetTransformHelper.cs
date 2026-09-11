using MapStitcher.Database;

namespace MapStitcher.Utilities
{
    /// <summary>
    /// Applies a sheet's persisted similarity transform (scale -> rotate -> translate)
    /// to a raw source-space coordinate. Shared by every export/preview pipeline
    /// (DXF export, SVG mosaic preview, etc.) so alignment math cannot drift
    /// between outputs.
    /// </summary>
    public static class SheetTransformHelper
    {
        private const double GridCellSeparation = 100.0;

        public static (double X, double Y) Apply(SurveySheet sheet, double x, double y)
        {
            double scaledX = x * sheet.TransformScale;
            double scaledY = y * sheet.TransformScale;

            double cos = Math.Cos(sheet.TransformRotation);
            double sin = Math.Sin(sheet.TransformRotation);

            double rotatedX = scaledX * cos - scaledY * sin;
            double rotatedY = scaledX * sin + scaledY * cos;

            // Never-merged sheets keep an identity transform. Fall back to a
            // deterministic grid-cell offset so they don't all collapse at
            // the origin when previewed/exported alongside merged sheets.
            bool isIdentity = sheet.TransformScale == 1.0
                              && sheet.TransformRotation == 0.0
                              && sheet.TransformTranslateX == 0.0
                              && sheet.TransformTranslateY == 0.0;

            if (isIdentity && sheet.GridRow.HasValue && sheet.GridCol.HasValue)
            {
                rotatedX += sheet.GridCol.Value * GridCellSeparation;
                rotatedY += sheet.GridRow.Value * GridCellSeparation;
            }

            return (rotatedX + sheet.TransformTranslateX, rotatedY + sheet.TransformTranslateY);
        }
    }
}