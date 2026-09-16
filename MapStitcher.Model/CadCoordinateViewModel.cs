namespace MapStitcher.Model
{
    public class CadCoordinateViewModel
    {
        public int SheetID { get; set; }

        public string SheetNumber { get; set; } = string.Empty;

        public string EntityType { get; set; } = string.Empty;

        public string LayerName { get; set; } = string.Empty;

        public string? Text { get; set; }

        public double X { get; set; }

        public double Y { get; set; }

        public bool HasInsertionPoint { get; set; }

        public string? LaghuReferenceNumber { get; set; }

        public string? Shape { get; set; }
    }
}