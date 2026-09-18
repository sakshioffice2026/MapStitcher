//using MapStitcher.Database;

//namespace MapStitcher.Model
//{
//    public class WorkspaceViewModel
//    {
//        public int ProjectID { get; set; }
//        public string ProjectName { get; set; } = null!;
//        public string District { get; set; } = null!;
//        public string Taluka { get; set; } = null!;
//        public string Village { get; set; } = null!;
//        public List<SurveySheet> Sheets { get; set; } = new();

//        // Grid dimensions derived from placed sheets
//        public int GridRows => Sheets.Any(s => s.GridRow.HasValue)
//            ? Sheets.Where(s => s.GridRow.HasValue).Max(s => s.GridRow!.Value) + 1
//            : 0;

//        public int GridCols => Sheets.Any(s => s.GridCol.HasValue)
//            ? Sheets.Where(s => s.GridCol.HasValue).Max(s => s.GridCol!.Value) + 1
//            : 0;

//        public SurveySheet? GetSheetAt(int row, int col) =>
//            Sheets.FirstOrDefault(s => s.GridRow == row && s.GridCol == col);

//        public List<SurveySheet> UnplacedSheets =>
//            Sheets.Where(s => !s.GridRow.HasValue || !s.GridCol.HasValue).ToList();
//    }
//}