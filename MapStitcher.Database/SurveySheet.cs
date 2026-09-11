namespace MapStitcher.Database
{
    public class SurveySheet
    {
        public int SheetID { get; set; }
        public int ProjectID { get; set; }
        public string SheetNumber { get; set; } = null!;
        public string? LaghuReferenceNumber { get; set; }
        public string FilePath { get; set; } = null!;
        public string? FileHash { get; set; }
        public string? DwgVersion { get; set; }
        public SheetStatus Status { get; set; } = SheetStatus.Uploaded;
        public string? FailureReason { get; set; }
        public int? GridRow { get; set; }
        public int? GridCol { get; set; }

        // Persisted similarity transform (identity = untouched/base sheet)
        public double TransformRotation { get; set; } = 0.0;
        public double TransformScale { get; set; } = 1.0;
        public double TransformTranslateX { get; set; } = 0.0;
        public double TransformTranslateY { get; set; } = 0.0;

        public DateTime UploadedDate { get; set; } = DateTime.UtcNow;

        public Project Project { get; set; } = null!;
        public ICollection<TiePoint> TiePoints { get; set; } = new List<TiePoint>();
        public ICollection<SheetBoundary> Boundaries { get; set; } = new List<SheetBoundary>();
    }
}