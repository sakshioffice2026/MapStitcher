// Controllers/ProjectsController.cs
using MapStitcher.Database;
using MapStitcher.Repositories.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace MapStitcher.Web.Controllers
{
    public class ProjectsController : Controller
    {
        private readonly IProjectRepository _projectRepo;

        public ProjectsController(IProjectRepository projectRepo)
        {
            _projectRepo = projectRepo;
        }

        [HttpGet]
        public async Task<IActionResult> Index()
        {
            var projects = await _projectRepo.GetAllAsync();
            return View(projects);
        }

        [HttpGet]
        public IActionResult Create()
        {
            return View();
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(string name, string district, string taluka, string village)
        {
            if (string.IsNullOrWhiteSpace(name))
                ModelState.AddModelError("name", "Project name is required.");

            if (string.IsNullOrWhiteSpace(district))
                ModelState.AddModelError("district", "District is required.");

            if (string.IsNullOrWhiteSpace(taluka))
                ModelState.AddModelError("taluka", "Taluka is required.");

            if (string.IsNullOrWhiteSpace(village))
                ModelState.AddModelError("village", "Village is required.");

            if (!ModelState.IsValid)
                return View();

            var project = new Project
            {
                Name = name.Trim(),
                District = district.Trim(),
                Taluka = taluka.Trim(),
                Village = village.Trim(),
                CreatedDate = DateTime.UtcNow
            };

            await _projectRepo.AddAsync(project);
            await _projectRepo.SaveChangesAsync();

            return RedirectToAction("Index");
        }

        [HttpGet]
        public async Task<IActionResult> Details(int id)
        {
            var project = await _projectRepo.GetByIdAsync(id);
            if (project == null)
                return NotFound();

            return View(project);
        }
    }
}