using MapStitcher.Business.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace MapStitcher.Web.Controllers
{
    public class CadGridController : Controller
    {
        private static string SanitizeFileNameSegment(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "sheet";

            var invalid = Path.GetInvalidFileNameChars();
            var chars = value
                .Where(c => !invalid.Contains(c) && c != '_')
                .ToArray();

            var cleaned = new string(chars).Trim();

            return string.IsNullOrWhiteSpace(cleaned)
                ? "sheet"
                : cleaned;
        }

        private readonly ISheetArrangementOrchestrator _arrangementService;
        private readonly IWebHostEnvironment _environment;

        public CadGridController(
            ISheetArrangementOrchestrator arrangementService,
            IWebHostEnvironment environment)
        {
            _arrangementService = arrangementService;
            _environment = environment;
        }

        [HttpGet]
        public IActionResult Index()
        {
            if (TempData["Error"] is string error)
                ViewBag.Error = error;

            return View();
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Index(IFormFileCollection files)
        {
            try
            {
                if (files == null || files.Count == 0)
                {
                    ViewBag.Error = "Please select one or more DWG/DXF files.";
                    return View();
                }

                // Per-request GUID subdirectory prevents race conditions when
                // multiple users upload simultaneously to the same server.
                var tempDirectory = Path.Combine(
                    _environment.ContentRootPath,
                    "TempCadGrid",
                    Guid.NewGuid().ToString("N"));

                Directory.CreateDirectory(tempDirectory);

                var skipped = new List<string>();

                try
                {
                    foreach (var file in files)
                    {
                        if (file == null || file.Length == 0)
                            continue;

                        var extension = Path
                            .GetExtension(file.FileName)
                            .ToLowerInvariant();

                        if (extension != ".dwg" && extension != ".dxf")
                        {
                            skipped.Add(file.FileName);
                            continue;
                        }

                        var originalStem = SanitizeFileNameSegment(
                            Path.GetFileNameWithoutExtension(file.FileName));

                        var tempFilePath = Path.Combine(
                            tempDirectory,
                            $"{Guid.NewGuid():N}__{originalStem}{extension}");

                        await using var stream = new FileStream(
                            tempFilePath,
                            FileMode.CreateNew,
                            FileAccess.Write,
                            FileShare.None);

                        await file.CopyToAsync(stream);
                    }

                    var mergeOutputRoot = Path.Combine(
                        _environment.WebRootPath,
                        "exports",
                        "cadgrid");

                    // Persist a copy of the uploaded files under a session GUID so the
                    // user can re-export the geometry grid DXF without re-uploading.
                    // Session folders older than 24 h are cleaned up opportunistically.
                    var sessionId = Guid.NewGuid().ToString("N");
                    var sessionFolder = Path.Combine(mergeOutputRoot, "sessions", sessionId);
                    Directory.CreateDirectory(sessionFolder);
                    CleanOldSessions(Path.Combine(mergeOutputRoot, "sessions"), TimeSpan.FromHours(24));

                    foreach (var f in Directory.GetFiles(tempDirectory))
                    {
                        try { System.IO.File.Copy(f, Path.Combine(sessionFolder, Path.GetFileName(f))); }
                        catch { /* non-fatal */ }
                    }

                    var result = await _arrangementService.ArrangeAsync(tempDirectory, mergeOutputRoot);
                    result.SessionId = sessionId;

                    if (result.SheetCount == 0 && skipped.Count == files.Count)
                    {
                        // Nothing usable was even uploaded — no point rendering
                        // an empty Result page.
                        ViewBag.Error =
                            $"No valid DWG/DXF files were accepted. Skipped: {string.Join(", ", skipped)}";
                        return View();
                    }

                    // Even when nothing could be placed (SheetCount == 0), the
                    // arrangement service's diagnostics — which sheet had no
                    // centre number, which neighbour link failed reciprocity,
                    // which referenced sheet wasn't uploaded — are the whole
                    // point of this screen. Swallowing them behind a generic
                    // "No sheets could be arranged." error hides exactly the
                    // information needed to fix the input files.
                    result.SkippedFileNames = skipped;
                    return View("Result", result);
                }
                finally
                {
                    try
                    {
                        if (Directory.Exists(tempDirectory))
                            Directory.Delete(tempDirectory, true);
                    }
                    catch
                    {
                        // Ignore cleanup failure.
                    }
                }
            }
            catch (Exception ex)
            {
                ViewBag.Error = $"Arrangement failed: {ex.Message}";
                return View();
            }
        }

        // Re-export the geometry grid DXF from persistent session files.
        // Called by the "Export Merged Sheets" button on the Result page when
        // the first-attempt stitch failed (MergeOutputFileName was null) or when
        // the user returns to the page and wants a fresh download.
        [HttpGet]
        public async Task<IActionResult> ExportGeometryGrid(string sessionId)
        {
            if (string.IsNullOrWhiteSpace(sessionId))
                return BadRequest("Session ID is required.");

            // Path.GetFileName strips any directory traversal characters.
            var safeId = Path.GetFileName(sessionId);
            var sessionFolder = Path.Combine(
                _environment.WebRootPath, "exports", "cadgrid", "sessions", safeId);

            if (!Directory.Exists(sessionFolder))
                return NotFound("Session files not found or expired. Please re-upload your files.");

            var mergeOutputRoot = Path.Combine(_environment.WebRootPath, "exports", "cadgrid");

            try
            {
                var result = await _arrangementService.ArrangeAsync(sessionFolder, mergeOutputRoot);

                if (string.IsNullOrWhiteSpace(result.MergeOutputFileName))
                {
                    var diagnostics = result.MergeErrors.Count > 0
                        ? string.Join("; ", result.MergeErrors.TakeLast(5))
                        : "No sheets with valid geometry extents were found.";
                    TempData["Error"] = $"Export failed: {diagnostics}";
                    return RedirectToAction("Index");
                }

                var fullPath = Path.Combine(mergeOutputRoot, result.MergeOutputFileName);
                var fileBytes = await System.IO.File.ReadAllBytesAsync(fullPath);
                return File(fileBytes, "application/dxf", result.MergeOutputFileName);
            }
            catch (Exception ex)
            {
                TempData["Error"] = $"Export failed: {ex.Message}";
                return RedirectToAction("Index");
            }
        }

        // Deletes session subdirectories older than the given age.
        // Called opportunistically on each upload to prevent unbounded disk growth.
        private static void CleanOldSessions(string sessionsRoot, TimeSpan maxAge)
        {
            if (!Directory.Exists(sessionsRoot))
                return;

            var cutoff = DateTime.UtcNow - maxAge;
            foreach (var dir in Directory.GetDirectories(sessionsRoot))
            {
                try
                {
                    if (Directory.GetCreationTimeUtc(dir) < cutoff)
                        Directory.Delete(dir, recursive: true);
                }
                catch { /* non-fatal */ }
            }
        }

        [HttpGet]
        public IActionResult DownloadMerged(string fileName)
        {
            // Path.GetFileName strips any directory segments, so a fileName
            // like "..\..\secrets.txt" collapses to "secrets.txt" and can only
            // ever resolve inside the cadgrid exports folder below.
            var safeFileName = Path.GetFileName(fileName ?? string.Empty);

            if (string.IsNullOrWhiteSpace(safeFileName))
                return NotFound("No file specified.");

            var fullPath = Path.Combine(
                _environment.WebRootPath,
                "exports",
                "cadgrid",
                safeFileName);

            if (!System.IO.File.Exists(fullPath))
                return NotFound("Merged file not found. It may have expired — please re-run the arrangement.");

            var fileBytes = System.IO.File.ReadAllBytes(fullPath);
            return File(fileBytes, "application/dxf", safeFileName);
        }
    }
}