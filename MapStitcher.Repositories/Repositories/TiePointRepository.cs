using MapStitcher.Database;
using MapStitcher.Repositories.Contracts;
using Microsoft.EntityFrameworkCore;

namespace MapStitcher.Repositories.Repositories
{
    public class TiePointRepository : ITiePointRepository
    {
        private readonly ApplicationDbContext _context;

        public TiePointRepository(ApplicationDbContext context)
        {
            _context = context;
        }

        public async Task<List<TiePoint>> GetBySheetIdAsync(int sheetId)
            => await _context.TiePoints
                .Where(t => t.SheetID == sheetId)
                .ToListAsync();

        public async Task AddAsync(TiePoint tiePoint)
            => await _context.TiePoints.AddAsync(tiePoint);

        public async Task SaveChangesAsync()
            => await _context.SaveChangesAsync();
    }
}