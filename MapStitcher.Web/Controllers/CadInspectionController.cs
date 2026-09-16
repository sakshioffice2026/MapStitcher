using MapStitcher.Business.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace MapStitcher.Web.Controllers
{
    public class CadInspectionController : Controller
    {
        private readonly ICadCoordinateInspectionService
            _inspectionService;

        private readonly IWebHostEnvironment
            _environment;

        public CadInspectionController(
            ICadCoordinateInspectionService inspectionService,
            IWebHostEnvironment environment)
        {
            _inspectionService =
                inspectionService;

            _environment =
                environment;
        }

        [HttpGet]
        public IActionResult NewSheet()
        {
            return View();
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> NewSheet(
            IFormFile file)
        {
            try
            {
                if (file == null)
                {
                    ViewBag.Error =
                        "Please select a CAD file.";

                    return View();
                }

                if (file.Length == 0)
                {
                    ViewBag.Error =
                        "The selected file is empty.";

                    return View();
                }

                var extension =
                    Path.GetExtension(
                        file.FileName)
                        .ToLowerInvariant();

                if (extension != ".dwg" &&
                    extension != ".dxf")
                {
                    ViewBag.Error =
                        "Only DWG and DXF files are supported.";

                    return View();
                }

                var tempDirectory =
                    Path.Combine(
                        _environment.ContentRootPath,
                        "TempCadInspection");

                Directory.CreateDirectory(
                    tempDirectory);

                var tempFileName =
                    $"{Guid.NewGuid():N}{extension}";

                var tempFilePath =
                    Path.Combine(
                        tempDirectory,
                        tempFileName);

                try
                {
                    await using (
                        var stream =
                            new FileStream(
                                tempFilePath,
                                FileMode.CreateNew,
                                FileAccess.Write,
                                FileShare.None))
                    {
                        await file.CopyToAsync(stream);
                    }

                    var result =
                        await _inspectionService
                            .InspectFileAsync(
                                tempFilePath,
                                file.FileName);

                    return View(
                        "NewSheetResult",
                        result);
                }
                finally
                {
                    try
                    {
                        if (
                            System.IO.File.Exists(
                                tempFilePath))
                        {
                            System.IO.File.Delete(
                                tempFilePath);
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
                    $"Inspection failed: {ex.Message}";

                return View();
            }
        }
    }
}