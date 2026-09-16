using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using CSMath;
using MapStitcher.Business.Contracts;
using MapStitcher.Model;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace MapStitcher.Business.Services
{
    public class CadSheetGridService : ICadSheetGridService
    {
        private const double CellGap = 25.0;
        private const double DefaultCellSize = 500.0;

        private readonly ICadCoordinateInspectionService _inspectionService;

        public CadSheetGridService(ICadCoordinateInspectionService inspectionService)
        {
            _inspectionService = inspectionService;
        }

        public async Task<CadSheetGridResult> BuildGridAsync(string directoryPath)
        {
            if (string.IsNullOrWhiteSpace(directoryPath))
                throw new ArgumentException("CAD directory is required.", nameof(directoryPath));

            if (!Directory.Exists(directoryPath))
                throw new DirectoryNotFoundException($"CAD directory does not exist: {directoryPath}");

            var files = Directory
                .GetFiles(directoryPath, "*.*", SearchOption.TopDirectoryOnly)
                .Where(IsCadFile)
                .ToList();

            if (files.Count == 0)
                return new CadSheetGridResult();

            var sheets = new List<CadSheetGridItem>();

            foreach (var file in files)
            {
                try
                {
                    var fileName = Path.GetFileName(file);
                    var indexNumber = GetIndexNumber(fileName);

                    var inspectionResult = await _inspectionService.InspectFileAsync(file, fileName);

                    sheets.Add(new CadSheetGridItem
                    {
                        IndexNumber = indexNumber,
                        FileName = fileName,
                        FilePath = file,
                        SheetNumber = inspectionResult.SheetNumber,
                        MinX = inspectionResult.MinX,
                        MinY = inspectionResult.MinY,
                        MaxX = inspectionResult.MaxX,
                        MaxY = inspectionResult.MaxY,
                        EntityCount = inspectionResult.EntityCount,
                        LaghuReferenceCount = inspectionResult.LaghuReferences.Count,
                        LaghuReferences = inspectionResult.LaghuReferences.ToList()
                    });
                }
                catch
                {
                    // One bad DWG should not block the rest.
                }
            }

            // Deduplicate colliding filename-derived indexes before gap-filling.
            DeduplicateIndexes(sheets);
            AssignMissingIndexes(sheets);

            sheets = sheets
                .OrderBy(x => x.IndexNumber)
                .ThenBy(x => x.FileName)
                .ToList();

            var columnCount = CalculateColumnCount(sheets.Count);

            for (int i = 0; i < sheets.Count; i++)
            {
                var sheet = sheets[i];
                var zeroBasedIndex = sheet.IndexNumber - 1;

                // Large filename trailing numbers (e.g. "000042" in a 3-file batch)
                // would produce a huge sparse grid. Fall back to sorted position whenever
                // the raw index would place the sheet outside the actual list bounds.
                if (zeroBasedIndex < 0 || zeroBasedIndex >= sheets.Count)
                    zeroBasedIndex = i;

                sheet.Row = zeroBasedIndex / columnCount;
                sheet.Column = zeroBasedIndex % columnCount;
            }

            var rowCount = sheets.Count == 0 ? 0 : sheets.Max(x => x.Row) + 1;

            return new CadSheetGridResult
            {
                SheetCount = sheets.Count,
                ColumnCount = columnCount,
                RowCount = rowCount,
                Sheets = sheets
            };
        }

        public Task MergeGridAsync(CadSheetGridResult result, string outputDirectory)
        {
            if (result.Sheets.Count == 0)
            {
                result.MergeErrors.Add("No sheets to merge.");
                return Task.CompletedTask;
            }

            int columnCount = result.ColumnCount > 0
                ? result.ColumnCount
                : result.Sheets.Max(s => s.Column) + 1;

            int rowCount = result.RowCount > 0
                ? result.RowCount
                : result.Sheets.Max(s => s.Row) + 1;

            // Per-column max width and per-row max height so that sheets of
            // different sizes never overlap on the merged canvas.
            var colMaxWidth = new double[columnCount];
            var rowMaxHeight = new double[rowCount];

            foreach (var sheet in result.Sheets)
            {
                var w = sheet.Width > 0 ? sheet.Width : DefaultCellSize;
                var h = sheet.Height > 0 ? sheet.Height : DefaultCellSize;

                if (sheet.Column < columnCount && w > colMaxWidth[sheet.Column])
                    colMaxWidth[sheet.Column] = w;

                if (sheet.Row < rowCount && h > rowMaxHeight[sheet.Row])
                    rowMaxHeight[sheet.Row] = h;
            }

            // Cumulative left-edge offset for each column.
            var colOffset = new double[columnCount];
            for (int c = 1; c < columnCount; c++)
                colOffset[c] = colOffset[c - 1] + colMaxWidth[c - 1] + CellGap;

            // Cumulative top-edge offset for each row (positive = downward in
            // screen space; negated when applied to CAD Y, which increases upward).
            var rowOffset = new double[rowCount];
            for (int r = 1; r < rowCount; r++)
                rowOffset[r] = rowOffset[r - 1] + rowMaxHeight[r - 1] + CellGap;

            var masterDocument = new CadDocument();
            int mergedCount = 0;

            foreach (var sheet in result.Sheets)
            {
                if (!File.Exists(sheet.FilePath))
                {
                    result.MergeErrors.Add($"{sheet.FileName}: source file not found, skipped.");
                    continue;
                }

                try
                {
                    // Normalise sheet to its own CAD origin, then offset to grid position.
                    // originX/originY are the sheet's own bottom-left / top-right in CAD space.
                    double originX = sheet.Width > 0 ? sheet.MinX : 0.0;
                    double originY = sheet.Height > 0 ? sheet.MaxY : 0.0;

                    // tx: move sheet's left edge to column left edge.
                    // ty: move sheet's top edge to row top edge (negative because CAD Y is up).
                    double tx = colOffset[sheet.Column] - originX;
                    double ty = -rowOffset[sheet.Row] - originY;

                    var translation = Transform.CreateTranslation(new XYZ(tx, ty, 0));

                    var ext = Path.GetExtension(sheet.FilePath).ToLowerInvariant();
                    var sourceDoc = ext == ".dxf"
                        ? DxfReader.Read(sheet.FilePath)
                        : DwgReader.Read(sheet.FilePath);

                    foreach (var sourceEntity in sourceDoc.Entities.ToList())
                    {
                        var clone = (Entity)sourceEntity.Clone();
                        clone.ApplyTransform(translation);
                        masterDocument.Entities.Add(clone);
                    }

                    mergedCount++;
                }
                catch (Exception ex)
                {
                    result.MergeErrors.Add($"{sheet.FileName}: merge failed — {ex.Message}");
                }
            }

            if (mergedCount == 0)
            {
                result.MergeErrors.Add("No sheets could be cloned into the master document.");
                return Task.CompletedTask;
            }

            Directory.CreateDirectory(outputDirectory);
            var fileName = $"merged_{DateTime.UtcNow:yyyyMMdd_HHmmss}.dxf";
            DxfWriter.Write(Path.Combine(outputDirectory, fileName), masterDocument);
            result.MergeOutputFileName = fileName;

            return Task.CompletedTask;
        }

        // ── Private helpers ───────────────────────────────────────────────────

        private static bool IsCadFile(string filePath)
        {
            var ext = Path.GetExtension(filePath);
            return string.Equals(ext, ".dwg", StringComparison.OrdinalIgnoreCase)
                || string.Equals(ext, ".dxf", StringComparison.OrdinalIgnoreCase);
        }

        private static int GetIndexNumber(string fileName)
        {
            var name = Path.GetFileNameWithoutExtension(fileName);
            if (string.IsNullOrWhiteSpace(name)) return 0;

            var match = Regex.Match(name, @"(\d+)$");
            if (!match.Success) return 0;

            return int.TryParse(match.Groups[1].Value, out var index) ? index : 0;
        }

        // Resets IndexNumber to 0 for any sheet whose number was already claimed
        // by an earlier sheet in the list. AssignMissingIndexes will then give the
        // duplicate a fresh, non-colliding slot.
        private static void DeduplicateIndexes(List<CadSheetGridItem> sheets)
        {
            var seen = new HashSet<int>();

            foreach (var sheet in sheets)
            {
                if (sheet.IndexNumber > 0 && !seen.Add(sheet.IndexNumber))
                    sheet.IndexNumber = 0;
            }
        }

        private static void AssignMissingIndexes(List<CadSheetGridItem> sheets)
        {
            var usedIndexes = sheets
                .Where(x => x.IndexNumber > 0)
                .Select(x => x.IndexNumber)
                .ToHashSet();

            int nextIndex = 1;

            foreach (var sheet in sheets)
            {
                if (sheet.IndexNumber > 0) continue;

                while (usedIndexes.Contains(nextIndex))
                    nextIndex++;

                sheet.IndexNumber = nextIndex;
                usedIndexes.Add(nextIndex);
                nextIndex++;
            }
        }

        private static int CalculateColumnCount(int sheetCount)
        {
            if (sheetCount <= 0) return 1;
            return Math.Max(1, (int)Math.Ceiling(Math.Sqrt(sheetCount)));
        }
    }
}