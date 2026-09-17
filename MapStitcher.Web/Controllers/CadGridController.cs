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
        private readonly ICadSheetGridService _cadGridService;
        private readonly IWebHostEnvironment _environment;

        public CadGridController(
            ISheetArrangementOrchestrator arrangementService,
            ICadSheetGridService cadGridService,
            IWebHostEnvironment environment)
        {
            _arrangementService = arrangementService;
            _cadGridService = cadGridService;
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
                    ViewBag.Error =
                        "Please select one or more DWG/DXF files.";

                    return View();
                }

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

                        if (extension != ".dwg" &&
                            extension != ".dxf")
                        {
                            skipped.Add(file.FileName);
                            continue;
                        }

                        var originalStem =
                            SanitizeFileNameSegment(
                                Path.GetFileNameWithoutExtension(
                                    file.FileName));

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

                    if (!Directory.GetFiles(tempDirectory).Any())
                    {
                        ViewBag.Error =
                            "No valid DWG/DXF files were accepted.";

                        return View();
                    }

                    var mergeOutputRoot = Path.Combine(
                        _environment.WebRootPath,
                        "exports",
                        "cadgrid");

                    Directory.CreateDirectory(mergeOutputRoot);

                    var sessionsRoot = Path.Combine(
                        mergeOutputRoot,
                        "sessions");

                    Directory.CreateDirectory(sessionsRoot);

                    var sessionId =
                        Guid.NewGuid().ToString("N");

                    var sessionFolder = Path.Combine(
                        sessionsRoot,
                        sessionId);

                    Directory.CreateDirectory(sessionFolder);

                    CleanOldSessions(
                        sessionsRoot,
                        TimeSpan.FromHours(24));

                    foreach (var file in Directory.GetFiles(
                                 tempDirectory))
                    {
                        System.IO.File.Copy(
                            file,
                            Path.Combine(
                                sessionFolder,
                                Path.GetFileName(file)));
                    }

                    var result =
                        await _arrangementService.ArrangeAsync(
                            tempDirectory,
                            mergeOutputRoot);

                    result.SessionId = sessionId;
                    result.SkippedFileNames = skipped;

                    if (result.SheetCount == 0 &&
                        skipped.Count == files.Count)
                    {
                        ViewBag.Error =
                            $"No valid DWG/DXF files were accepted. " +
                            $"Skipped: {string.Join(", ", skipped)}";

                        return View();
                    }

                    // The arrangement service only calculates the layout.
                    // The CAD grid service performs the actual merge/export.
                    await _cadGridService.MergeGridAsync(
                        result,
                        mergeOutputRoot);

                    return View("Result", result);
                }
                finally
                {
                    try
                    {
                        if (Directory.Exists(tempDirectory))
                            Directory.Delete(
                                tempDirectory,
                                true);
                    }
                    catch
                    {
                    }
                }
            }
            catch (Exception ex)
            {
                ViewBag.Error =
                    $"Arrangement/export failed: {ex.Message}";

                return View();
            }
        }

        [HttpGet]
        public Task<IActionResult> ExportGeometryGrid(
            string sessionId)
        {
            return ExportSessionFile(
                sessionId,
                "dxf");
        }

        [HttpGet]
        public Task<IActionResult> ExportGeometryGridDwg(
            string sessionId)
        {
            return ExportSessionFile(
                sessionId,
                "dwg");
        }

        private async Task<IActionResult> ExportSessionFile(
            string sessionId,
            string format)
        {
            if (string.IsNullOrWhiteSpace(sessionId))
                return BadRequest(
                    "Session ID is required.");

            var safeId =
                Path.GetFileName(sessionId);

            if (!string.Equals(
                    safeId,
                    sessionId,
                    StringComparison.Ordinal))
            {
                return BadRequest(
                    "Invalid session ID.");
            }

            var sessionFolder = Path.Combine(
                _environment.WebRootPath,
                "exports",
                "cadgrid",
                "sessions",
                safeId);

            if (!Directory.Exists(sessionFolder))
            {
                return NotFound(
                    "Session files not found or expired. " +
                    "Please re-upload your files.");
            }

            var mergeOutputRoot = Path.Combine(
                _environment.WebRootPath,
                "exports",
                "cadgrid");

            try
            {
                var result =
                    await _arrangementService.ArrangeAsync(
                        sessionFolder,
                        mergeOutputRoot);

                result.SessionId = safeId;

                await _cadGridService.MergeGridAsync(
                    result,
                    mergeOutputRoot);

                if (string.Equals(
                        format,
                        "dwg",
                        StringComparison.OrdinalIgnoreCase))
                {
                    if (string.IsNullOrWhiteSpace(
                            result.MergeOutputFileName))
                    {
                        return BuildExportError(result);
                    }

                    var dwgFileName =
                        Path.ChangeExtension(
                            result.MergeOutputFileName,
                            ".dwg");

                    var dwgPath = Path.Combine(
                        mergeOutputRoot,
                        dwgFileName);

                    if (!System.IO.File.Exists(dwgPath))
                    {
                        return BuildExportError(
                            result,
                            "The merged DWG file was not created.");
                    }

                    var dwgBytes =
                        await System.IO.File.ReadAllBytesAsync(
                            dwgPath);

                    return File(
                        dwgBytes,
                        "application/acad",
                        dwgFileName);
                }

                if (string.IsNullOrWhiteSpace(
                        result.MergeOutputFileName))
                {
                    return BuildExportError(result);
                }

                var dxfPath = Path.Combine(
                    mergeOutputRoot,
                    result.MergeOutputFileName);

                if (!System.IO.File.Exists(dxfPath))
                {
                    return BuildExportError(
                        result,
                        "The merged DXF file was not created.");
                }

                var dxfBytes =
                    await System.IO.File.ReadAllBytesAsync(
                        dxfPath);

                return File(
                    dxfBytes,
                    "application/dxf",
                    result.MergeOutputFileName);
            }
            catch (Exception ex)
            {
                TempData["Error"] =
                    $"Export failed: {ex.Message}";

                return RedirectToAction("Index");
            }
        }

        private IActionResult BuildExportError(
            MapStitcher.Model.CadSheetGridResult result,
            string? fallback = null)
        {
            var diagnostics =
                result.MergeErrors.Count > 0
                    ? string.Join(
                        "; ",
                        result.MergeErrors.TakeLast(5))
                    : fallback ??
                      "No sheets with valid geometry extents were found.";

            TempData["Error"] =
                $"Export failed: {diagnostics}";

            return RedirectToAction("Index");
        }

        private static void CleanOldSessions(
            string sessionsRoot,
            TimeSpan maxAge)
        {
            if (!Directory.Exists(sessionsRoot))
                return;

            var cutoff =
                DateTime.UtcNow - maxAge;

            foreach (var directory in Directory.GetDirectories(
                         sessionsRoot))
            {
                try
                {
                    if (Directory.GetCreationTimeUtc(directory) <
                        cutoff)
                    {
                        Directory.Delete(
                            directory,
                            recursive: true);
                    }
                }
                catch
                {
                }
            }
        }

        [HttpGet]
        public IActionResult DownloadMerged(
            string fileName)
        {
            var safeFileName =
                Path.GetFileName(
                    fileName ?? string.Empty);

            if (string.IsNullOrWhiteSpace(
                    safeFileName))
            {
                return NotFound(
                    "No file specified.");
            }

            var extension =
                Path.GetExtension(safeFileName);

            if (!string.Equals(
                    extension,
                    ".dxf",
                    StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(
                    extension,
                    ".dwg",
                    StringComparison.OrdinalIgnoreCase))
            {
                return BadRequest(
                    "Only DWG and DXF files can be downloaded.");
            }

            var fullPath = Path.Combine(
                _environment.WebRootPath,
                "exports",
                "cadgrid",
                safeFileName);

            if (!System.IO.File.Exists(fullPath))
            {
                return NotFound(
                    "Merged file not found. " +
                    "It may have expired — please re-run the arrangement.");
            }

            var fileBytes =
                System.IO.File.ReadAllBytes(fullPath);

            var contentType =
                string.Equals(
                    extension,
                    ".dwg",
                    StringComparison.OrdinalIgnoreCase)
                    ? "application/acad"
                    : "application/dxf";

            return File(
                fileBytes,
                contentType,
                safeFileName);
        }
    }
}