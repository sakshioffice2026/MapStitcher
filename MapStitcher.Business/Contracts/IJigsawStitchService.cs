namespace MapStitcher.Business.Contracts
{
    public class JigsawSlot
    {
        public int Row { get; set; }
        public int Col { get; set; }
        public int? SheetID { get; set; }
        public string? SheetNumber { get; set; }
        public bool IsMissing { get; set; }
    }

    public class JigsawMergeResult
    {
        public bool Success { get; set; }
        public string? OutputFilePath { get; set; }
        public int TotalSlots { get; set; }
        public int PlacedCount { get; set; }
        public int MissingCount { get; set; }
        public List<JigsawSlot> Grid { get; set; } = new();
        public List<string> Errors { get; set; } = new();
    }

    public interface IJigsawStitchService
    {
        // Calculates and persists GridRow/GridCol and TransformTranslateX/Y only.
        // This is the UI preview/layout operation and never creates a download file.
        Task<JigsawMergeResult> ArrangeSheetsAsync(int projectId, int columnsPerRow);

        // Exports the already-arranged sheets by cloning every source entity and
        // applying the persisted transform. Export is intentionally separate from Arrange.
        Task<string> ExportArrangedSheetsAsync(int projectId, string outputRootPath);

        // Backward-compatible combined operation for existing callers.
        Task<JigsawMergeResult> MergeSheetsAsync(int projectId, int columnsPerRow, string outputRootPath);
    }
}