// MapStitcher.Business/Services/SheetArrangementOrchestrator.cs
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using MapStitcher.Business.Contracts;
using MapStitcher.Model;

namespace MapStitcher.Business.Services
{
    public class SheetArrangementOrchestrator : ISheetArrangementOrchestrator
    {
        // Temp upload files are named "{32-char-hex-guid}__{originalName}.ext"
        // by CadGridController so uploads never collide on disk while the
        // user-facing name is still recoverable for display.
        private static string ExtractDisplayFileName(string filePath)
        {
            var fileName = Path.GetFileName(filePath);
            var separatorIndex = fileName.IndexOf("__", StringComparison.Ordinal);

            if (separatorIndex == 32 &&
                fileName[..32].All(Uri.IsHexDigit) &&
                separatorIndex + 2 < fileName.Length)
            {
                return fileName[(separatorIndex + 2)..];
            }

            return fileName;
        }


        private readonly IIndexMapExtractionService _indexMapService;
        private readonly ITopologyGridService _topologyService;
        private readonly INeatlineExtractionService _neatlineService;
        private readonly ICadCoordinateInspectionService _inspectionService;

        public SheetArrangementOrchestrator(
            IIndexMapExtractionService indexMapService,
            ITopologyGridService topologyService,
            INeatlineExtractionService neatlineService,
            ICadCoordinateInspectionService inspectionService)
        {
            _indexMapService = indexMapService;
            _topologyService = topologyService;
            _neatlineService = neatlineService;
            _inspectionService = inspectionService;
        }

        public Task<CadSheetGridResult> ArrangeAsync(string directoryPath)
        {
            return ArrangeAsync(directoryPath, null);
        }

        public async Task<CadSheetGridResult> ArrangeAsync(string directoryPath, string? outputRootPath)
        {
            var result = new CadSheetGridResult();

            var files = Directory
                .GetFiles(directoryPath, "*.*", SearchOption.TopDirectoryOnly)
                .Where(f => f.EndsWith(".dwg", StringComparison.OrdinalIgnoreCase)
                         || f.EndsWith(".dxf", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (files.Count == 0) return result;

            var topologyInputs = new List<SheetTopologyInput>();
            var neatlinesBySheetId = new Dictionary<string, NeatlineExtent>();
            var inspectionsBySheetId = new Dictionary<string, CadCoordinateInspectionResult>();

            foreach (var file in files)
            {
                try
                {
                    var neighbors = await _indexMapService.ExtractAsync(file, IndexMapLayerConfig.IndexGridLayer);

                    try
                    {
                        inspectionsBySheetId[file] =
                            await _inspectionService.InspectFileAsync(file, Path.GetFileName(file));
                    }
                    catch (Exception ex)
                    {
                        result.MergeErrors.Add($"{Path.GetFileName(file)}: Laghu reference scan failed — {ex.Message}");
                    }

                    // Do NOT skip/continue here. TopologyGridService now places
                    // a sheet with no centre number as its own standalone
                    // component instead of excluding it — but only if it
                    // actually receives the sheet. Passing null through lets
                    // that handling work; filtering it out here bypasses it
                    // entirely and also skips the neatline extraction below.
                    var centerNumber = neighbors.CenterSheetNumber;

                    topologyInputs.Add(new SheetTopologyInput
                    {
                        SheetId = file,
                        CenterSheetNumber = centerNumber,
                        TopSheetNumber = neighbors.TopSheetNumber,
                        BottomSheetNumber = neighbors.BottomSheetNumber,
                        LeftSheetNumber = neighbors.LeftSheetNumber,
                        RightSheetNumber = neighbors.RightSheetNumber
                    });

                    foreach (var w in neighbors.Warnings)
                        result.MergeErrors.Add($"{Path.GetFileName(file)}: {w}");

                    var ext = Path.GetExtension(file).ToLowerInvariant();
                    var doc = ext == ".dxf" ? DxfReader.Read(file) : DwgReader.Read(file);
                    var neatline = _neatlineService.Extract(doc, IndexMapLayerConfig.NeatlineLayer);

                    if (!neatline.IsValid)
                        result.MergeErrors.Add($"{Path.GetFileName(file)}: no valid neatline on layer '{IndexMapLayerConfig.NeatlineLayer}', size unknown.");

                    neatlinesBySheetId[file] = neatline;
                }
                catch (Exception ex)
                {
                    result.MergeErrors.Add($"{Path.GetFileName(file)}: extraction failed — {ex.Message}");
                }
            }

            var topology = _topologyService.BuildGrid(topologyInputs);
            result.MergeErrors.AddRange(topology.Anomalies);

            var placedSheets = topology.Sheets.Where(t => t.Placed).ToList();

            if (placedSheets.Count == 0)
            {
                result.MergeErrors.Add("No sheets could be topologically placed from index-map neighbor data.");
                return result;
            }

            // Bounds must span both real placed sheets and any blank boxes for
            // referenced-but-not-uploaded neighbours, so a missing sheet at the
            // grid's edge still gets its own row/column instead of being clipped.
            int minGx = Math.Min(
                placedSheets.Min(t => t.GridX),
                topology.MissingSlots.Count > 0 ? topology.MissingSlots.Min(m => m.GridX) : int.MaxValue);

            int minGy = Math.Min(
                placedSheets.Min(t => t.GridY),
                topology.MissingSlots.Count > 0 ? topology.MissingSlots.Min(m => m.GridY) : int.MaxValue);

            int maxGy = Math.Max(
                placedSheets.Max(t => t.GridY),
                topology.MissingSlots.Count > 0 ? topology.MissingSlots.Max(m => m.GridY) : int.MinValue);

            result.Sheets = placedSheets.Select(t =>
            {
                var neatline = neatlinesBySheetId.TryGetValue(t.SheetId, out var n) ? n : new NeatlineExtent();
                var inspection = inspectionsBySheetId.TryGetValue(t.SheetId, out var insp) ? insp : null;

                return new CadSheetGridItem
                {
                    FileName = ExtractDisplayFileName(t.SheetId),
                    FilePath = t.SheetId,
                    SheetNumber = t.SheetNumber ?? string.Empty,
                    Column = t.GridX - minGx,
                    Row = maxGy - t.GridY,
                    MinX = neatline.MinX,
                    MinY = neatline.MinY,
                    MaxX = neatline.MaxX,
                    MaxY = neatline.MaxY,
                    LaghuReferenceCount = inspection?.LaghuReferences.Count ?? 0,
                    LaghuReferences = inspection?.LaghuReferences.ToList() ?? new List<LaghuReferenceViewModel>()
                };
            }).ToList();

            foreach (var u in topology.Sheets.Where(t => !t.Placed))
                result.MergeErrors.Add($"{Path.GetFileName(u.SheetId)}: not connected to the topology graph, excluded from arrangement.");

            result.MissingSlots = topology.MissingSlots
                .Select(m => new CadMissingSheetSlot
                {
                    SheetNumber = m.SheetNumber,
                    Column = m.GridX - minGx,
                    Row = maxGy - m.GridY
                })
                .ToList();

            result.SheetCount = result.Sheets.Count;
            result.ColumnCount = new[] { result.Sheets.Count > 0 ? result.Sheets.Max(s => s.Column) : -1,
                                          result.MissingSlots.Count > 0 ? result.MissingSlots.Max(s => s.Column) : -1 }
                                  .Max() + 1;
            result.RowCount = new[] { result.Sheets.Count > 0 ? result.Sheets.Max(s => s.Row) : -1,
                                       result.MissingSlots.Count > 0 ? result.MissingSlots.Max(s => s.Row) : -1 }
                               .Max() + 1;

            if (!string.IsNullOrWhiteSpace(outputRootPath))
                StitchMasterDxf(result, outputRootPath);

            return result;
        }

        // Best-effort mosaic stitch: no shared real-world coordinates exist at
        // this stage (that only happens after tie-point georeferencing in the
        // DB-backed workflow), so sheets are tiled edge-to-edge using a uniform
        // cell size derived from the largest neatline extent, in the same
        // Row/Column order already resolved by the topology grid. Failures for
        // an individual sheet are recorded as merge errors and the sheet is
        // skipped; the whole grid result is still returned either way.
        private void StitchMasterDxf(CadSheetGridResult result, string outputRootPath)
        {
            var stitchable = result.Sheets
                .Where(s => s.MaxX > s.MinX && s.MaxY > s.MinY)
                .ToList();

            if (stitchable.Count == 0)
            {
                result.MergeErrors.Add("Stitch skipped: no sheet has a valid neatline extent.");
                return;
            }

            const double margin = 50.0;
            var cellWidth = stitchable.Max(s => s.Width) + margin;
            var cellHeight = stitchable.Max(s => s.Height) + margin;

            var masterDocument = new CadDocument();
            int stitchedCount = 0;

            foreach (var sheet in result.Sheets)
            {
                if (sheet.MaxX <= sheet.MinX || sheet.MaxY <= sheet.MinY)
                {
                    result.MergeErrors.Add($"{sheet.FileName}: no valid neatline, excluded from master DXF stitch.");
                    continue;
                }

                try
                {
                    var tx = sheet.Column * cellWidth - sheet.MinX;
                    var ty = -(sheet.Row * cellHeight) - sheet.MaxY;

                    var cloned = CloneEntitiesTranslated(sheet.FilePath, tx, ty);
                    foreach (var entity in cloned)
                        masterDocument.Entities.Add(entity);

                    var label = new TextEntity
                    {
                        Value = $"Sheet {sheet.SheetNumber}",
                        Height = 2.5,
                        InsertPoint = new CSMath.XYZ(
                            sheet.Column * cellWidth,
                            -(sheet.Row * cellHeight) + 5,
                            0)
                    };
                    masterDocument.Entities.Add(label);

                    stitchedCount++;
                }
                catch (Exception ex)
                {
                    result.MergeErrors.Add($"{sheet.FileName}: stitch failed — {ex.Message}");
                }
            }

            if (stitchedCount == 0)
            {
                result.MergeErrors.Add("Master DXF stitch produced no entities.");
                return;
            }

            try
            {
                Directory.CreateDirectory(outputRootPath);
                var fileName = $"cadgrid_merge_{Guid.NewGuid():N}.dxf";
                var fullPath = Path.Combine(outputRootPath, fileName);

                DxfWriter.Write(fullPath, masterDocument);

                result.MergeOutputFileName = fileName;
            }
            catch (Exception ex)
            {
                result.MergeErrors.Add($"Failed to write master DXF: {ex.Message}");
            }
        }

        private static List<Entity> CloneEntitiesTranslated(string sourceFilePath, double tx, double ty)
        {
            var ext = Path.GetExtension(sourceFilePath).ToLowerInvariant();
            var sourceDocument = ext == ".dxf" ? DxfReader.Read(sourceFilePath) : DwgReader.Read(sourceFilePath);

            var translation = CSMath.Transform.CreateTranslation(new CSMath.XYZ(tx, ty, 0));

            var cloned = new List<Entity>();
            foreach (var sourceEntity in sourceDocument.Entities.ToList())
            {
                var clone = (Entity)sourceEntity.Clone();
                clone.ApplyTransform(translation);
                cloned.Add(clone);
            }

            return cloned;
        }
    }
}