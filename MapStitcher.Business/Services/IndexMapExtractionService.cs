using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using MapStitcher.Business.Contracts;
using MapStitcher.Model;
using MapStitcher.Utilities;
using System.Text.RegularExpressions;

namespace MapStitcher.Business.Services
{
    public class IndexMapExtractionService : IIndexMapExtractionService
    {
        private const string HatchLayer = "Sym_Hatch";
        private const string TitleLayer = "Sym_Title";

        private static readonly HashSet<string> NoiseTokens =
            new(StringComparer.OrdinalIgnoreCase)
            {
                "RIVER",
                "VILLAGE BOUNDARY",
                "BOUNDARY",
                "NALA",
                "ROAD",
                "-",
                "--"
            };

        private static readonly Regex AlnumRegex =
            new(@"[A-Za-z0-9]+", RegexOptions.Compiled);

        public Task<IndexMapNeighbors> ExtractAsync(
            string filePath,
            string indexLayerName)
        {
            if (!File.Exists(filePath))
                throw new FileNotFoundException(
                    "CAD file was not found.",
                    filePath);

            var ext = Path.GetExtension(filePath).ToLowerInvariant();

            CadDocument doc =
                ext == ".dwg"
                    ? DwgReader.Read(filePath)
                    : DxfReader.Read(filePath);

            var result = new IndexMapNeighbors();

            /*
             * ---------------------------------------------------------
             * 1. Find the spatial/index-map area from Sym_Hatch
             * ---------------------------------------------------------
             */

            double minX = double.MaxValue;
            double minY = double.MaxValue;
            double maxX = double.MinValue;
            double maxY = double.MinValue;

            int hatchCount = 0;

            foreach (var entity in doc.Entities)
            {
                var layerName =
                    entity.Layer?.Name ?? string.Empty;

                if (!string.Equals(
                        layerName,
                        HatchLayer,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (entity is not Hatch hatch)
                    continue;

                var bounds = hatch.GetBoundingBox();

                if (bounds == null)
                    continue;

                minX = Math.Min(minX, bounds.Min.X);
                minY = Math.Min(minY, bounds.Min.Y);
                maxX = Math.Max(maxX, bounds.Max.X);
                maxY = Math.Max(maxY, bounds.Max.Y);

                hatchCount++;
            }

            if (hatchCount == 0)
            {
                result.Warnings.Add(
                    $"No HATCH geometry found on layer '{HatchLayer}'.");

                return Task.FromResult(result);
            }

            if (minX == double.MaxValue ||
                minY == double.MaxValue ||
                maxX == double.MinValue ||
                maxY == double.MinValue)
            {
                result.Warnings.Add(
                    $"Unable to calculate bounding box for layer '{HatchLayer}'.");

                return Task.FromResult(result);
            }

            double width = maxX - minX;
            double height = maxY - minY;

            if (width <= 0 || height <= 0)
            {
                result.Warnings.Add(
                    "Sym_Hatch spatial area has zero width or height.");

                return Task.FromResult(result);
            }

            /*
             * ---------------------------------------------------------
             * 2. Divide the spatial area into a 3 x 3 grid
             * ---------------------------------------------------------
             */

            double thirdWidth = width / 3.0;
            double thirdHeight = height / 3.0;

            var cellTexts = new Dictionary<string, List<string>>
            {
                ["Center"] = new(),
                ["Top"] = new(),
                ["Bottom"] = new(),
                ["Left"] = new(),
                ["Right"] = new()
            };

            /*
             * ---------------------------------------------------------
             * 3. Read sheet numbers from Sym_Title
             * ---------------------------------------------------------
             */

            foreach (var entity in doc.Entities)
            {
                var layerName =
                    entity.Layer?.Name ?? string.Empty;

                if (!string.Equals(
                        layerName,
                        TitleLayer,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                double x;
                double y;
                string rawText;

                switch (entity)
                {
                    case TextEntity text:
                        x = text.InsertPoint.X;
                        y = text.InsertPoint.Y;
                        rawText = text.Value;
                        break;

                    case MText mtext:
                        x = mtext.InsertPoint.X;
                        y = mtext.InsertPoint.Y;
                        rawText = mtext.Value;
                        break;

                    default:
                        continue;
                }

                var cleaned = Sanitize(rawText);

                if (cleaned == null)
                    continue;

                /*
                 * Ignore titles outside the Sym_Hatch spatial area.
                 */
                if (x < minX ||
                    x > maxX ||
                    y < minY ||
                    y > maxY)
                {
                    continue;
                }

                int column =
                    ClampThird(
                        (x - minX) / thirdWidth);

                int row =
                    ClampThird(
                        (y - minY) / thirdHeight);

                string? cell = (row, column) switch
                {
                    (1, 1) => "Center",

                    // CAD coordinates: Y increases upward.
                    (2, 1) => "Top",

                    (0, 1) => "Bottom",

                    (1, 0) => "Left",

                    (1, 2) => "Right",

                    _ => null
                };

                if (cell != null)
                    cellTexts[cell].Add(cleaned);
            }

            /*
             * ---------------------------------------------------------
             * 4. Resolve sheet numbers
             * ---------------------------------------------------------
             */

            result.CenterSheetNumber =
                Pick(
                    cellTexts["Center"],
                    "Center",
                    result.Warnings);

            result.TopSheetNumber =
                Pick(
                    cellTexts["Top"],
                    "Top",
                    result.Warnings);

            result.BottomSheetNumber =
                Pick(
                    cellTexts["Bottom"],
                    "Bottom",
                    result.Warnings);

            result.LeftSheetNumber =
                Pick(
                    cellTexts["Left"],
                    "Left",
                    result.Warnings);

            result.RightSheetNumber =
                Pick(
                    cellTexts["Right"],
                    "Right",
                    result.Warnings);

            if (result.CenterSheetNumber == null)
            {
                result.Warnings.Add(
                    $"No sheet number found in Center cell on layer '{TitleLayer}'.");
            }

            return Task.FromResult(result);
        }

        private static int ClampThird(double ratio)
        {
            if (ratio < 1.0 / 3.0)
                return 0;

            if (ratio < 2.0 / 3.0)
                return 1;

            return 2;
        }

        private static string? Pick(
            List<string> candidates,
            string cellName,
            List<string> warnings)
        {
            if (candidates.Count == 0)
                return null;

            var distinct =
                candidates
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

            if (distinct.Count > 1)
            {
                warnings.Add(
                    $"{cellName} cell has conflicting labels: " +
                    $"{string.Join(", ", distinct)}. Using first.");
            }

            return distinct[0];
        }

        private static string? Sanitize(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return null;

            var text =
                DecodeText(raw).Trim();

            foreach (var noise in NoiseTokens)
            {
                text = Regex.Replace(
                    text,
                    Regex.Escape(noise),
                    "",
                    RegexOptions.IgnoreCase);
            }

            text = text.Trim(
                ' ',
                '-',
                '.',
                ':',
                '\t');

            return AlnumRegex.IsMatch(text)
                ? text
                : null;
        }

        private static string DecodeText(string? raw)
        {
            if (string.IsNullOrEmpty(raw))
                return string.Empty;

            try
            {
                return DxfUnicodeEscapeDecoder.Decode(raw);
            }
            catch
            {
                return raw;
            }
        }
    }
}
