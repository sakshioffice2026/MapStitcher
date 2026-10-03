namespace MapStitcher.Database
{
    public class CadGridSheet
    {
        public int SheetID { get; set; }
        public string SessionId { get; set; } = null!;

        public int IndexNumber { get; set; }
        public string FileName { get; set; } = null!;
        public string FilePath { get; set; } = null!;
        public string SheetNumber { get; set; } = null!;

        public int Row { get; set; }
        public int Column { get; set; }

        public double MinX { get; set; }
        public double MinY { get; set; }
        public double MaxX { get; set; }
        public double MaxY { get; set; }

        public int EntityCount { get; set; }
        public int LaghuReferenceCount { get; set; }

        public CadGridSession Session { get; set; } = null!;
    }
}
