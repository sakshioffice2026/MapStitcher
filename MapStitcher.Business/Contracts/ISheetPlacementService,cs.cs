namespace MapStitcher.Business.Contracts
{
    public class PlacementResult
    {
        public bool Success { get; set; }
        public int SheetID { get; set; }
        public int GridRow { get; set; }
        public int GridCol { get; set; }
        public PlacementMode Mode { get; set; }
        public string? Message { get; set; }
    }

    public enum PlacementMode
    {
        Auto,
        Manual
    }

    /// <summary>An empty grid cell a given sheet can validly connect to, for "Connect Here" highlighting.</summary>
    public class OpenSlot
    {
        public int GridRow { get; set; }
        public int GridCol { get; set; }
        public string Label { get; set; } = "Connect Here";
    }

    public interface ISheetPlacementService
    {
        Task<PlacementResult> AutoPlaceSheetAsync(int sheetId);
        Task<PlacementResult> ManualPlaceSheetAsync(int sheetId, int gridRow, int gridCol);

        /// <summary>Returns valid empty neighboring cells for the given sheet, based on shared tie points with already-placed sheets. No direction terms — UI highlights these directly.</summary>
        Task<List<OpenSlot>> GetOpenTargetSlotsAsync(int sheetId);
    }
}