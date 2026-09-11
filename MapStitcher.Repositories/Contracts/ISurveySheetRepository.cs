using MapStitcher.Database;

namespace MapStitcher.Repositories.Contracts
{
    public interface ISurveySheetRepository
    {
        Task<SurveySheet?> GetByIdAsync(int sheetId);
        Task<List<SurveySheet>> GetByProjectIdAsync(int projectId);
        Task AddAsync(SurveySheet sheet);
        Task DeleteAsync(SurveySheet sheet);
        Task SaveChangesAsync();
    }
}