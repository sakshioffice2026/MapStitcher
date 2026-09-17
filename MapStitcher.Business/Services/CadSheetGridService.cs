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

        public CadSheetGridService(
            ICadCoordinateInspectionService inspectionService)
        {
            _inspectionService = inspectionService;
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

            var files = Directory
                .GetFiles(
                    directoryPath,
                    "*.*",
                    SearchOption.TopDirectoryOnly)
                .Where(IsCadFile)
                .ToList();

            if (files.Count == 0)
                return new CadSheetGridResult();

            var sheets =
                new List<CadSheetGridItem>();

            foreach (var file in files)
            {
                try
                {
                    var fileName =
                        Path.GetFileName(file);

                    var indexNumber =
                        GetIndexNumber(fileName);

                    var inspectionResult =
                        await _inspectionService.InspectFileAsync(
                            file,
                            fileName);

                    sheets.Add(
                        new CadSheetGridItem
                        {
                            IndexNumber = indexNumber,
                            FileName = fileName,
                            FilePath = file,
                            SheetNumber =
                                inspectionResult.SheetNumber,
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
                    // Keep processing other CAD files.
                }
            }

            DeduplicateIndexes(sheets);
            AssignMissingIndexes(sheets);

            sheets = sheets
                .OrderBy(x => x.IndexNumber)
                .ThenBy(x => x.FileName)
                .ToList();

            var columnCount =
                CalculateColumnCount(
                    sheets.Count);

            for (int i = 0;
                 i < sheets.Count;
                 i++)
            {
                var sheet =
                    sheets[i];

                var zeroBasedIndex =
                    sheet.IndexNumber - 1;

                if (zeroBasedIndex < 0 ||
                    zeroBasedIndex >= sheets.Count)
                {
                    zeroBasedIndex = i;
                }

                sheet.Row =
                    zeroBasedIndex / columnCount;

                sheet.Column =
                    zeroBasedIndex % columnCount;
            }

            var rowCount =
                sheets.Count == 0
                    ? 0
                    : sheets.Max(x => x.Row) + 1;

            return new CadSheetGridResult
            {
                SheetCount = sheets.Count,
                ColumnCount = columnCount,
                RowCount = rowCount,
                Sheets = sheets
            };
        }

        public Task MergeGridAsync(
            CadSheetGridResult result,
            string outputDirectory)
        {
            if (result == null)
            {
                throw new ArgumentNullException(
                    nameof(result));
            }

            if (string.IsNullOrWhiteSpace(
                    outputDirectory))
            {
                throw new ArgumentException(
                    "Output directory is required.",
                    nameof(outputDirectory));
            }

            if (result.Sheets == null ||
                result.Sheets.Count == 0)
            {
                result.MergeErrors.Add(
                    "No sheets to merge.");

                return Task.CompletedTask;
            }

            var columnCount =
                result.ColumnCount > 0
                    ? result.ColumnCount
                    : result.Sheets.Max(
                        x => x.Column) + 1;

            var rowCount =
                result.RowCount > 0
                    ? result.RowCount
                    : result.Sheets.Max(
                        x => x.Row) + 1;

            var columnWidths =
                new double[columnCount];

            var rowHeights =
                new double[rowCount];

            foreach (var sheet in result.Sheets)
            {
                var width =
                    sheet.MaxX > sheet.MinX
                        ? sheet.MaxX - sheet.MinX
                        : DefaultCellSize;

                var height =
                    sheet.MaxY > sheet.MinY
                        ? sheet.MaxY - sheet.MinY
                        : DefaultCellSize;

                if (sheet.Column >= 0 &&
                    sheet.Column < columnCount)
                {
                    columnWidths[sheet.Column] =
                        Math.Max(
                            columnWidths[sheet.Column],
                            width);
                }

                if (sheet.Row >= 0 &&
                    sheet.Row < rowCount)
                {
                    rowHeights[sheet.Row] =
                        Math.Max(
                            rowHeights[sheet.Row],
                            height);
                }
            }

            var columnOffsets =
                new double[columnCount];

            for (int i = 1;
                 i < columnCount;
                 i++)
            {
                columnOffsets[i] =
                    columnOffsets[i - 1] +
                    columnWidths[i - 1] +
                    CellGap;
            }

            var rowOffsets =
                new double[rowCount];

            for (int i = 1;
                 i < rowCount;
                 i++)
            {
                rowOffsets[i] =
                    rowOffsets[i - 1] +
                    rowHeights[i - 1] +
                    CellGap;
            }

            CadDocument masterDocument = null;

            /*
             * IMPORTANT:
             *
             * We keep the original entity list for the first
             * document BEFORE removing anything from it.
             */
            List<Entity> firstMasterEntities = null;

            var mergedCount = 0;

            foreach (var sheet in result.Sheets)
            {
                if (!File.Exists(sheet.FilePath))
                {
                    result.MergeErrors.Add(
                        $"{sheet.FileName}: source file not found.");

                    continue;
                }

                try
                {
                    var sourceDocument =
                        ReadCadDocument(
                            sheet.FilePath,
                            result.MergeErrors);

                    if (sourceDocument == null)
                    {
                        result.MergeErrors.Add(
                            $"{sheet.FileName}: CAD document could not be read.");

                        continue;
                    }

                    /*
                     * Capture entities FIRST.
                     *
                     * This fixes the previous bug where the first
                     * document was cleared before it was processed.
                     */
                    var sourceEntities =
                        sourceDocument.Entities
                            .ToList();

                    if (sourceEntities.Count == 0)
                    {
                        result.MergeErrors.Add(
                            $"{sheet.FileName}: no model-space entities were read.");

                        continue;
                    }

                    /*
                     * First valid document becomes the master.
                     *
                     * Keep all its document tables:
                     * layers, linetypes, blocks, styles, etc.
                     */
                    if (masterDocument == null)
                    {
                        masterDocument =
                            sourceDocument;

                        firstMasterEntities =
                            sourceEntities;

                        /*
                         * Remove the original model-space entities
                         * only AFTER capturing them.
                         */
                        foreach (var entity in
                                 sourceEntities)
                        {
                            masterDocument.Entities.Remove(
                                entity);
                        }
                    }

                    var safeColumn =
                        Math.Max(
                            0,
                            Math.Min(
                                columnCount - 1,
                                sheet.Column));

                    var safeRow =
                        Math.Max(
                            0,
                            Math.Min(
                                rowCount - 1,
                                sheet.Row));

                    var targetX =
                        columnOffsets[safeColumn];

                    var targetY =
                        -rowOffsets[safeRow];

                    var translation =
                        Transform.CreateTranslation(
                            new XYZ(
                                targetX - sheet.MinX,
                                targetY - sheet.MaxY,
                                0));

                    /*
                     * Flatten INSERT entities recursively.
                     */
                    var flattenedEntities =
                        new List<Entity>();

                    foreach (var sourceEntity
                             in sourceEntities)
                    {
                        FlattenEntity(
                            sourceEntity,
                            flattenedEntities,
                            result.MergeErrors,
                            sheet.FileName);
                    }

                    if (flattenedEntities.Count == 0)
                    {
                        result.MergeErrors.Add(
                            $"{sheet.FileName}: no drawable entities remained after flattening.");

                        continue;
                    }

                    foreach (var flattenedEntity
                             in flattenedEntities)
                    {
                        try
                        {
                            Entity outputEntity;

                            /*
                             * First source belongs to masterDocument.
                             *
                             * Other source documents must be cloned.
                             */
                            if (ReferenceEquals(
                                    sourceDocument,
                                    masterDocument))
                            {
                                outputEntity =
                                    flattenedEntity;
                            }
                            else
                            {
                                outputEntity =
                                    (Entity)flattenedEntity.Clone();
                            }

                            outputEntity.ApplyTransform(
                                translation);

                            masterDocument.Entities.Add(
                                outputEntity);
                        }
                        catch (Exception ex)
                        {
                            result.MergeErrors.Add(
                                $"{sheet.FileName}: entity add failed — " +
                                $"{ex.GetType().Name}: {ex.Message}");
                        }
                    }

                    mergedCount++;
                }
                catch (Exception ex)
                {
                    result.MergeErrors.Add(
                        $"{sheet.FileName}: merge failed — " +
                        $"{ex.GetType().Name}: {ex.Message}");

                    if (ex.InnerException != null)
                    {
                        result.MergeErrors.Add(
                            $"{sheet.FileName}: inner exception — " +
                            $"{ex.InnerException.GetType().Name}: " +
                            $"{ex.InnerException.Message}");
                    }
                }
            }

            if (masterDocument == null)
            {
                result.MergeErrors.Add(
                    "No CAD document could be loaded.");

                return Task.CompletedTask;
            }

            if (masterDocument.Entities.Count == 0)
            {
                result.MergeErrors.Add(
                    "Merged master document contains zero model-space entities.");

                return Task.CompletedTask;
            }

            if (mergedCount == 0)
            {
                result.MergeErrors.Add(
                    "No sheets were successfully merged.");

                return Task.CompletedTask;
            }

            Directory.CreateDirectory(
                outputDirectory);

            var timestamp =
                DateTime.UtcNow.ToString(
                    "yyyyMMdd_HHmmssfff");

            var dxfFileName =
                $"merged_{timestamp}.dxf";

            var dwgFileName =
                $"merged_{timestamp}.dwg";

            var dxfPath =
                Path.Combine(
                    outputDirectory,
                    dxfFileName);

            var dwgPath =
                Path.Combine(
                    outputDirectory,
                    dwgFileName);

            /*
             * DXF
             */
            try
            {
                DxfWriter.Write(
                    dxfPath,
                    masterDocument);
            }
            catch (Exception ex)
            {
                result.MergeErrors.Add(
                    $"DXF export failed — " +
                    $"{ex.GetType().Name}: {ex.Message}");

                if (ex.InnerException != null)
                {
                    result.MergeErrors.Add(
                        $"DXF export inner exception — " +
                        $"{ex.InnerException.GetType().Name}: " +
                        $"{ex.InnerException.Message}");
                }
            }

            /*
             * DWG
             */
            try
            {
                DwgWriter.Write(
                    dwgPath,
                    masterDocument);
            }
            catch (Exception ex)
            {
                result.MergeErrors.Add(
                    $"DWG export failed — " +
                    $"{ex.GetType().Name}: {ex.Message}");

                if (ex.InnerException != null)
                {
                    result.MergeErrors.Add(
                        $"DWG export inner exception — " +
                        $"{ex.InnerException.GetType().Name}: " +
                        $"{ex.InnerException.Message}");
                }
            }

            if (File.Exists(dxfPath))
            {
                result.MergeOutputFileName =
                    dxfFileName;
            }
            else if (File.Exists(dwgPath))
            {
                result.MergeOutputFileName =
                    dwgFileName;
            }
            else
            {
                result.MergeErrors.Add(
                    "Neither DXF nor DWG output was created.");
            }

            return Task.CompletedTask;
        }

        private static void FlattenEntity(
            Entity entity,
            List<Entity> output,
            List<string> errors,
            string fileName)
        {
            if (entity == null)
                return;

            /*
             * INSERT:
             *
             * Resolve the referenced BlockRecord and recursively
             * flatten its contents.
             */
            if (entity is Insert insert)
            {
                if (insert.Block == null)
                {
                    errors.Add(
                        $"{fileName}: INSERT has no block reference.");

                    return;
                }

                try
                {
                    /*
                     * ACadSharp's Explode() already:
                     *
                     * - reads Insert.Block.Entities
                     * - clones them
                     * - applies the INSERT transform
                     *
                     * Nested INSERTs are then recursively flattened.
                     */
                    foreach (var explodedEntity
                             in insert.Explode())
                    {
                        if (explodedEntity == null)
                            continue;

                        if (explodedEntity is Insert)
                        {
                            FlattenEntity(
                                explodedEntity,
                                output,
                                errors,
                                fileName);
                        }
                        else
                        {
                            output.Add(
                                explodedEntity);
                        }
                    }
                }
                catch (Exception ex)
                {
                    errors.Add(
                        $"{fileName}: INSERT '{insert.Block.Name}' " +
                        $"could not be flattened — " +
                        $"{ex.GetType().Name}: {ex.Message}");
                }

                return;
            }

            /*
             * Normal model-space geometry.
             */
            output.Add(entity);
        }

        private static CadDocument ReadCadDocument(
            string file,
            List<string> errors)
        {
            var extension =
                Path.GetExtension(file)
                    .ToLowerInvariant();

            void OnNotification(
                object sender,
                NotificationEventArgs args)
            {
                var message =
                    $"[{args.NotificationType}] {args.Message}";

                if (args.Exception != null)
                {
                    message +=
                        $" | {args.Exception.GetType().Name}: " +
                        $"{args.Exception.Message}";
                }

                errors.Add(
                    $"{Path.GetFileName(file)}: " +
                    $"ACadSharp — {message}");
            }

            if (extension == ".dxf")
            {
                var configuration =
                    new DxfReaderConfiguration
                    {
                        Failsafe = false
                    };

                return DxfReader.Read(
                    file,
                    configuration,
                    OnNotification);
            }

            if (extension == ".dwg")
            {
                var configuration =
                    new DwgReaderConfiguration
                    {
                        Failsafe = false
                    };

                return DwgReader.Read(
                    file,
                    configuration,
                    OnNotification);
            }

            throw new NotSupportedException(
                $"Unsupported CAD file type: {extension}");
        }

        private static bool IsCadFile(
            string filePath)
        {
            var extension =
                Path.GetExtension(filePath);

            return string.Equals(
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
            var name =
                Path.GetFileNameWithoutExtension(
                    fileName);

            if (string.IsNullOrWhiteSpace(name))
                return 0;

            var match =
                Regex.Match(
                    name,
                    @"(\d+)$");

            if (!match.Success)
                return 0;

            return int.TryParse(
                match.Groups[1].Value,
                out var index)
                ? index
                : 0;
        }

        private static void DeduplicateIndexes(
            List<CadSheetGridItem> sheets)
        {
            var seen =
                new HashSet<int>();

            foreach (var sheet in sheets)
            {
                if (sheet.IndexNumber > 0 &&
                    !seen.Add(sheet.IndexNumber))
                {
                    sheet.IndexNumber = 0;
                }
            }
        }

        private static void AssignMissingIndexes(
            List<CadSheetGridItem> sheets)
        {
            var usedIndexes =
                sheets
                    .Where(x => x.IndexNumber > 0)
                    .Select(x => x.IndexNumber)
                    .ToHashSet();

            var nextIndex = 1;

            foreach (var sheet in sheets)
            {
                if (sheet.IndexNumber > 0)
                    continue;

                while (usedIndexes.Contains(
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
                return 1;

            return Math.Max(
                1,
                (int)Math.Ceiling(
                    Math.Sqrt(sheetCount)));
        }
    }
}