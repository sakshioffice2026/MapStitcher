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

        // Session ID for re-export: identifies the persistent copy of uploaded
        // files kept in exports/cadgrid/sessions/{SessionId}/ so the user can
        // trigger a fresh DXF export without re-uploading the source files.
        public string? SessionId { get; set; }

        public List<string> MergeErrors { get; set; } = new();

        // Upload validation warnings
        public List<string> SkippedFileNames { get; set; } = new();
    }
}