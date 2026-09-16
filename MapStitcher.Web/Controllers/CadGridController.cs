using MapStitcher.Business.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace MapStitcher.Web.Controllers
{
    public class CadGridController : Controller
    {
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

                    var result = await _arrangementService.ArrangeAsync(tempDirectory);

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
    }
}