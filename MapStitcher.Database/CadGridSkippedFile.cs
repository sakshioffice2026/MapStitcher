namespace MapStitcher.Database
{
    public class CadGridSkippedFile
    {
        public int SkippedFileID { get; set; }
        public string SessionId { get; set; } = null!;

        public string FileName { get; set; } = null!;

        public CadGridSession Session { get; set; } = null!;
    }
}
