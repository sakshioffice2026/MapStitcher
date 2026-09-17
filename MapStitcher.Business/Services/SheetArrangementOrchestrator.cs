// MapStitcher.Business/Services/SheetArrangementOrchestrator.cs

using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using ACadSharp.Tables;
using MapStitcher.Business.Contracts;
using MapStitcher.Model;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MapStitcher.Business.Services
{
    public class SheetArrangementOrchestrator : ISheetArrangementOrchestrator
    {
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

        public async Task<CadSheetGridResult> ArrangeAsync(
            string directoryPath,
            string? outputRootPath)
        {
            var result = new CadSheetGridResult();

            var files = Directory
                .GetFiles(
                    directoryPath,
                    "*.*",
                    SearchOption.TopDirectoryOnly)
                .Where(f =>
                    f.EndsWith(".dwg", StringComparison.OrdinalIgnoreCase) ||
                    f.EndsWith(".dxf", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (files.Count == 0)
                return result;

            var topologyInputs =
                new List<SheetTopologyInput>();

            var neatlinesBySheetId =
                new Dictionary<string, NeatlineExtent>();

            var inspectionsBySheetId =
                new Dictionary<string, CadCoordinateInspectionResult>();

            var boundaryPolygonsBySheetId =
                new Dictionary<
                    string,
                    List<List<(double X, double Y)>>>();

            var docsBySheetId =
                new Dictionary<string, CadDocument>();

            var rawBoundsByFile =
                new Dictionary<
                    string,
                    (double minX, double minY, double maxX, double maxY)?>();

            foreach (var file in files)
            {
                try
                {
                    var neighbors =
                        await _indexMapService.ExtractAsync(
                            file,
                            IndexMapLayerConfig.IndexGridLayer);

                    try
                    {
                        inspectionsBySheetId[file] =
                            await _inspectionService.InspectFileAsync(
                                file,
                                Path.GetFileName(file));
                    }
                    catch (Exception ex)
                    {
                        result.MergeErrors.Add(
                            $"{Path.GetFileName(file)}: " +
                            $"Laghu reference scan failed — {ex.Message}");
                    }

                    topologyInputs.Add(
                        new SheetTopologyInput
                        {
                            SheetId = file,
                            CenterSheetNumber =
                                neighbors.CenterSheetNumber,

                            TopSheetNumber =
                                neighbors.TopSheetNumber,

                            BottomSheetNumber =
                                neighbors.BottomSheetNumber,

                            LeftSheetNumber =
                                neighbors.LeftSheetNumber,

                            RightSheetNumber =
                                neighbors.RightSheetNumber
                        });

                    foreach (var warning in neighbors.Warnings)
                    {
                        result.MergeErrors.Add(
                            $"{Path.GetFileName(file)}: {warning}");
                    }

                    var doc =
                        ReadCadDocument(
                            file,
                            result.MergeErrors);

                    docsBySheetId[file] = doc;

                    var neatline =
                        _neatlineService.Extract(
                            doc,
                            IndexMapLayerConfig.NeatlineLayer);

                    if (!neatline.IsValid)
                    {
                        result.MergeErrors.Add(
                            $"{Path.GetFileName(file)}: " +
                            $"no valid neatline on layer " +
                            $"'{IndexMapLayerConfig.NeatlineLayer}', " +
                            "size unknown.");
                    }

                    neatlinesBySheetId[file] =
                        neatline;

                    rawBoundsByFile[file] =
                        ComputeRawEntityBounds(doc);

                    boundaryPolygonsBySheetId[file] =
                        ExtractBoundaryPolygons(
                            doc,
                            IndexMapLayerConfig.NeatlineLayer);
                }
                catch (Exception ex)
                {
                    result.MergeErrors.Add(
                        $"{Path.GetFileName(file)}: " +
                        $"extraction failed — " +
                        $"{ex.GetType().Name}: {ex.Message}");

                    if (ex.InnerException != null)
                    {
                        result.MergeErrors.Add(
                            $"{Path.GetFileName(file)}: " +
                            $"inner exception — " +
                            $"{ex.InnerException.GetType().Name}: " +
                            $"{ex.InnerException.Message}");
                    }
                }
            }

            var topology =
                _topologyService.BuildGrid(
                    topologyInputs);

            result.MergeErrors.AddRange(
                topology.Anomalies);

            var placedSheets =
                topology.Sheets
                    .Where(t => t.Placed)
                    .ToList();

            if (placedSheets.Count == 0)
            {
                result.MergeErrors.Add(
                    "No sheets could be topologically placed " +
                    "from index-map neighbor data.");

                return result;
            }

            int minGx =
                Math.Min(
                    placedSheets.Min(t => t.GridX),
                    topology.MissingSlots.Count > 0
                        ? topology.MissingSlots.Min(m => m.GridX)
                        : int.MaxValue);

            int minGy =
                Math.Min(
                    placedSheets.Min(t => t.GridY),
                    topology.MissingSlots.Count > 0
                        ? topology.MissingSlots.Min(m => m.GridY)
                        : int.MaxValue);

            int maxGy =
                Math.Max(
                    placedSheets.Max(t => t.GridY),
                    topology.MissingSlots.Count > 0
                        ? topology.MissingSlots.Max(m => m.GridY)
                        : int.MinValue);

            result.Sheets =
                placedSheets
                    .Select(t =>
                    {
                        var neatline =
                            neatlinesBySheetId.TryGetValue(
                                t.SheetId,
                                out var n)
                                ? n
                                : new NeatlineExtent();

                        var inspection =
                            inspectionsBySheetId.TryGetValue(
                                t.SheetId,
                                out var insp)
                                ? insp
                                : null;

                        double minX;
                        double minY;
                        double maxX;
                        double maxY;

                        if (neatline.IsValid)
                        {
                            minX = neatline.MinX;
                            minY = neatline.MinY;
                            maxX = neatline.MaxX;
                            maxY = neatline.MaxY;
                        }
                        else if (
                            inspection != null &&
                            inspection.MaxX -
                                inspection.MinX > 0 &&
                            inspection.MaxY -
                                inspection.MinY > 0)
                        {
                            minX = inspection.MinX;
                            minY = inspection.MinY;
                            maxX = inspection.MaxX;
                            maxY = inspection.MaxY;
                        }
                        else if (
                            rawBoundsByFile.TryGetValue(
                                t.SheetId,
                                out var rb) &&
                            rb.HasValue &&
                            rb.Value.maxX -
                                rb.Value.minX > 0 &&
                            rb.Value.maxY -
                                rb.Value.minY > 0)
                        {
                            minX = rb.Value.minX;
                            minY = rb.Value.minY;
                            maxX = rb.Value.maxX;
                            maxY = rb.Value.maxY;

                            result.MergeErrors.Add(
                                $"{Path.GetFileName(t.SheetId)}: " +
                                "neatline and inspection bounds invalid — " +
                                "using raw entity bounds as fallback " +
                                $"({minX:F0},{minY:F0} → " +
                                $"{maxX:F0},{maxY:F0}).");
                        }
                        else
                        {
                            minX = 0;
                            minY = 0;
                            maxX = 0;
                            maxY = 0;
                        }

                        var polygons =
                            boundaryPolygonsBySheetId.TryGetValue(
                                t.SheetId,
                                out var polys)
                                ? polys
                                : new List<
                                    List<(double X, double Y)>>();

                        return new CadSheetGridItem
                        {
                            FileName =
                                ExtractDisplayFileName(
                                    t.SheetId),

                            FilePath =
                                t.SheetId,

                            SheetNumber =
                                t.SheetNumber ??
                                string.Empty,

                            Column =
                                t.GridX - minGx,

                            Row =
                                maxGy - t.GridY,

                            MinX = minX,
                            MinY = minY,
                            MaxX = maxX,
                            MaxY = maxY,

                            EntityCount =
                                inspection?.EntityCount ?? 0,

                            LaghuReferenceCount =
                                inspection?.LaghuReferences.Count ?? 0,

                            LaghuReferences =
                                inspection?.LaghuReferences.ToList()
                                ?? new List<LaghuReferenceViewModel>(),

                            BoundaryPolygons =
                                polygons
                        };
                    })
                    .ToList();

            foreach (
                var unplaced in
                topology.Sheets.Where(t => !t.Placed))
            {
                result.MergeErrors.Add(
                    $"{Path.GetFileName(unplaced.SheetId)}: " +
                    "not connected to the topology graph, " +
                    "excluded from arrangement.");
            }

            result.MissingSlots =
                topology.MissingSlots
                    .Select(m =>
                        new CadMissingSheetSlot
                        {
                            SheetNumber =
                                m.SheetNumber,

                            Column =
                                m.GridX - minGx,

                            Row =
                                maxGy - m.GridY
                        })
                    .ToList();

            result.SheetCount =
                result.Sheets.Count;

            result.ColumnCount =
                new[]
                {
                    result.Sheets.Count > 0
                        ? result.Sheets.Max(
                            s => s.Column)
                        : -1,

                    result.MissingSlots.Count > 0
                        ? result.MissingSlots.Max(
                            s => s.Column)
                        : -1
                }.Max() + 1;

            result.RowCount =
                new[]
                {
                    result.Sheets.Count > 0
                        ? result.Sheets.Max(
                            s => s.Row)
                        : -1,

                    result.MissingSlots.Count > 0
                        ? result.MissingSlots.Max(
                            s => s.Row)
                        : -1
                }.Max() + 1;

            // DXF export/stitching has intentionally been removed.
            // outputRootPath is retained in the method signature so the
            // existing interface/controller does not break.

            return result;
        }

        private static CadDocument ReadCadDocument(
            string file,
            List<string> errors)
        {
            var ext =
                Path.GetExtension(file)
                    .ToLowerInvariant();

            void OnNotification(
                object sender,
                NotificationEventArgs args)
            {
                var message =
                    $"[{args.NotificationType}] " +
                    $"{args.Message}";

                if (args.Exception != null)
                {
                    message +=
                        $" | " +
                        $"{args.Exception.GetType().Name}: " +
                        $"{args.Exception.Message}";
                }

                errors.Add(
                    $"{Path.GetFileName(file)}: " +
                    $"ACadSharp — {message}");
            }

            if (ext == ".dxf")
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

            if (ext == ".dwg")
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
                $"Unsupported CAD file type: {ext}");
        }

        private static (
            double minX,
            double minY,
            double maxX,
            double maxY)?
            ComputeRawEntityBounds(
                CadDocument doc)
        {
            double minX =
                double.MaxValue;

            double minY =
                double.MaxValue;

            double maxX =
                double.MinValue;

            double maxY =
                double.MinValue;

            bool hasPoints = false;

            void Expand(
                double x,
                double y)
            {
                if (x < minX)
                    minX = x;

                if (y < minY)
                    minY = y;

                if (x > maxX)
                    maxX = x;

                if (y > maxY)
                    maxY = y;

                hasPoints = true;
            }

            foreach (
                var entity in
                FlattenDocumentEntities(
                    doc.Entities,
                    0))
            {
                try
                {
                    if (entity is LwPolyline lw)
                    {
                        foreach (var vertex in lw.Vertices)
                        {
                            Expand(
                                vertex.Location.X,
                                vertex.Location.Y);
                        }
                    }
                    else if (entity is Polyline2D p2d)
                    {
                        foreach (var vertex in p2d.Vertices)
                        {
                            Expand(
                                vertex.Location.X,
                                vertex.Location.Y);
                        }
                    }
                    else if (entity is Line line)
                    {
                        Expand(
                            line.StartPoint.X,
                            line.StartPoint.Y);

                        Expand(
                            line.EndPoint.X,
                            line.EndPoint.Y);
                    }
                    else if (entity is Circle circle)
                    {
                        Expand(
                            circle.Center.X -
                                circle.Radius,
                            circle.Center.Y -
                                circle.Radius);

                        Expand(
                            circle.Center.X +
                                circle.Radius,
                            circle.Center.Y +
                                circle.Radius);
                    }
                    else if (entity is Arc arc)
                    {
                        Expand(
                            arc.Center.X -
                                arc.Radius,
                            arc.Center.Y -
                                arc.Radius);

                        Expand(
                            arc.Center.X +
                                arc.Radius,
                            arc.Center.Y +
                                arc.Radius);
                    }
                    else if (entity is TextEntity text)
                    {
                        Expand(
                            text.InsertPoint.X,
                            text.InsertPoint.Y);
                    }
                    else if (entity is MText mtext)
                    {
                        Expand(
                            mtext.InsertPoint.X,
                            mtext.InsertPoint.Y);
                    }
                }
                catch
                {
                }
            }

            return hasPoints
                ? (
                    minX,
                    minY,
                    maxX,
                    maxY)
                : null;
        }

        private static List<
            List<(double X, double Y)>>
            ExtractBoundaryPolygons(
                CadDocument doc,
                string layerName)
        {
            var rings =
                new List<
                    List<(double X, double Y)>>();

            foreach (
                var entity in
                FlattenDocumentEntities(
                    doc.Entities,
                    0))
            {
                try
                {
                    var entityLayer =
                        entity.Layer?.Name ??
                        string.Empty;

                    if (!string.Equals(
                            entityLayer,
                            layerName,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    List<(double X, double Y)>? ring =
                        null;

                    if (
                        entity is LwPolyline lw &&
                        lw.Vertices.Count >= 3)
                    {
                        ring =
                            lw.Vertices
                                .Select(v =>
                                    (
                                        v.Location.X,
                                        v.Location.Y))
                                .ToList();
                    }
                    else if (entity is Polyline2D p2d)
                    {
                        var points =
                            p2d.Vertices
                                .Select(v =>
                                    (
                                        v.Location.X,
                                        v.Location.Y))
                                .ToList();

                        if (points.Count >= 3)
                            ring = points;
                    }

                    if (
                        ring != null &&
                        ring.Count >= 3)
                    {
                        rings.Add(ring);
                    }
                }
                catch
                {
                }
            }

            return rings;
        }

        private static IEnumerable<Entity>
            FlattenDocumentEntities(
                IEnumerable<Entity> entities,
                int depth)
        {
            if (depth > 32)
                yield break;

            foreach (var entity in entities)
            {
                if (entity == null)
                    continue;

                if (entity is Insert insert)
                {
                    BlockRecord? block = null;

                    try
                    {
                        block = insert.Block;
                    }
                    catch
                    {
                        continue;
                    }

                    if (block == null)
                        continue;

                    List<Entity>? children =
                        null;

                    try
                    {
                        children =
                            block.Entities
                                .Where(x => x != null)
                                .ToList();
                    }
                    catch
                    {
                        continue;
                    }

                    foreach (
                        var child in
                        FlattenDocumentEntities(
                            children,
                            depth + 1))
                    {
                        yield return child;
                    }

                    continue;
                }

                yield return entity;
            }
        }
    }
}