using MapStitcher.Database;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MapStitcher.Repositories.Contracts
{
    public interface ITiePointRepository
    {
        Task<List<TiePoint>> GetBySheetIdAsync(int sheetId);
        Task AddAsync(TiePoint tiePoint);
        Task SaveChangesAsync();
    }
}
