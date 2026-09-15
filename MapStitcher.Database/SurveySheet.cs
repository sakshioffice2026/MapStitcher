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

        // True CAD extents computed from every ModelSpace entity at parse time.
        // Used by the UI viewer to render/fit geometry immediately, independent
        // of whether closed-polygon boundary extraction succeeded.
        public double BoundsMinX { get; set; }
        public double BoundsMinY { get; set; }
        public double BoundsMaxX { get; set; }
        public double BoundsMaxY { get; set; }
        public bool HasGeometry { get; set; }

        // Plus-shaped title-block index-box neighbors, read from the DWG's
        // Sym_Title layer: the sheet number sitting directly above/below/
        // left/right of this sheet's own number. Null when there is no
        // neighbor in that direction (edge of the village) or when the
        // index box could not be read.
        public string? NeighborSheetNumberNorth { get; set; }
        public string? NeighborSheetNumberSouth { get; set; }
        public string? NeighborSheetNumberEast { get; set; }
        public string? NeighborSheetNumberWest { get; set; }

        // "Shape" check result: true only when this sheet's own boundary
        // (Poly_Survey_Bndry / Poly_Village_Bndry layers) resolves to a
        // clean 4-corner rectangle. False (including when no boundary was
        // found at all) means the sheet should be blocked from auto-merge
        // and flagged for manual review.
        public bool BoundaryIsRectangle { get; set; }

        // Preview-only translation offsets computed by ArrangeSheetsGrid.
        // Consumed by ExportMasterDxf to place cloned entities without overlap.
        public double OffsetX { get; set; }
        public double OffsetY { get; set; }

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