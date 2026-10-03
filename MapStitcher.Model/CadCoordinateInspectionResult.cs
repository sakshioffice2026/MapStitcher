namespace MapStitcher.Model
{
    public class CadCoordinateInspectionResult
    {
        public int SheetID { get; set; }

        public string SheetNumber { get; set; } = string.Empty;

        public double MinX { get; set; }

        public double MinY { get; set; }

        public double MaxX { get; set; }

        public double MaxY { get; set; }

        public int EntityCount { get; set; }

        public List<CadCoordinateViewModel> Coordinates { get; set; }
            = new();

        public List<LaghuReferenceViewModel> LaghuReferences { get; set; }
            = new();
    }
}