using MapStitcher.Business.Contracts;
using MapStitcher.Database;
using MapStitcher.Model;
using MapStitcher.Repositories.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace MapStitcher.Web.Controllers
{
    public class SheetsController : Controller
    {
        private readonly ISurveySheetRepository _sheetRepo;
        private readonly IProjectRepository _projectRepo;
        private readonly ITiePointRepository _tiePointRepo;
        private readonly ICadastralParsingService _parsingService;
        private readonly ICadastralMergeService _mergeService;
        private readonly ISheetPlacementService _placementService;
        private readonly ICadastralExportService _exportService;
        private readonly IWebHostEnvironment _env;

        private static readonly string[] AllowedExtensions = { ".dwg", ".dxf" };
        private const long MaxFileSize = 50 * 1024 * 1024;

        public SheetsController(
            ISurveySheetRepository sheetRepo,
            IProjectRepository projectRepo,
            ITiePointRepository tiePointRepo,
            ICadastralParsingService parsingService,
            ICadastralMergeService mergeService,
            ISheetPlacementService placementService,
            ICadastralExportService exportService,
            IWebHostEnvironment env)
        {
            _sheetRepo = sheetRepo;
            _projectRepo = projectRepo;
            _tiePointRepo = tiePointRepo;
            _exportService = exportService;
            _parsingService = parsingService;
            _mergeService = mergeService;
            _placementService = placementService;
            _env = env;
        }

        [HttpGet]
        public IActionResult Upload(int projectId)
        {
            ViewBag.ProjectID = projectId;
            return View();
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [RequestSizeLimit(MaxFileSize * 50)]
        public async Task<IActionResult> Upload(int projectId, List<IFormFile> files)
        {
            var project = await _projectRepo.GetByIdAsync(projectId);
            if (project == null)
                return NotFound("Invalid ProjectID.");

            if (files == null || files.Count == 0)
            {
                TempData["Error"] = "Please select at least one file.";
                return RedirectToAction("Upload", new { projectId });
            }

            var existingSheets = await _sheetRepo.GetByProjectIdAsync(projectId);
            var existingHashes = existingSheets
                .Where(s => !string.IsNullOrEmpty(s.FileHash))
                .Select(s => s.FileHash!)
                .ToHashSet();
            var existingNumbers = existingSheets
                .Select(s => s.SheetNumber)
                .ToHashSet();

            var uploadRoot = Path.Combine(_env.WebRootPath, "uploads", projectId.ToString());
            Directory.CreateDirectory(uploadRoot);

            int uploaded = 0, skippedDuplicate = 0, failed = 0;
            var messages = new List<string>();

            foreach (var file in files)
            {
                if (file.Length == 0)
                    continue;

                var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
                if (!AllowedExtensions.Contains(ext))
                {
                    failed++;
                    messages.Add($"{file.FileName}: unsupported file type.");
                    continue;
                }

                if (file.Length > MaxFileSize)
                {
                    failed++;
                    messages.Add($"{file.FileName}: exceeds 50MB limit.");
                    continue;
                }

                string fileHash;
                using (var sha256 = System.Security.Cryptography.SHA256.Create())
                using (var stream = file.OpenReadStream())
                {
                    var hashBytes = await sha256.ComputeHashAsync(stream);
                    fileHash = Convert.ToHexString(hashBytes);
                }

                if (existingHashes.Contains(fileHash))
                {
                    skippedDuplicate++;
                    messages.Add($"{file.FileName}: duplicate (already uploaded), skipped.");
                    continue;
                }

                string baseName = Path.GetFileNameWithoutExtension(file.FileName);
                string sheetNumber = baseName;
                int suffix = 2;
                while (existingNumbers.Contains(sheetNumber))
                {
                    sheetNumber = $"{baseName}-{suffix}";
                    suffix++;
                }
                existingNumbers.Add(sheetNumber);

                var safeFileName = $"{Guid.NewGuid()}{ext}";
                var fullPath = Path.Combine(uploadRoot, safeFileName);

                try
                {
                    await using var fileStream = new FileStream(fullPath, FileMode.Create);
                    file.OpenReadStream().Position = 0;
                    await file.CopyToAsync(fileStream);
                }
                catch (IOException ex)
                {
                    failed++;
                    messages.Add($"{file.FileName}: save failed ({ex.Message}).");
                    continue;
                }

                var relativePath = Path.Combine("uploads", projectId.ToString(), safeFileName)
                    .Replace("\\", "/");

                var sheet = new SurveySheet
                {
                    ProjectID = projectId,
                    SheetNumber = sheetNumber,
                    FilePath = relativePath,
                    FileHash = fileHash,
                    Status = SheetStatus.Uploaded,
                    UploadedDate = DateTime.UtcNow
                };

                await _sheetRepo.AddAsync(sheet);
                await _sheetRepo.SaveChangesAsync();
                existingHashes.Add(fileHash);

                try
                {
                    await _parsingService.ParseSheetAsync(sheet.SheetID);
                    var placement = await _placementService.AutoPlaceSheetAsync(sheet.SheetID);
                    uploaded++;

                    if (placement.Success)
                    {
                        messages.Add($"{file.FileName}: uploaded, parsed, placed at ({placement.GridRow},{placement.GridCol}).");

                        // Auto-merge with any already-placed grid-neighbors
                        var mergeMessages = await TryAutoMergeWithNeighborsAsync(sheet.SheetID);
                        messages.AddRange(mergeMessages);
                    }
                    else
                    {
                        messages.Add($"{file.FileName}: uploaded, parsed. Placement: {placement.Message}");
                    }
                }
                catch (Exception ex)
                {
                    uploaded++;
                    messages.Add($"{file.FileName}: uploaded but parsing failed ({ex.Message}).");
                }
            }

            TempData["Success"] = $"{uploaded} uploaded, {skippedDuplicate} duplicates skipped, {failed} failed.";
            TempData["UploadDetails"] = string.Join(" | ", messages);

            return RedirectToAction("Workspace", "Sheets", new { projectId });
        }

        [HttpPost]
        public async Task<IActionResult> Parse(int sheetId)
        {
            var sheet = await _sheetRepo.GetByIdAsync(sheetId);
            if (sheet == null)
                return NotFound("Sheet not found.");

            try
            {
                await _parsingService.ParseSheetAsync(sheetId);
                TempData["Success"] = "Sheet parsed successfully.";
            }
            catch (Exception ex)
            {
                TempData["Error"] = $"Parsing failed: {ex.Message}";
            }

            return RedirectToAction("Workspace", new { projectId = sheet.ProjectID });
        }

        [HttpPost]
        public async Task<IActionResult> PlaceSheet(int sheetId, int gridRow, int gridCol)
        {
            var sheet = await _sheetRepo.GetByIdAsync(sheetId);
            if (sheet == null)
                return NotFound("Sheet not found.");

            var result = await _placementService.ManualPlaceSheetAsync(sheetId, gridRow, gridCol);

            if (result.Success)
            {
                var mergeMessages = await TryAutoMergeWithNeighborsAsync(sheetId);
                TempData["Success"] = result.Message + (mergeMessages.Any()
                    ? " | " + string.Join(" | ", mergeMessages)
                    : "");
            }
            else
            {
                TempData["Error"] = result.Message;
            }

            return RedirectToAction("Workspace", new { projectId = sheet.ProjectID });
        }

        [HttpPost]
        public async Task<IActionResult> Merge(int baseSheetId, int adjacentSheetId)
        {
            var baseSheet = await _sheetRepo.GetByIdAsync(baseSheetId);
            if (baseSheet == null)
                return NotFound("Base sheet not found.");

            try
            {
                var result = await _mergeService.MergeSheetsAsync(baseSheetId, adjacentSheetId);

                TempData[result.Success ? "Success" : "Error"] =
                    $"{result.Message} (Matched: {result.MatchedPointCount}, Inliers: {result.InlierPointCount}, RMS: {result.RmsErrorMeters:F3})";
            }
            catch (Exception ex)
            {
                TempData["Error"] = $"Merge failed: {ex.Message}";
            }

            return RedirectToAction("Workspace", new { projectId = baseSheet.ProjectID });
        }

        [HttpPost]
        public async Task<IActionResult> RemoveSheet(int sheetId)
        {
            var sheet = await _sheetRepo.GetByIdAsync(sheetId);
            if (sheet == null)
                return NotFound("Sheet not found.");

            int projectId = sheet.ProjectID;
            string sheetNumber = sheet.SheetNumber;

            var fullPath = Path.Combine(_env.WebRootPath, sheet.FilePath);
            if (System.IO.File.Exists(fullPath))
            {
                try { System.IO.File.Delete(fullPath); }
                catch { /* non-fatal — DB record removal still proceeds */ }
            }

            await _sheetRepo.DeleteAsync(sheet);
            await _sheetRepo.SaveChangesAsync();

            TempData["Success"] = $"Sheet {sheetNumber} removed.";
            return RedirectToAction("Workspace", new { projectId });
        }

        [HttpGet]
        public async Task<IActionResult> Georeference(int sheetId)
        {
            var sheet = await _sheetRepo.GetByIdAsync(sheetId);
            if (sheet == null)
                return NotFound("Sheet not found.");

            var tiePoints = await _tiePointRepo.GetBySheetIdAsync(sheetId);

            var model = new GeoreferenceViewModel
            {
                SheetID = sheet.SheetID,
                SheetNumber = sheet.SheetNumber,
                ProjectID = sheet.ProjectID,
                TiePoints = tiePoints.Select(t => new TiePointInputModel
                {
                    PointID = t.PointID,
                    PointLabel = t.PointLabel,
                    SourceX = t.SourceX,
                    SourceY = t.SourceY,
                    TargetX = t.TargetX,
                    TargetY = t.TargetY
                }).ToList()
            };

            return View(model);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Georeference(GeoreferenceViewModel model)
        {
            var sheet = await _sheetRepo.GetByIdAsync(model.SheetID);
            if (sheet == null)
                return NotFound("Sheet not found.");

            var existingPoints = await _tiePointRepo.GetBySheetIdAsync(model.SheetID);

            foreach (var input in model.TiePoints)
            {
                var point = existingPoints.FirstOrDefault(p => p.PointID == input.PointID);
                if (point == null)
                    continue;

                point.PointLabel = string.IsNullOrWhiteSpace(input.PointLabel) ? null : input.PointLabel.Trim();
                point.TargetX = input.TargetX;
                point.TargetY = input.TargetY;
            }

            await _tiePointRepo.SaveChangesAsync();

            TempData["Success"] = "Georeference control points saved.";
            return RedirectToAction("Georeference", new { sheetId = model.SheetID });
        }

        [HttpGet]
        public async Task<IActionResult> Workspace(int projectId)
        {
            var project = await _projectRepo.GetByIdAsync(projectId);
            if (project == null)
                return NotFound("Invalid ProjectID.");

            var sheets = await _sheetRepo.GetByProjectIdAsync(projectId);

            var model = new WorkspaceViewModel
            {
                ProjectID = projectId,
                ProjectName = project.Name,
                District = project.District,
                Taluka = project.Taluka,
                Village = project.Village,
                Sheets = sheets
            };

            return View(model);
        }
        [HttpGet]
        public async Task<IActionResult> Export(int projectId)
        {
            var project = await _projectRepo.GetByIdAsync(projectId);
            if (project == null)
                return NotFound("Invalid ProjectID.");

            try
            {
                var outputRoot = Path.Combine(_env.WebRootPath, "exports");
                var filePath = await _exportService.ExportProjectAsync(projectId, outputRoot);

                var fileBytes = await System.IO.File.ReadAllBytesAsync(filePath);
                var fileName = Path.GetFileName(filePath);

                return File(fileBytes, "application/dxf", fileName);
            }
            catch (Exception ex)
            {
                TempData["Error"] = $"Export failed: {ex.Message}";
                return RedirectToAction("Workspace", new { projectId });
            }
        }

        // Checks all 4 grid-neighbors of a freshly placed sheet and attempts
        // a merge with each one that already exists. Safe to call repeatedly —
        // MergeSheetsAsync itself rejects when fewer than 2 tie points match.
        private async Task<List<string>> TryAutoMergeWithNeighborsAsync(int sheetId)
        {
            var messages = new List<string>();

            var sheet = await _sheetRepo.GetByIdAsync(sheetId);
            if (sheet == null || !sheet.GridRow.HasValue || !sheet.GridCol.HasValue)
                return messages;

            var siblings = await _sheetRepo.GetByProjectIdAsync(sheet.ProjectID);

            var offsets = new (int dr, int dc)[] { (-1, 0), (1, 0), (0, -1), (0, 1) };

            foreach (var (dr, dc) in offsets)
            {
                var neighbor = siblings.FirstOrDefault(s =>
                    s.SheetID != sheetId &&
                    s.GridRow == sheet.GridRow + dr &&
                    s.GridCol == sheet.GridCol + dc);

                if (neighbor == null)
                    continue;

                try
                {
                    var result = await _mergeService.MergeSheetsAsync(sheetId, neighbor.SheetID);

                    messages.Add(result.Success
                        ? $"Auto-merged with Sheet {neighbor.SheetNumber} (RMS: {result.RmsErrorMeters:F3})."
                        : $"Auto-merge with Sheet {neighbor.SheetNumber} skipped: {result.Message}");
                }
                catch (Exception ex)
                {
                    messages.Add($"Auto-merge with Sheet {neighbor.SheetNumber} failed: {ex.Message}");
                }
            }

            return messages;
        }
    }
}