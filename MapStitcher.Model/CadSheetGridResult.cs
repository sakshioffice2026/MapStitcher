namespace MapStitcher.Model
{
    public class CadSheetGridResult
    {
        public int SheetCount { get; set; }
        public int ColumnCount { get; set; }
        public int RowCount { get; set; }
        public List<CadSheetGridItem> Sheets { get; set; } = new();

        // Blank boxes for referenced-but-not-uploaded sheets (kept in-bounds
        // of the grid so the layout shows exactly where they belong).
        public List<CadMissingSheetSlot> MissingSlots { get; set; } = new();

        // Merge output
        public string? MergeOutputFileName { get; set; }
        public List<string> MergeErrors { get; set; } = new();

        // Upload validation warnings
        public List<string> SkippedFileNames { get; set; } = new();
    }
}