// MapStitcher.Business/Services/SheetArrangementOrchestrator.cs
using ACadSharp.IO;
using MapStitcher.Business.Contracts;
using MapStitcher.Model;

namespace MapStitcher.Business.Services
{
    public class SheetArrangementOrchestrator : ISheetArrangementOrchestrator
    {
        private readonly IIndexMapExtractionService _indexMapService;
        private readonly ITopologyGridService _topologyService;
        private readonly INeatlineExtractionService _neatlineService;

        public SheetArrangementOrchestrator(
            IIndexMapExtractionService indexMapService,
            ITopologyGridService topologyService,
            INeatlineExtractionService neatlineService)
        {
            _indexMapService = indexMapService;
            _topologyService = topologyService;
            _neatlineService = neatlineService;
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

            foreach (var file in files)
            {
                try
                {
                    var neighbors = await _indexMapService.ExtractAsync(file, IndexMapLayerConfig.IndexGridLayer);
                    var centerNumber = neighbors.CenterSheetNumber ?? Path.GetFileNameWithoutExtension(file);

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

                return new CadSheetGridItem
                {
                    FileName = Path.GetFileName(t.SheetId),
                    FilePath = t.SheetId,
                    SheetNumber = t.SheetNumber ?? string.Empty,
                    Column = t.GridX - minGx,
                    Row = maxGy - t.GridY,
                    MinX = neatline.MinX,
                    MinY = neatline.MinY,
                    MaxX = neatline.MaxX,
                    MaxY = neatline.MaxY
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