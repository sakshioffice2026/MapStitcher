// MapStitcher.Business/Services/SheetArrangementOrchestrator.cs
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using ACadSharp.Tables;
using System;
using System.Linq;
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
            var boundaryPolygonsBySheetId = new Dictionary<string, List<List<(double X, double Y)>>>();
            // Raw entity bounds: used as a 3rd fallback when neatline layer is
            // absent AND inspection yields zero-size extents. Computed from every
            // geometric entity in the document so the stitch always has a valid
            // bounding box even on files that have no Poly_Survey_Bndry layer.
            var rawBoundsByFile = new Dictionary<string, (double minX, double minY, double maxX, double maxY)?>();

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

                    // Raw entity bounds: pre-compute now while the doc is in memory.
                    // Used as a 3rd fallback in the Select lambda below.
                    rawBoundsByFile[file] = ComputeRawEntityBounds(doc);

                    // Extract actual polygon shapes from Poly_Survey_Bndry for Geometry Grid rendering.
                    boundaryPolygonsBySheetId[file] = ExtractBoundaryPolygons(doc, IndexMapLayerConfig.NeatlineLayer);
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

                // Fall back to full CAD inspection bounds when the neatline
                // layer produced no valid extent (layer missing, empty, or
                // zero-size). Without this, Width/Height stay 0 and the
                // Geometry Grid SVG renders invisible zero-size rectangles.
                double minX, minY, maxX, maxY;
                if (neatline.IsValid)
                {
                    minX = neatline.MinX;
                    minY = neatline.MinY;
                    maxX = neatline.MaxX;
                    maxY = neatline.MaxY;
                }
                else if (inspection != null && (inspection.MaxX - inspection.MinX) > 0 && (inspection.MaxY - inspection.MinY) > 0)
                {
                    minX = inspection.MinX;
                    minY = inspection.MinY;
                    maxX = inspection.MaxX;
                    maxY = inspection.MaxY;
                }
                else if (rawBoundsByFile.TryGetValue(t.SheetId, out var rb) && rb.HasValue
                    && (rb.Value.maxX - rb.Value.minX) > 0
                    && (rb.Value.maxY - rb.Value.minY) > 0)
                {
                    // 3rd fallback: raw entity extent — used when Poly_Survey_Bndry is
                    // absent and inspection gives zero-size bounds. Ensures the sheet
                    // still participates in the DXF stitch instead of being silently excluded.
                    minX = rb.Value.minX;
                    minY = rb.Value.minY;
                    maxX = rb.Value.maxX;
                    maxY = rb.Value.maxY;
                    result.MergeErrors.Add($"{Path.GetFileName(t.SheetId)}: neatline and inspection bounds invalid — using raw entity bounds as fallback ({minX:F0},{minY:F0} → {maxX:F0},{maxY:F0}).");
                }
                else
                {
                    minX = minY = maxX = maxY = 0;
                }

                var polygons = boundaryPolygonsBySheetId.TryGetValue(t.SheetId, out var polys) ? polys : new List<List<(double X, double Y)>>();

                return new CadSheetGridItem
                {
                    FileName = ExtractDisplayFileName(t.SheetId),
                    FilePath = t.SheetId,
                    SheetNumber = t.SheetNumber ?? string.Empty,
                    Column = t.GridX - minGx,
                    Row = maxGy - t.GridY,
                    MinX = minX,
                    MinY = minY,
                    MaxX = maxX,
                    MaxY = maxY,
                    EntityCount = inspection?.EntityCount ?? 0,
                    LaghuReferenceCount = inspection?.LaghuReferences.Count ?? 0,
                    LaghuReferences = inspection?.LaghuReferences.ToList() ?? new List<LaghuReferenceViewModel>(),
                    BoundaryPolygons = polygons
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
            {
                try
                {
                    StitchMasterDxf(result, outputRootPath);
                }
                catch (Exception ex)
                {
                    result.MergeErrors.Add($"DXF export failed: {ex.Message}");
                }
            }

            return result;
        }

        // Computes a bounding box from every geometric entity in the document —
        // polylines, lines, circles, inserts, and text — without filtering by layer.
        // Returns null when the document contains no measurable coordinates.
        private static (double minX, double minY, double maxX, double maxY)? ComputeRawEntityBounds(CadDocument doc)
        {
            double minX = double.MaxValue, minY = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue;
            bool hasPoints = false;

            void Expand(double x, double y)
            {
                if (x < minX) minX = x;
                if (y < minY) minY = y;
                if (x > maxX) maxX = x;
                if (y > maxY) maxY = y;
                hasPoints = true;
            }

            foreach (var entity in FlattenDocumentEntities(doc.Entities, 0))
            {
                try
                {
                    switch (entity)
                    {
                        case LwPolyline lw:
                            foreach (var v in lw.Vertices) Expand(v.Location.X, v.Location.Y);
                            break;
                        case Polyline2D p2d:
                            foreach (var v in p2d.Vertices) Expand(v.Location.X, v.Location.Y);
                            break;
                        case Line l:
                            Expand(l.StartPoint.X, l.StartPoint.Y);
                            Expand(l.EndPoint.X, l.EndPoint.Y);
                            break;
                        case Circle c:
                            Expand(c.Center.X - c.Radius, c.Center.Y - c.Radius);
                            Expand(c.Center.X + c.Radius, c.Center.Y + c.Radius);
                            break;
                        case TextEntity t:
                            Expand(t.InsertPoint.X, t.InsertPoint.Y);
                            break;
                        case MText mt:
                            Expand(mt.InsertPoint.X, mt.InsertPoint.Y);
                            break;
                        case Insert ins:
                            Expand(ins.InsertPoint.X, ins.InsertPoint.Y);
                            break;
                    }
                }
                catch { /* skip bad entities */ }
            }

            return hasPoints ? (minX, minY, maxX, maxY) : null;
        }

        // Extracts all closed polygon rings from the given layer (Poly_Survey_Bndry).
        // Walks both top-level entities and INSERT children (same block-flattening
        // logic as NeatlineExtractionService) so geometry inside blocks is captured.
        private static List<List<(double X, double Y)>> ExtractBoundaryPolygons(CadDocument doc, string layerName)
        {
            var rings = new List<List<(double X, double Y)>>();

            foreach (var entity in FlattenDocumentEntities(doc.Entities, 0))
            {
                var layer = entity.Layer?.Name ?? string.Empty;
                if (!string.Equals(layer, layerName, StringComparison.OrdinalIgnoreCase))
                    continue;

                List<(double X, double Y)>? ring = null;

                if (entity is LwPolyline lw && lw.Vertices.Count >= 3)
                {
                    ring = lw.Vertices.Select(v => (v.Location.X, v.Location.Y)).ToList();
                }
                else if (entity is Polyline2D p2d)
                {
                    var pts = p2d.Vertices.Select(v => (v.Location.X, v.Location.Y)).ToList();
                    if (pts.Count >= 3) ring = pts;
                }

                if (ring != null && ring.Count >= 3)
                    rings.Add(ring);
            }

            return rings;
        }

        private static IEnumerable<Entity> FlattenDocumentEntities(IEnumerable<Entity> entities, int depth)
        {
            if (depth > 8) yield break;

            foreach (var entity in entities)
            {
                if (entity is Insert insert)
                {
                    List<Entity>? children = null;
                    try { children = insert.Block?.Entities?.Cast<Entity>().ToList(); } catch { }
                    if (children != null)
                        foreach (var child in FlattenDocumentEntities(children, depth + 1))
                            yield return child;
                    continue;
                }
                yield return entity;
            }
        }

        // Exports the geometry grid as a master DXF. Each sheet's full entity set
        // is cloned and translated so it sits at the same Row/Column grid position
        // shown in the SVG Geometry Grid — using each sheet's own MinX/MaxY as the
        // normalisation origin (same as the SVG rendering) so the exported layout
        // matches what the user sees on screen exactly.
        private void StitchMasterDxf(CadSheetGridResult result, string outputRootPath)
        {
            var stitchable = result.Sheets
                .Where(s => s.MaxX > s.MinX && s.MaxY > s.MinY)
                .ToList();

            // Diagnostic: show bounds for every sheet so failures are visible in UI
            foreach (var s in result.Sheets)
                result.MergeErrors.Add($"[BOUNDS] {s.FileName}: MinX={s.MinX:F2} MinY={s.MinY:F2} MaxX={s.MaxX:F2} MaxY={s.MaxY:F2} W={s.Width:F2} H={s.Height:F2}");

            if (stitchable.Count == 0)
            {
                result.MergeErrors.Add("Stitch skipped: no sheet has a valid extent.");
                return;
            }

            const double margin = 50.0;
            const double defaultCell = 500.0;

            // Per-column max width and per-row max height — same logic as the SVG grid.
            var colCount = result.Sheets.Max(s => s.Column) + 1;
            var rowCount = result.Sheets.Max(s => s.Row) + 1;

            var colMaxWidth = new double[colCount];
            var rowMaxHeight = new double[rowCount];

            foreach (var s in stitchable)
            {
                if (s.Width > colMaxWidth[s.Column]) colMaxWidth[s.Column] = s.Width;
                if (s.Height > rowMaxHeight[s.Row]) rowMaxHeight[s.Row] = s.Height;
            }

            // Fill zero-width/height slots with a default so offsets don't collapse.
            for (int c = 0; c < colCount; c++)
                if (colMaxWidth[c] <= 0) colMaxWidth[c] = defaultCell;
            for (int r = 0; r < rowCount; r++)
                if (rowMaxHeight[r] <= 0) rowMaxHeight[r] = defaultCell;

            // Cumulative column and row offsets — matches SVG geoColOffsets/geoRowOffsets.
            var colOffset = new double[colCount];
            var rowOffset = new double[rowCount];
            for (int c = 1; c < colCount; c++)
                colOffset[c] = colOffset[c - 1] + colMaxWidth[c - 1] + margin;
            for (int r = 1; r < rowCount; r++)
                rowOffset[r] = rowOffset[r - 1] + rowMaxHeight[r - 1] + margin;

            var masterDocument = new CadDocument();
            int stitchedCount = 0;

            foreach (var sheet in result.Sheets)
            {
                if (sheet.MaxX <= sheet.MinX || sheet.MaxY <= sheet.MinY)
                {
                    result.MergeErrors.Add($"{sheet.FileName}: no valid extent, excluded from DXF export.");
                    continue;
                }

                try
                {
                    // In the SVG: cell origin = (colOffset[col], rowOffset[row]).
                    // Sheet is centred in cell. CAD Y is up; SVG Y is down.
                    // For DXF export we keep CAD coordinate space (Y up):
                    //   tx: move sheet's MinX to cell left edge (+ centre offset)
                    //   ty: move sheet's MaxY to cell top edge  (+ centre offset, negated)
                    var cellW = colMaxWidth[sheet.Column];
                    var cellH = rowMaxHeight[sheet.Row];
                    var centreOffsetX = (cellW - sheet.Width) / 2.0;
                    var centreOffsetY = (cellH - sheet.Height) / 2.0;

                    var tx = colOffset[sheet.Column] + centreOffsetX - sheet.MinX;
                    var ty = -(rowOffset[sheet.Row] + centreOffsetY) - sheet.MaxY;

                    var cloned = CloneEntitiesTranslated(sheet.FilePath, tx, ty);
                    foreach (var entity in cloned)
                    {
                        var boundEntity = RebindTables(entity, masterDocument);
                        masterDocument.Entities.Add(boundEntity);
                    }

                    // Sheet label at top-left of the translated cell.
                    var labelX = colOffset[sheet.Column] + centreOffsetX;
                    var labelY = -(rowOffset[sheet.Row] + centreOffsetY) + sheet.Height + 3.0;

                    var labelLayer = masterDocument.Layers.FirstOrDefault(l => l.Name == "0")
                                     ?? new Layer("0");
                    if (!masterDocument.Layers.Any(l => l.Name == "0"))
                        masterDocument.Layers.Add(labelLayer);

                    var label = new TextEntity
                    {
                        Value = $"Sheet {sheet.SheetNumber}",
                        Height = 3.0,
                        InsertPoint = new CSMath.XYZ(labelX, labelY, 0),
                        Layer = labelLayer
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
                try
                {
                    var clone = (Entity)sourceEntity.Clone();
                    clone.ApplyTransform(translation);
                    cloned.Add(clone);
                }
                catch { /* skip entities that fail to clone */ }
            }

            return cloned;
        }

        // Rebind entity's Layer/LineType/Block references to masterDocument equivalents.
        // For Insert: read InsertPoint AFTER translation has already been applied to
        // the clone, then rebuild with the translated point against the master block.
        private static Entity RebindTables(Entity entity, CadDocument masterDocument, HashSet<string>? visitedBlocks = null)
        {
            visitedBlocks ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Ensure Layer exists in master
            var layerName = entity.Layer?.Name ?? "0";
            var masterLayer = masterDocument.Layers.FirstOrDefault(l => l.Name == layerName);
            if (masterLayer == null)
            {
                masterLayer = new Layer(layerName);
                masterDocument.Layers.Add(masterLayer);
            }
            entity.Layer = masterLayer;

            // Rebind LineType if it already exists in master (don't create missing ones)
            var lineTypeName = entity.LineType?.Name;
            if (!string.IsNullOrEmpty(lineTypeName))
            {
                var masterLineType = masterDocument.LineTypes.FirstOrDefault(l => l.Name == lineTypeName);
                if (masterLineType != null)
                    entity.LineType = masterLineType;
                else
                    entity.LineType = null; // fall back to ByLayer
            }

            if (entity is Insert sourceInsert && sourceInsert.Block != null)
            {
                var blockName = sourceInsert.Block.Name;

                // Copy block definition into master once
                if (!masterDocument.BlockRecords.Any(b => b.Name == blockName) && visitedBlocks.Add(blockName))
                {
                    try
                    {
                        var clonedBlock = (BlockRecord)sourceInsert.Block.Clone();
                        foreach (var nestedEntity in clonedBlock.Entities.ToList())
                            RebindTables(nestedEntity, masterDocument, visitedBlocks);
                        masterDocument.BlockRecords.Add(clonedBlock);
                    }
                    catch { }
                }

                var masterBlock = masterDocument.BlockRecords.FirstOrDefault(b => b.Name == blockName);
                if (masterBlock == null) return entity; // can't fix — return as-is

                // InsertPoint already has the translation applied by ApplyTransform on the clone.
                var rebuilt = new Insert(masterBlock)
                {
                    InsertPoint = sourceInsert.InsertPoint, // translated point from clone
                    Normal = sourceInsert.Normal,
                    Rotation = sourceInsert.Rotation,
                    XScale = sourceInsert.XScale,
                    YScale = sourceInsert.YScale,
                    ZScale = sourceInsert.ZScale,
                    ColumnCount = sourceInsert.ColumnCount,
                    ColumnSpacing = sourceInsert.ColumnSpacing,
                    RowCount = sourceInsert.RowCount,
                    RowSpacing = sourceInsert.RowSpacing,
                    Layer = masterLayer,
                    Color = sourceInsert.Color,
                    LineWeight = sourceInsert.LineWeight,
                    Transparency = sourceInsert.Transparency,
                    IsInvisible = sourceInsert.IsInvisible
                };

                return rebuilt;
            }

            return entity;
        }
    }
}