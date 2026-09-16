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

        /// <summary>True when GridRow/GridCol came from BFS over the title-block
        /// index-box N/S/E/W neighbor links; false when it fell back to the
        /// alphabetical columnsPerRow layout because no sheet in the project had
        /// any neighbor data.</summary>
        public bool UsedNeighborGraph { get; set; }

        /// <summary>Neighbor disagreements (two sheets both claim the same grid
        /// cell) and sheets with no neighbor path back to the root, found while
        /// walking the graph. Non-fatal — arrangement still succeeds.</summary>
        public List<string> Conflicts { get; set; } = new();
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