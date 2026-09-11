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

    public interface ISheetPlacementService
    {
        Task<PlacementResult> AutoPlaceSheetAsync(int sheetId);
        Task<PlacementResult> ManualPlaceSheetAsync(int sheetId, int gridRow, int gridCol);
    }
}