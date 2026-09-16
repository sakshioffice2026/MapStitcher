namespace MapStitcher.Model
{
    public class CadSheetGridResult
    {
        public int SheetCount { get; set; }

        public int ColumnCount { get; set; }

        public int RowCount { get; set; }

        public List<CadSheetGridItem> Sheets { get; set; }
            = new();
    }
}