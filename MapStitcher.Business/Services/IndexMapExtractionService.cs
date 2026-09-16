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
        private static readonly HashSet<string> NoiseTokens = new(StringComparer.OrdinalIgnoreCase)
        {
            "RIVER", "VILLAGE BOUNDARY", "BOUNDARY", "NALA", "ROAD", "-", "--"
        };

        private static readonly Regex AlnumRegex = new(@"[A-Za-z0-9]+", RegexOptions.Compiled);

        public Task<IndexMapNeighbors> ExtractAsync(string filePath, string indexLayerName)
        {
            if (!File.Exists(filePath))
                throw new FileNotFoundException("CAD file was not found.", filePath);

            var ext = Path.GetExtension(filePath).ToLowerInvariant();
            CadDocument doc = ext == ".dwg" ? DwgReader.Read(filePath) : DxfReader.Read(filePath);

            var linePoints = new List<(double X, double Y)>();
            var textPoints = new List<(double X, double Y, string Text)>();

            foreach (var entity in doc.Entities)
            {
                var layerName = entity.Layer?.Name ?? string.Empty;
                if (!string.Equals(layerName, indexLayerName, StringComparison.OrdinalIgnoreCase))
                    continue;

                switch (entity)
                {
                    case Line line:
                        linePoints.Add((line.StartPoint.X, line.StartPoint.Y));
                        linePoints.Add((line.EndPoint.X, line.EndPoint.Y));
                        break;

                    case LwPolyline poly:
                        foreach (var v in poly.Vertices)
                            linePoints.Add((v.Location.X, v.Location.Y));
                        break;

                    case TextEntity t:
                        textPoints.Add((t.InsertPoint.X, t.InsertPoint.Y, DecodeText(t.Value)));
                        break;

                    case MText mt:
                        textPoints.Add((mt.InsertPoint.X, mt.InsertPoint.Y, DecodeText(mt.Value)));
                        break;
                }
            }

            var result = new IndexMapNeighbors();

            if (linePoints.Count == 0)
            {
                result.Warnings.Add($"No LINE/LWPOLYLINE geometry found on layer '{indexLayerName}'.");
                return Task.FromResult(result);
            }

            double minX = linePoints.Min(p => p.X), maxX = linePoints.Max(p => p.X);
            double minY = linePoints.Min(p => p.Y), maxY = linePoints.Max(p => p.Y);
            double thirdW = (maxX - minX) / 3.0;
            double thirdH = (maxY - minY) / 3.0;

            if (thirdW <= 0 || thirdH <= 0)
            {
                result.Warnings.Add("Index grid bounding box is degenerate (zero width/height).");
                return Task.FromResult(result);
            }

            var cellTexts = new Dictionary<string, List<string>>
            {
                ["Center"] = new(),
                ["Top"] = new(),
                ["Bottom"] = new(),
                ["Left"] = new(),
                ["Right"] = new()
            };

            var cornerCount = 0;

            foreach (var (x, y, raw) in textPoints)
            {
                var cleaned = Sanitize(raw);
                if (cleaned == null) continue;

                int col = ClampThird((x - minX) / thirdW);
                int row = ClampThird((y - minY) / thirdH); // row 0 = bottom, 2 = top (CAD Y-up)

                string? cell = (row, col) switch
                {
                    (1, 1) => "Center",
                    (2, 1) => "Top",
                    (0, 1) => "Bottom",
                    (1, 0) => "Left",
                    (1, 2) => "Right",
                    (2, 0) or (2, 2) or (0, 0) or (0, 2) => "Corner",
                    _ => null
                };

                if (cell == "Corner")
                {
                    cornerCount++;
                    continue; // corners are not part of the neighbor model, ignored by design
                }

                if (cell != null)
                    cellTexts[cell].Add(cleaned);
            }

            if (cornerCount > 0)
                result.Warnings.Add($"{cornerCount} label(s) in corner cells ignored (not part of Top/Bottom/Left/Right/Center model).");

            result.CenterSheetNumber = Pick(cellTexts["Center"], "Center", result.Warnings);
            result.TopSheetNumber = Pick(cellTexts["Top"], "Top", result.Warnings);
            result.BottomSheetNumber = Pick(cellTexts["Bottom"], "Bottom", result.Warnings);
            result.LeftSheetNumber = Pick(cellTexts["Left"], "Left", result.Warnings);
            result.RightSheetNumber = Pick(cellTexts["Right"], "Right", result.Warnings);

            return Task.FromResult(result);
        }

        private static int ClampThird(double ratio)
        {
            if (ratio < 1.0 / 3.0) return 0;
            if (ratio < 2.0 / 3.0) return 1;
            return 2;
        }

        private static string? Pick(List<string> candidates, string cellName, List<string> warnings)
        {
            if (candidates.Count == 0) return null;

            var distinct = candidates.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (distinct.Count > 1)
                warnings.Add($"{cellName} cell has conflicting labels: {string.Join(", ", distinct)}. Using first.");

            return distinct[0];
        }

        private static string? Sanitize(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;

            var text = DecodeText(raw).Trim();

            foreach (var noise in NoiseTokens)
                text = Regex.Replace(text, Regex.Escape(noise), "", RegexOptions.IgnoreCase);

            text = text.Trim(' ', '-', '.', ':', '\t');

            return AlnumRegex.IsMatch(text) ? text : null;
        }

        private static string DecodeText(string? raw)
        {
            if (string.IsNullOrEmpty(raw)) return string.Empty;
            try { return DxfUnicodeEscapeDecoder.Decode(raw); }
            catch { return raw; }
        }
    }
}