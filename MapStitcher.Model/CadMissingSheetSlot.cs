namespace MapStitcher.Model
{
    /// <summary>Grid position referenced by an uploaded sheet's index box that
    /// has no corresponding uploaded file. Rendered as a labeled blank box.</summary>
    public class CadMissingSheetSlot
    {
        public string SheetNumber { get; set; } = string.Empty;
        public int Row { get; set; }
        public int Column { get; set; }
    }
}