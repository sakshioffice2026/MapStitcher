namespace MapStitcher.Database
{
    public class CadGridMergeError
    {
        public int MergeErrorID { get; set; }
        public string SessionId { get; set; } = null!;

        public string ErrorMessage { get; set; } = null!;

        public CadGridSession Session { get; set; } = null!;
    }
}
