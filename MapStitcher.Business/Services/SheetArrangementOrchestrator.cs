// MapStitcher.Business/Services/SheetArrangementOrchestrator.cs
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

        public async Task<CadSheetGridResult> ArrangeAsync(string directoryPath)
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

            int minGx = placedSheets.Min(t => t.GridX);
            int minGy = placedSheets.Min(t => t.GridY);
            int maxGy = placedSheets.Max(t => t.GridY);

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

            result.SheetCount = result.Sheets.Count;
            result.ColumnCount = result.Sheets.Count > 0 ? result.Sheets.Max(s => s.Column) + 1 : 0;
            result.RowCount = result.Sheets.Count > 0 ? result.Sheets.Max(s => s.Row) + 1 : 0;

            return result;
        }
    }
}