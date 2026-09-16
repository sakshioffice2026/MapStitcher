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
    public class CadSheetGridService
        : ICadSheetGridService
    {
        private readonly ICadCoordinateInspectionService
            _inspectionService;

        public CadSheetGridService(
            ICadCoordinateInspectionService inspectionService)
        {
            _inspectionService =
                inspectionService;
        }

        public async Task<CadSheetGridResult> BuildGridAsync(
            string directoryPath)
        {
            if (string.IsNullOrWhiteSpace(directoryPath))
            {
                throw new ArgumentException(
                    "CAD directory is required.",
                    nameof(directoryPath));
            }

            if (!Directory.Exists(directoryPath))
            {
                throw new DirectoryNotFoundException(
                    $"CAD directory does not exist: {directoryPath}");
            }

            /*
             * Read BOTH DWG and DXF.
             */
            var files =
                Directory
                    .GetFiles(
                        directoryPath,
                        "*.*",
                        SearchOption.TopDirectoryOnly)
                    .Where(IsCadFile)
                    .ToList();

            if (files.Count == 0)
            {
                return new CadSheetGridResult();
            }

            var sheets =
                new List<CadSheetGridItem>();

            foreach (var file in files)
            {
                try
                {
                    var fileName =
                        Path.GetFileName(file);

                    var indexNumber =
                        GetIndexNumber(
                            fileName);

                    /*
                     * If an index cannot be extracted,
                     * don't discard the DWG.
                     *
                     * It will be assigned a dynamic
                     * fallback index later.
                     */
                    if (indexNumber <= 0)
                    {
                        indexNumber = 0;
                    }

                    var inspectionResult =
                        await _inspectionService
                            .InspectFileAsync(
                                file,
                                fileName);

                    sheets.Add(
                        new CadSheetGridItem
                        {
                            IndexNumber =
                                indexNumber,

                            FileName =
                                fileName,

                            FilePath =
                                file,

                            SheetNumber =
                                inspectionResult
                                    .SheetNumber,

                            MinX =
                                inspectionResult.MinX,

                            MinY =
                                inspectionResult.MinY,

                            MaxX =
                                inspectionResult.MaxX,

                            MaxY =
                                inspectionResult.MaxY,

                            EntityCount =
                                inspectionResult.EntityCount,

                            LaghuReferenceCount =
                                inspectionResult
                                    .LaghuReferences
                                    .Count,

                            LaghuReferences =
                                inspectionResult
                                    .LaghuReferences
                                    .ToList()
                        });
                }
                catch
                {
                    /*
                     * One bad DWG should not prevent
                     * the remaining DWGs from being
                     * processed.
                     */
                }
            }

            /*
             * Assign fallback indexes to files where
             * the filename did not contain an index.
             */
            AssignMissingIndexes(sheets);

            /*
             * Sort by actual index number.
             */
            sheets =
                sheets
                    .OrderBy(x => x.IndexNumber)
                    .ThenBy(x => x.FileName)
                    .ToList();

            /*
             * Calculate the grid dimensions dynamically.
             *
             * Example:
             *
             * 1 file  -> 1 column
             * 2 files -> 2 columns
             * 4 files -> 2 columns
             * 9 files -> 3 columns
             * 10 files -> 4 columns
             * 16 files -> 4 columns
             * 25 files -> 5 columns
             *
             * No hard-coded sheet count.
             */
            var columnCount =
                CalculateColumnCount(
                    sheets.Count);

            /*
             * Put every sheet into the grid.
             */
            foreach (var sheet in sheets)
            {
                var zeroBasedIndex =
                    sheet.IndexNumber - 1;

                /*
                 * If an index is outside the normal
                 * sequence, calculate the position
                 * from the sorted position instead.
                 */
                if (zeroBasedIndex < 0)
                {
                    zeroBasedIndex =
                        sheets.IndexOf(sheet);
                }

                sheet.Row =
                    zeroBasedIndex /
                    columnCount;

                sheet.Column =
                    zeroBasedIndex %
                    columnCount;
            }

            var rowCount =
                sheets.Count == 0
                    ? 0
                    : sheets.Max(x => x.Row) + 1;

            return new CadSheetGridResult
            {
                SheetCount =
                    sheets.Count,

                ColumnCount =
                    columnCount,

                RowCount =
                    rowCount,

                Sheets =
                    sheets
            };
        }

        private static bool IsCadFile(
            string filePath)
        {
            var extension =
                Path.GetExtension(
                    filePath);

            return
                string.Equals(
                    extension,
                    ".dwg",
                    StringComparison.OrdinalIgnoreCase)
                ||
                string.Equals(
                    extension,
                    ".dxf",
                    StringComparison.OrdinalIgnoreCase);
        }

        private static int GetIndexNumber(
            string fileName)
        {
            /*
             * Example:
             *
             * 471403953706906000001.dwg
             *
             * trailing number = 1
             *
             * 471403953706906000003.dwg
             *
             * trailing number = 3
             *
             * This does NOT depend on a fixed filename length.
             */

            var nameWithoutExtension =
                Path.GetFileNameWithoutExtension(
                    fileName);

            if (string.IsNullOrWhiteSpace(
                    nameWithoutExtension))
            {
                return 0;
            }

            var match =
                Regex.Match(
                    nameWithoutExtension,
                    @"(\d+)$");

            if (!match.Success)
            {
                return 0;
            }

            if (int.TryParse(
                    match.Groups[1].Value,
                    out var index))
            {
                return index;
            }

            return 0;
        }

        private static void AssignMissingIndexes(
            List<CadSheetGridItem> sheets)
        {
            var usedIndexes =
                sheets
                    .Where(x =>
                        x.IndexNumber > 0)
                    .Select(x =>
                        x.IndexNumber)
                    .ToHashSet();

            var nextIndex = 1;

            foreach (var sheet in sheets)
            {
                if (sheet.IndexNumber > 0)
                {
                    continue;
                }

                while (
                    usedIndexes.Contains(
                        nextIndex))
                {
                    nextIndex++;
                }

                sheet.IndexNumber =
                    nextIndex;

                usedIndexes.Add(
                    nextIndex);

                nextIndex++;
            }
        }

        private static int CalculateColumnCount(
            int sheetCount)
        {
            if (sheetCount <= 0)
            {
                return 1;
            }

            /*
             * Square-ish dynamic grid.
             *
             * 10 sheets:
             *
             * 1  2  3  4
             * 5  6  7  8
             * 9  10
             *
             * 20 sheets:
             *
             * 1  2  3  4  5
             * ...
             */
            return Math.Max(
                1,
                (int)Math.Ceiling(
                    Math.Sqrt(sheetCount)));
        }
    }
}