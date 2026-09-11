using MapStitcher.Database;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MapStitcher.Repositories.Contracts
{

          public interface IProjectRepository
        {
            Task<Project?> GetByIdAsync(int projectId);
            Task<List<Project>> GetAllAsync();
            Task AddAsync(Project project);
            Task SaveChangesAsync();
        }
    }

