namespace MapStitcher.Business.Contracts
{
    public interface ICadastralExportService
    {
        Task<string> ExportProjectAsync(int projectId, string outputRootPath);
    }
}