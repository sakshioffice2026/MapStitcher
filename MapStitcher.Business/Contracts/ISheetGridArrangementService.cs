namespace MapStitcher.Business.Contracts
{
    public class SheetGridPlacement
    {
        public int SheetID { get; set; }
        public string SheetNumber { get; set; } = "";
        public int GridRow { get; set; }
        public int GridCol { get; set; }
        public double OffsetX { get; set; }
        public double OffsetY { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public bool HasGeometry { get; set; }
    }

    public class GridArrangementResult
    {
        public bool Success { get; set; }
        public int TotalSheets { get; set; }
        public int ColumnsPerRow { get; set; }
        public List<SheetGridPlacement> Placements { get; set; } = new();
        public string? Message { get; set; }
    }

    /// <summary>
    /// Computes an overlap-free grid layout (GridRow/GridCol/OffsetX/OffsetY)
    /// for every sheet in a project, purely as in-memory/DB preview metadata.
    /// Never touches the filesystem and never reads/writes CAD entities —
    /// use IJigsawStitchService or ICadastralExportService.ExportMasterDxfAsync
    /// for the actual file export.
    /// </summary>
    public interface ISheetGridArrangementService
    {
        Task<GridArrangementResult> ArrangeSheetsGrid(int projectId, int columnsPerRow);
    }
}