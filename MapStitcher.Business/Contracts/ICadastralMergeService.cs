//// Contracts/ICadastralMergeService.cs — full file
//namespace MapStitcher.Business.Contracts
//{
//    public interface ICadastralMergeService
//    {
//        /// <summary>Computes the XY offset, persists TargetX/TargetY on the adjacent
//        /// sheet's tie points, and marks both sheets Merged.</summary>
//        Task<MergeResult> MergeSheetsAsync(int baseSheetId, int adjacentSheetId);

//        /// <summary>Same ΔX/ΔY/RMS/direction computation as MergeSheetsAsync, but
//        /// read-only: no TiePoint/SurveySheet fields are written and no
//        /// SaveChangesAsync is called. Used to prove the relationship on screen
//        /// before committing to an actual merge.</summary>
//        Task<MergeResult> PreviewMergeAsync(int baseSheetId, int adjacentSheetId);
//    }

//    public class MergeResult
//    {
//        public bool Success { get; set; }
//        public double RmsErrorMeters { get; set; }
//        public int MatchedPointCount { get; set; }
//        public int InlierPointCount { get; set; }
//        public int RejectedOutlierCount { get; set; }

//        /// <summary>ΔX = base.SourceX - adjacent.SourceX (mean over inlier pairs).</summary>
//        public double DeltaX { get; set; }

//        /// <summary>ΔY = base.SourceY - adjacent.SourceY (mean over inlier pairs).</summary>
//        public double DeltaY { get; set; }

//        /// <summary>Position of the adjacent sheet relative to the base sheet:
//        /// "North", "South", "East", "West", or "Unknown".</summary>
//        public string Direction { get; set; } = "Unknown";

//        /// <summary>Human-readable (ΔRow, ΔCol) implied by Direction, e.g.
//        /// "North (ΔRow=-1, ΔCol=0)".</summary>
//        public string? GridRelationship { get; set; }

//        public List<MatchedPointDetail> MatchedPoints { get; set; } = new();

//        /// <summary>Technical detail for logs/diagnostics.</summary>
//        public string? Message { get; set; }

//        /// <summary>Short, non-technical sentence safe to show directly on a grid-cell badge. Null on success.</summary>
//        public string? FailureReason { get; set; }
//    }

//    /// <summary>One matched tie-point pair used (or rejected) when computing the
//    /// base↔adjacent XY offset.</summary>
//    public class MatchedPointDetail
//    {
//        public string? Label { get; set; }
//        public double BaseX { get; set; }
//        public double BaseY { get; set; }
//        public double AdjacentX { get; set; }
//        public double AdjacentY { get; set; }
//        public double Dx { get; set; }
//        public double Dy { get; set; }
//        public bool IsInlier { get; set; }
//    }
//}