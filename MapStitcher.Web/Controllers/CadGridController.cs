using MapStitcher.Business.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace MapStitcher.Web.Controllers
{
    public class CadGridController : Controller
    {
        private readonly ICadSheetGridService _gridService;
        private readonly IWebHostEnvironment _environment;

        public CadGridController(
            ICadSheetGridService gridService,
            IWebHostEnvironment environment)
        {
            _gridService = gridService;
            _environment = environment;
        }

        [HttpGet]
        public IActionResult Index()
        {
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

                        var tempFilePath = Path.Combine(
                            tempDirectory,
                            $"{Guid.NewGuid():N}{extension}");

                        await using var stream = new FileStream(
                            tempFilePath,
                            FileMode.CreateNew,
                            FileAccess.Write,
                            FileShare.None);

                        await file.CopyToAsync(stream);
                    }

                    var result = await _gridService.BuildGridAsync(tempDirectory);
                    result.SkippedFileNames = skipped;

                    if (result.SheetCount == 0)
                    {
                        ViewBag.Error = skipped.Count > 0
                            ? $"No valid DWG/DXF files were accepted. " +
                              $"Skipped: {string.Join(", ", skipped)}"
                            : "No DWG/DXF files found after upload.";
                        return View();
                    }

                    // Merge while temp files are still on disk (before finally deletes them).
                    var exportsDir = Path.Combine(
                        _environment.ContentRootPath,
                        "CadGridExports");

                    await _gridService.MergeGridAsync(result, exportsDir);

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
                ViewBag.Error = $"Grid generation failed: {ex.Message}";
                return View();
            }
        }

        [HttpGet]
        public IActionResult Download(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName)
                || fileName.Contains("..")
                || fileName.Contains('/')
                || fileName.Contains('\\'))
            {
                return BadRequest("Invalid file name.");
            }

            var filePath = Path.Combine(
                _environment.ContentRootPath,
                "CadGridExports",
                fileName);

            if (!System.IO.File.Exists(filePath))
                return NotFound();

            return PhysicalFile(filePath, "application/octet-stream", fileName);
        }
    }
}