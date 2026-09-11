// Repositories/SurveySheetRepository.cs
using MapStitcher.Database;
using MapStitcher.Repositories.Contracts;
using Microsoft.EntityFrameworkCore;

namespace MapStitcher.Repositories.Repositories
{
    public class SurveySheetRepository : ISurveySheetRepository
    {
        private readonly ApplicationDbContext _context;

        public SurveySheetRepository(ApplicationDbContext context)
        {
            _context = context;
        }

        public async Task<SurveySheet?> GetByIdAsync(int sheetId)
            => await _context.SurveySheets.FindAsync(sheetId);

        public async Task<List<SurveySheet>> GetByProjectIdAsync(int projectId)
            => await _context.SurveySheets
                .Where(s => s.ProjectID == projectId)
                .ToListAsync();

        public async Task AddAsync(SurveySheet sheet)
            => await _context.SurveySheets.AddAsync(sheet);

        public Task DeleteAsync(SurveySheet sheet)
        {
            _context.SurveySheets.Remove(sheet);
            return Task.CompletedTask;
        }

        public async Task SaveChangesAsync()
            => await _context.SaveChangesAsync();
    }
}