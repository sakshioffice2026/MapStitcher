// MapStitcher.Model/SheetTopology.cs
namespace MapStitcher.Model
{
    public class SheetTopologyInput
    {
        public string SheetId { get; set; } = string.Empty;
        public string? CenterSheetNumber { get; set; }
        public string? TopSheetNumber { get; set; }
        public string? BottomSheetNumber { get; set; }
        public string? LeftSheetNumber { get; set; }
        public string? RightSheetNumber { get; set; }
    }

    public class SheetTopologyResult
    {
        public string SheetId { get; set; } = string.Empty;
        public string? SheetNumber { get; set; }
        public int GridX { get; set; }
        public int GridY { get; set; }
        public bool Placed { get; set; }
    }

    public class TopologyBuildResult
    {
        public List<SheetTopologyResult> Sheets { get; set; } = new();
        public List<string> Anomalies { get; set; } = new();
    }
}