namespace MapStitcher.Database
{
    public class CadGridMissingSlot
    {
        public int MissingSlotID { get; set; }
        public string SessionId { get; set; } = null!;

        public string SheetNumber { get; set; } = null!;
        public int Row { get; set; }
        public int Column { get; set; }

        public CadGridSession Session { get; set; } = null!;
    }
}
