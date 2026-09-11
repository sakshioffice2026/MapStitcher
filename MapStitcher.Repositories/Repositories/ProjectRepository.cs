using MapStitcher.Database;
using MapStitcher.Repositories.Contracts;
using Microsoft.EntityFrameworkCore;

namespace MapStitcher.Repositories.Repositories
{
    public class ProjectRepository : IProjectRepository
    {
        private readonly ApplicationDbContext _context;

        public ProjectRepository(ApplicationDbContext context)
        {
            _context = context;
        }

        public async Task<Project?> GetByIdAsync(int projectId)
            => await _context.Projects.FindAsync(projectId);

        public async Task<List<Project>> GetAllAsync()
            => await _context.Projects.ToListAsync();

        public async Task AddAsync(Project project)
            => await _context.Projects.AddAsync(project);

        public async Task SaveChangesAsync()
            => await _context.SaveChangesAsync();
    }
}