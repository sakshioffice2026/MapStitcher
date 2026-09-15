using MapStitcher.Business.Contracts;
using MapStitcher.Business.Services;
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
        private readonly IStitchOrchestrationService _stitchService;
        private readonly ICadastralExportService _exportService;
        private readonly ISvgMosaicExportService _svgExportService;
        private readonly IJigsawStitchService _jigsawService;
        private readonly ISheetGridArrangementService _gridArrangementService;
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
            IStitchOrchestrationService stitchService,
            ICadastralExportService exportService,
            ISvgMosaicExportService svgExportService,
            IJigsawStitchService jigsawService,
            ISheetGridArrangementService gridArrangementService,
            IWebHostEnvironment env)
        {
            _sheetRepo = sheetRepo;
            _projectRepo = projectRepo;
            _tiePointRepo = tiePointRepo;
            _exportService = exportService;
            _svgExportService = svgExportService;
            _parsingService = parsingService;
            _mergeService = mergeService;
            _placementService = placementService;
            _stitchService = stitchService;
            _jigsawService = jigsawService;
            _gridArrangementService = gridArrangementService;
            _env = env;
        }

        // Isolated download action. Requires the project's sheets to already
        // have a grid arrangement (set by MergeSheets, which arranges then
        // exports in one call) or by calling ISheetGridArrangementService
        // directly from another entry point.
        [HttpGet]
        public async Task<IActionResult> ExportMasterDxf(int projectId)
        {
            var project = await _projectRepo.GetByIdAsync(projectId);
            if (project == null)
                return NotFound("Invalid ProjectID.");

            try
            {
                var outputRoot = Path.Combine(_env.WebRootPath, "exports");
                var filePath = await _exportService.ExportMasterDxfAsync(projectId, outputRoot);

                var fileBytes = await System.IO.File.ReadAllBytesAsync(filePath);
                var fileName = Path.GetFileName(filePath);

                return File(fileBytes, "application/dxf", fileName);
            }
            catch (Exception ex)
            {
                TempData["Error"] = $"Master DXF export failed: {ex.Message}";
                return RedirectToAction("Workspace", new { projectId });
            }
        }

        // Fast, reliable extents for the UI viewer — independent of whether
        // boundary-polygon extraction produced any SheetBoundary rows.
        [HttpGet]
        public async Task<IActionResult> GetSheetBounds(int sheetId)
        {
            var sheet = await _sheetRepo.GetByIdAsync(sheetId);
            if (sheet == null)
                return NotFound("Sheet not found.");

            return Json(new
            {
                sheetId = sheet.SheetID,
                hasGeometry = sheet.HasGeometry,
                minX = sheet.BoundsMinX,
                minY = sheet.BoundsMinY,
                maxX = sheet.BoundsMaxX,
                maxY = sheet.BoundsMaxY,
                offsetX = sheet.OffsetX,
                offsetY = sheet.OffsetY,
                gridRow = sheet.GridRow,
                gridCol = sheet.GridCol
            });
        }

        // Same extents as GetSheetBounds, batched for every sheet in the
        // project in one call. Used by the CAD viewer's client-side fallback
        // rectangle rendering when SvgPreview has no boundary polygons yet.
        [HttpGet]
        public async Task<IActionResult> GetProjectSheetBounds(int projectId)
        {
            var project = await _projectRepo.GetByIdAsync(projectId);
            if (project == null)
                return NotFound("Invalid ProjectID.");

            var sheets = await _sheetRepo.GetByProjectIdAsync(projectId);

            return Json(sheets.Select(sheet => new
            {
                sheetId = sheet.SheetID,
                sheetNumber = sheet.SheetNumber,
                hasGeometry = sheet.HasGeometry,
                minX = sheet.BoundsMinX,
                minY = sheet.BoundsMinY,
                maxX = sheet.BoundsMaxX,
                maxY = sheet.BoundsMaxY,
                offsetX = sheet.OffsetX,
                offsetY = sheet.OffsetY,
                gridRow = sheet.GridRow,
                gridCol = sheet.GridCol
            }));
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
                        var mergeAttempts = await TryAutoMergeWithNeighborsAsync(sheet.SheetID);
                        messages.AddRange(mergeAttempts.Select(FormatMergeMessage));
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

        // Debug-only: returns raw, undecoded TEXT/MTEXT values from a sheet's CAD
        // file as JSON, so exact Sakal Bharati raw sequences can be extracted and
        // for verifying layer names and DxfUnicodeEscapeDecoder output. Not part of the parse/merge pipeline.
        [HttpGet]
        public async Task<IActionResult> DebugRawText(int sheetId)
        {
            var sheet = await _sheetRepo.GetByIdAsync(sheetId);
            if (sheet == null)
                return NotFound("Sheet not found.");

            try
            {
                var entries = await _parsingService.DumpRawTextAsync(sheetId);
                return Json(new
                {
                    sheetId,
                    sheet.SheetNumber,
                    entryCount = entries.Count,
                    entries
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Raw text dump failed: {ex.Message}");
            }
        }

        [HttpPost]
        public async Task<IActionResult> StitchAll(int projectId)
        {
            var project = await _projectRepo.GetByIdAsync(projectId);
            if (project == null)
                return NotFound("Invalid ProjectID.");

            var result = await _stitchService.StitchAllAsync(projectId);

            foreach (var outcome in result.Outcomes)
            {
                var sheet = await _sheetRepo.GetByIdAsync(outcome.SheetID);
                if (sheet == null)
                    continue;

                sheet.FailureReason = outcome.FailureReason;
                if (outcome.Merged)
                    sheet.Status = SheetStatus.Merged;
            }
            await _sheetRepo.SaveChangesAsync();

            bool isAjax = Request.Headers["X-Requested-With"] == "XMLHttpRequest";
            if (isAjax)
            {
                return Json(new
                {
                    totalSheets = result.TotalSheets,
                    placedCount = result.PlacedCount,
                    mergedCount = result.MergedCount,
                    needsManualCheckCount = result.NeedsManualCheckCount,
                    outcomes = result.Outcomes.Select(o => new
                    {
                        sheetId = o.SheetID,
                        sheetNumber = o.SheetNumber,
                        merged = o.Merged,
                        failureReason = o.FailureReason
                    })
                });
            }

            TempData["Success"] = $"Stitched {result.MergedCount}/{result.TotalSheets} sheets.";
            return RedirectToAction("Workspace", new { projectId });
        }

        // Single-button entry point: computes a non-overlapping grid layout
        // from each sheet's real bounding box (ArrangeSheetsGrid), then
        // clones every entity from each sheet's source file at its offset
        // into one master DXF (ExportMasterDxfAsync), and returns it for
        // download. Replaces the old index-marker-dependent jigsaw path,
        // which produced a blank file whenever no sheet had a recognizable
        // row/col marker.
        [HttpPost]
        public async Task<IActionResult> MergeSheets(int projectId, int columnsPerRow)
        {
            var project = await _projectRepo.GetByIdAsync(projectId);
            if (project == null)
                return NotFound("Invalid ProjectID.");

            try
            {
                var arrangeResult = await _gridArrangementService.ArrangeSheetsGrid(projectId, columnsPerRow);
                if (!arrangeResult.Success)
                {
                    TempData["Error"] = arrangeResult.Message ?? "Grid arrangement failed.";
                    return RedirectToAction("Workspace", new { projectId });
                }

                var outputRoot = Path.Combine(_env.WebRootPath, "exports");
                var filePath = await _exportService.ExportMasterDxfAsync(projectId, outputRoot);

                var fileBytes = await System.IO.File.ReadAllBytesAsync(filePath);
                var fileName = Path.GetFileName(filePath);

                TempData["Success"] = $"Merged {arrangeResult.TotalSheets} sheet(s) into master DXF.";

                return File(fileBytes, "application/dxf", fileName);
            }
            catch (Exception ex)
            {
                TempData["Error"] = $"Merge failed: {ex.Message}";
                return RedirectToAction("Workspace", new { projectId });
            }
        }

        [HttpGet]
        public async Task<IActionResult> GetOpenSlots(int sheetId)
        {
            var slots = await _placementService.GetOpenTargetSlotsAsync(sheetId);
            return Json(slots.Select(s => new { gridRow = s.GridRow, gridCol = s.GridCol, label = s.Label }));
        }

        [HttpPost]
        public async Task<IActionResult> PlaceSheet(int sheetId, int gridRow, int gridCol)
        {
            var sheet = await _sheetRepo.GetByIdAsync(sheetId);
            if (sheet == null)
                return NotFound("Sheet not found.");

            bool isAjax = Request.Headers["X-Requested-With"] == "XMLHttpRequest";
            var result = await _placementService.ManualPlaceSheetAsync(sheetId, gridRow, gridCol);

            if (!result.Success)
            {
                TempData["Error"] = result.Message;
                if (isAjax)
                    return Json(new { success = false, placementMessage = result.Message, merges = Array.Empty<object>() });

                return RedirectToAction("Workspace", new { projectId = sheet.ProjectID });
            }

            var mergeAttempts = await TryAutoMergeWithNeighborsAsync(sheetId);
            TempData["Success"] = result.Message + (mergeAttempts.Any()
                ? " | " + string.Join(" | ", mergeAttempts.Select(FormatMergeMessage))
                : "");

            if (isAjax)
            {
                return Json(new
                {
                    success = true,
                    placementMessage = result.Message,
                    merges = mergeAttempts.Select(m => new
                    {
                        neighborSheetId = m.NeighborSheetId,
                        neighborSheetNumber = m.NeighborSheetNumber,
                        success = m.Success,
                        rmsError = m.RmsErrorMeters,
                        message = m.Message
                    })
                });
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

                var adjacentSheet = await _sheetRepo.GetByIdAsync(adjacentSheetId);
                if (adjacentSheet != null)
                {
                    adjacentSheet.FailureReason = result.FailureReason;
                    await _sheetRepo.SaveChangesAsync();
                }

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

        [HttpGet]
        public async Task<IActionResult> SvgPreview(int projectId)
        {
            var project = await _projectRepo.GetByIdAsync(projectId);
            if (project == null)
                return NotFound("Invalid ProjectID.");

            try
            {
                var svg = await _svgExportService.BuildProjectSvgAsync(projectId);
                return Content(svg, "image/svg+xml");
            }
            catch (InvalidOperationException ex)
            {
                TempData["Error"] = $"Mosaic preview failed: {ex.Message}";
                return RedirectToAction("Workspace", new { projectId });
            }
        }

        // Checks all 4 grid-neighbors of a freshly placed sheet and attempts
        // a merge with each one that already exists. Safe to call repeatedly —
        // MergeSheetsAsync itself rejects when fewer than 2 tie points match.
        private async Task<List<MergeAttemptResult>> TryAutoMergeWithNeighborsAsync(int sheetId)
        {
            var attempts = new List<MergeAttemptResult>();

            var sheet = await _sheetRepo.GetByIdAsync(sheetId);
            if (sheet == null || !sheet.GridRow.HasValue || !sheet.GridCol.HasValue)
                return attempts;

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
                    neighbor.FailureReason = result.FailureReason;
                    await _sheetRepo.SaveChangesAsync();

                    attempts.Add(new MergeAttemptResult(
                        neighbor.SheetID, neighbor.SheetNumber, result.Success,
                        result.RmsErrorMeters, result.Message));
                }
                catch (Exception ex)
                {
                    attempts.Add(new MergeAttemptResult(
                        neighbor.SheetID, neighbor.SheetNumber, false, 0.0, ex.Message));
                }
            }

            return attempts;
        }

        private static string FormatMergeMessage(MergeAttemptResult m) => m.Success
            ? $"Auto-merged with Sheet {m.NeighborSheetNumber} (RMS: {m.RmsErrorMeters:F3})."
            : $"Auto-merge with Sheet {m.NeighborSheetNumber} skipped: {m.Message}";

        private sealed record MergeAttemptResult(
            int NeighborSheetId, string NeighborSheetNumber, bool Success, double RmsErrorMeters, string Message);
    }
}