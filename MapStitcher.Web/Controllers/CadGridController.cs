using MapStitcher.Business.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace MapStitcher.Web.Controllers
{
    public class CadGridController : Controller
    {
        private readonly ICadSheetGridService
            _gridService;

        private readonly IWebHostEnvironment
            _environment;

        public CadGridController(
            ICadSheetGridService gridService,
            IWebHostEnvironment environment)
        {
            _gridService =
                gridService;

            _environment =
                environment;
        }

        [HttpGet]
        public IActionResult Index()
        {
            return View();
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Index(
            IFormFileCollection files)
        {
            try
            {
                if (files == null ||
                    files.Count == 0)
                {
                    ViewBag.Error =
                        "Please select one or more DWG/DXF files.";

                    return View();
                }

                var tempDirectory =
                    Path.Combine(
                        _environment.ContentRootPath,
                        "TempCadGrid");

                Directory.CreateDirectory(
                    tempDirectory);

                try
                {
                    foreach (var file in files)
                    {
                        if (file == null ||
                            file.Length == 0)
                        {
                            continue;
                        }

                        var extension =
                            Path.GetExtension(
                                file.FileName)
                                .ToLowerInvariant();

                        if (extension != ".dwg" &&
                            extension != ".dxf")
                        {
                            continue;
                        }

                        var tempFileName =
                            $"{Guid.NewGuid():N}{extension}";

                        var tempFilePath =
                            Path.Combine(
                                tempDirectory,
                                tempFileName);

                        await using (
                            var stream =
                                new FileStream(
                                    tempFilePath,
                                    FileMode.CreateNew,
                                    FileAccess.Write,
                                    FileShare.None))
                        {
                            await file.CopyToAsync(
                                stream);
                        }
                    }

                    var result =
                        await _gridService
                            .BuildGridAsync(
                                tempDirectory);

                    return View(
                        "Result",
                        result);
                }
                finally
                {
                    /*
                     * Delete all temporary CAD files.
                     */
                    try
                    {
                        if (Directory.Exists(
                                tempDirectory))
                        {
                            Directory.Delete(
                                tempDirectory,
                                true);
                        }
                    }
                    catch
                    {
                        // Ignore cleanup failure.
                    }
                }
            }
            catch (Exception ex)
            {
                ViewBag.Error =
                    $"Grid generation failed: {ex.Message}";

                return View();
            }
        }
    }
}