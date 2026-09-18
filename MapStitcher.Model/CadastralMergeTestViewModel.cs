//namespace MapStitcher.Model
//{
//    public class CadastralMergeTestViewModel
//    {
//        public int BaseSheetId { get; set; }
//        public string BaseSheetNumber { get; set; } = "";
//        public int AdjacentSheetId { get; set; }
//        public string AdjacentSheetNumber { get; set; } = "";

//        public bool Success { get; set; }
//        public int MatchedPointCount { get; set; }
//        public int InlierPointCount { get; set; }
//        public int RejectedOutlierCount { get; set; }

//        public double DeltaX { get; set; }
//        public double DeltaY { get; set; }
//        public double RmsErrorMeters { get; set; }

//        public string Direction { get; set; } = "Unknown";
//        public string? GridRelationship { get; set; }

//        public string? Message { get; set; }
//        public string? FailureReason { get; set; }

//        public List<MatchedPointRow> MatchedPoints { get; set; } = new();
//    }

//    public class MatchedPointRow
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