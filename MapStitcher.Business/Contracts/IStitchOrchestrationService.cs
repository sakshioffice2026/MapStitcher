namespace MapStitcher.Business.Contracts
{
    public interface IStitchOrchestrationService
    {
        /// <summary>
        /// Places and merges every sheet in the project that can be placed/merged automatically.
        /// Sheets with no uploaded neighbor are left as gaps. Never throws for individual
        /// sheet failures — each is reported in the result instead.
        /// </summary>
        Task<StitchResult> StitchAllAsync(int projectId);
    }

    public class StitchResult
    {
        public int TotalSheets { get; set; }
        public int PlacedCount { get; set; }
        public int MergedCount { get; set; }
        public int NeedsManualCheckCount { get; set; }
        public List<SheetStitchOutcome> Outcomes { get; set; } = new();
    }

    public class SheetStitchOutcome
    {
        public int SheetID { get; set; }
        public string SheetNumber { get; set; } = "";
        public bool Placed { get; set; }
        public bool Merged { get; set; }

        /// <summary>Plain-language reason to show on the sheet's grid cell. Null when fully merged.</summary>
        public string? FailureReason { get; set; }
    }
}