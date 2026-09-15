namespace MapStitcher.Business.Contracts
{
    public class SheetIndexInfo
    {
        public int SheetID { get; set; }
        public string SheetNumber { get; set; } = "";
        public int? SheetIndexNumber { get; set; }
        public int? ExplicitRow { get; set; }
        public int? ExplicitCol { get; set; }
        public string? GatNumber { get; set; }
        public bool Extracted { get; set; }
    }

    public interface IJigsawIndexExtractionService
    {
        Task<SheetIndexInfo> ExtractAsync(int sheetId);
    }
}