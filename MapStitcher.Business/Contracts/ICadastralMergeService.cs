// Contracts/ICadastralMergeService.cs — full file
namespace MapStitcher.Business.Contracts
{
    public interface ICadastralMergeService
    {
        Task<MergeResult> MergeSheetsAsync(int baseSheetId, int adjacentSheetId);
    }

    public class MergeResult
    {
        public bool Success { get; set; }
        public double RmsErrorMeters { get; set; }
        public int MatchedPointCount { get; set; }
        public int InlierPointCount { get; set; }
        public int RejectedOutlierCount { get; set; }

        /// <summary>Technical detail for logs/diagnostics.</summary>
        public string? Message { get; set; }

        /// <summary>Short, non-technical sentence safe to show directly on a grid-cell badge. Null on success.</summary>
        public string? FailureReason { get; set; }
    }
}