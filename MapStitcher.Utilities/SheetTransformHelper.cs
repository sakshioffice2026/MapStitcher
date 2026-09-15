﻿using MapStitcher.Database;

namespace MapStitcher.Utilities
{
    /// <summary>
    /// Applies a sheet's persisted similarity transform in one place so
    /// SVG preview and DXF export use exactly the same coordinates.
    /// </summary>
    public static class SheetTransformHelper
    {
        public static (double X, double Y) Apply(
            SurveySheet sheet,
            double x,
            double y)
        {
            double scaledX =
                x * sheet.TransformScale;

            double scaledY =
                y * sheet.TransformScale;

            double cos =
                Math.Cos(sheet.TransformRotation);

            double sin =
                Math.Sin(sheet.TransformRotation);

            double rotatedX =
                scaledX * cos -
                scaledY * sin;

            double rotatedY =
                scaledX * sin +
                scaledY * cos;

            return (
                rotatedX + sheet.TransformTranslateX,
                rotatedY + sheet.TransformTranslateY);
        }
    }
}
