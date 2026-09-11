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
        public string? Message { get; set; }
    }
}