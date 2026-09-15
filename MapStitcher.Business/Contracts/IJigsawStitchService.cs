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
        Task<JigsawMergeResult> MergeSheetsAsync(
            int projectId,
            int columnsPerRow,
            string outputRootPath);
    }
}