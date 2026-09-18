//using MapStitcher.Database;
//using MapStitcher.Repositories.Contracts;
//using Microsoft.EntityFrameworkCore;

//namespace MapStitcher.Repositories.Repositories
//{
//    public class SheetBoundaryRepository : ISheetBoundaryRepository
//    {
//        private readonly ApplicationDbContext _context;

//        public SheetBoundaryRepository(ApplicationDbContext context)
//        {
//            _context = context;
//        }

//        public async Task<List<SheetBoundary>> GetBySheetIdAsync(int sheetId)
//            => await _context.SheetBoundaries
//                .Where(b => b.SheetID == sheetId)
//                .ToListAsync();

//        public async Task AddAsync(SheetBoundary boundary)
//            => await _context.SheetBoundaries.AddAsync(boundary);

//        public async Task SaveChangesAsync()
//            => await _context.SaveChangesAsync();
//    }
//}