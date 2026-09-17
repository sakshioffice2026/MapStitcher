namespace MapStitcher.Model
{
    public class CadSheetGridItem
    {
        public int IndexNumber { get; set; }

        public string FileName { get; set; } = string.Empty;

        public string FilePath { get; set; } = string.Empty;

        public string SheetNumber { get; set; } = string.Empty;

        public int Row { get; set; }

        public int Column { get; set; }

        public double MinX { get; set; }

        public double MinY { get; set; }

        public double MaxX { get; set; }

        public double MaxY { get; set; }

        public double Width =>
            MaxX - MinX;

        public double Height =>
            MaxY - MinY;

        public int EntityCount { get; set; }

        public int LaghuReferenceCount { get; set; }

        public List<LaghuReferenceViewModel> LaghuReferences { get; set; }
            = new();

        // Each inner list is one closed polygon ring from Poly_Survey_Bndry.
        // Points are in original CAD coordinates (not yet screen-scaled).
        public List<List<(double X, double Y)>> BoundaryPolygons { get; set; }
            = new();
    }
}