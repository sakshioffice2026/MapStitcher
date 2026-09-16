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

        // Guards against a malformed/self-referencing block chain.
        private const int MaxBlockDepth = 8;

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

            // Every top-level model-space entity, plus (recursively) the
            // contents of every block it INSERTs, each paired with the
            // transform needed to bring that entity's own local coordinates
            // into world space. Title-block sheet numbers and the index-box
            // outline are very often authored inside a block definition
            // rather than directly in model space, so reading only
            // doc.Entities (as before) silently misses them.
            var flattened = FlattenEntities(doc.Entities, FlattenContext.Identity, 0).ToList();

            result.Warnings.Add(
                $"[DIAG] Top-level model-space entities: {doc.Entities.Count()}. " +
                $"Flattened (incl. block/attribute contents): {flattened.Count}.");

            /*
             * ---------------------------------------------------------
             * 1. Find the spatial/index-map area from Sym_Hatch
             * ---------------------------------------------------------
             */

            double minX = double.MaxValue;
            double minY = double.MaxValue;
            double maxX = double.MinValue;
            double maxY = double.MinValue;

            int boundsPointCount = 0;
            bool usedHatch = true;

            foreach (var (entity, ctx) in flattened)
            {
                if (!LayerMatches(entity, HatchLayer))
                    continue;

                if (entity is not Hatch hatch)
                    continue;

                var bounds = hatch.GetBoundingBox();

                // GetBoundingBox() is in the hatch's own local coordinate
                // system, so all four corners must go through the transform
                // (rotation can tilt an axis-aligned local box) — not just
                // its Min/Max corners.
                foreach (var (cx, cy) in new[]
                {
                    (bounds.Min.X, bounds.Min.Y),
                    (bounds.Min.X, bounds.Max.Y),
                    (bounds.Max.X, bounds.Min.Y),
                    (bounds.Max.X, bounds.Max.Y)
                })
                {
                    var (wx, wy) = ctx.ToWorld(cx, cy);
                    minX = Math.Min(minX, wx);
                    minY = Math.Min(minY, wy);
                    maxX = Math.Max(maxX, wx);
                    maxY = Math.Max(maxY, wy);
                }

                boundsPointCount++;
            }

            if (boundsPointCount == 0)
            {
                // Fallback: some sheets draw the index-box outline as a
                // polyline or plain lines on Sym_Hatch instead of a HATCH
                // fill. Use their vertices/endpoints for the same bounding
                // box instead of giving up on the sheet entirely.
                usedHatch = false;

                foreach (var (entity, ctx) in flattened)
                {
                    if (!LayerMatches(entity, HatchLayer))
                        continue;

                    switch (entity)
                    {
                        case LwPolyline lwPoly:
                            foreach (var v in lwPoly.Vertices)
                            {
                                var (wx, wy) = ctx.ToWorld(v.Location.X, v.Location.Y);
                                minX = Math.Min(minX, wx);
                                minY = Math.Min(minY, wy);
                                maxX = Math.Max(maxX, wx);
                                maxY = Math.Max(maxY, wy);
                                boundsPointCount++;
                            }
                            break;

                        case Polyline2D poly2d:
                            foreach (var v in poly2d.Vertices)
                            {
                                var (wx, wy) = ctx.ToWorld(v.Location.X, v.Location.Y);
                                minX = Math.Min(minX, wx);
                                minY = Math.Min(minY, wy);
                                maxX = Math.Max(maxX, wx);
                                maxY = Math.Max(maxY, wy);
                                boundsPointCount++;
                            }
                            break;

                        case Line line:
                            foreach (var (lx, ly) in new[]
                            {
                                (line.StartPoint.X, line.StartPoint.Y),
                                (line.EndPoint.X, line.EndPoint.Y)
                            })
                            {
                                var (wx, wy) = ctx.ToWorld(lx, ly);
                                minX = Math.Min(minX, wx);
                                minY = Math.Min(minY, wy);
                                maxX = Math.Max(maxX, wx);
                                maxY = Math.Max(maxY, wy);
                                boundsPointCount++;
                            }
                            break;
                    }
                }
            }

            if (boundsPointCount == 0)
            {
                result.Warnings.Add(
                    $"No HATCH, LWPOLYLINE, POLYLINE, or LINE geometry found on layer '{HatchLayer}' " +
                    "(checked model space and block inserts).");

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

            if (!usedHatch)
            {
                result.Warnings.Add(
                    $"No HATCH found on layer '{HatchLayer}'; bounding box derived from " +
                    "polyline/line geometry on that layer instead.");
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

            result.Warnings.Add(
                $"[DIAG] Sym_Hatch bounds ({(usedHatch ? "HATCH" : "polyline/line fallback")}): " +
                $"X[{minX:F2}..{maxX:F2}] Y[{minY:F2}..{maxY:F2}]  " +
                $"(thirdWidth={thirdWidth:F2}, thirdHeight={thirdHeight:F2})");

            int titleEntityCount = 0;

            foreach (var (entity, ctx) in flattened)
            {
                if (!LayerMatches(entity, TitleLayer))
                    continue;

                double localX;
                double localY;
                string rawText;

                switch (entity)
                {
                    case TextEntity text:
                        localX = text.InsertPoint.X;
                        localY = text.InsertPoint.Y;
                        rawText = text.Value;
                        break;

                    case MText mtext:
                        localX = mtext.InsertPoint.X;
                        localY = mtext.InsertPoint.Y;
                        rawText = mtext.Value;
                        break;

                    default:
                        continue;
                }

                titleEntityCount++;

                var cleaned = Sanitize(rawText);

                var (x, y) = ctx.ToWorld(localX, localY);

                if (cleaned == null)
                {
                    result.Warnings.Add(
                        $"[DIAG] Sym_Title raw='{rawText}' at ({x:F2},{y:F2}) — " +
                        "discarded by Sanitize (empty/pure noise token).");
                    continue;
                }

                /*
                 * Ignore titles outside the Sym_Hatch spatial area.
                 */
                if (x < minX ||
                    x > maxX ||
                    y < minY ||
                    y > maxY)
                {
                    result.Warnings.Add(
                        $"[DIAG] Sym_Title text='{cleaned}' at ({x:F2},{y:F2}) — " +
                        $"OUTSIDE Sym_Hatch bounds X[{minX:F2}..{maxX:F2}] Y[{minY:F2}..{maxY:F2}].");
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

                result.Warnings.Add(
                    $"[DIAG] Sym_Title text='{cleaned}' at ({x:F2},{y:F2}) → row={row},col={column} → " +
                    $"cell={cell ?? "(none — not plus-shaped position)"}");

                if (cell != null)
                    cellTexts[cell].Add(cleaned);
            }

            result.Warnings.Add(
                $"[DIAG] Total Sym_Title TEXT/MTEXT/ATTRIB entities found: {titleEntityCount}");

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
                    $"No sheet number found in Center cell on layer '{TitleLayer}' " +
                    "(checked model space and block inserts).");
            }

            return Task.FromResult(result);
        }

        private static bool LayerMatches(Entity entity, string layerName)
        {
            var name = entity.Layer?.Name ?? string.Empty;
            return string.Equals(name, layerName, StringComparison.OrdinalIgnoreCase);
        }

        // A local-to-world affine transform: rotation + non-uniform scale +
        // translation, composed through nested block inserts. "Base" is the
        // owning block's base point — child entities are authored relative to
        // it, so it must be subtracted before scale/rotation are applied.
        private readonly struct FlattenContext
        {
            public double OriginX { get; }
            public double OriginY { get; }
            public double Rotation { get; }
            public double ScaleX { get; }
            public double ScaleY { get; }
            public double BaseX { get; }
            public double BaseY { get; }

            public FlattenContext(
                double originX, double originY, double rotation,
                double scaleX, double scaleY, double baseX, double baseY)
            {
                OriginX = originX;
                OriginY = originY;
                Rotation = rotation;
                ScaleX = scaleX;
                ScaleY = scaleY;
                BaseX = baseX;
                BaseY = baseY;
            }

            public static FlattenContext Identity => new(0, 0, 0, 1, 1, 0, 0);

            public (double X, double Y) ToWorld(double localX, double localY)
            {
                double cos = Math.Cos(Rotation);
                double sin = Math.Sin(Rotation);
                double lx = (localX - BaseX) * ScaleX;
                double ly = (localY - BaseY) * ScaleY;
                double rx = lx * cos - ly * sin;
                double ry = lx * sin + ly * cos;
                return (OriginX + rx, OriginY + ry);
            }
        }

        // Walks model-space entities plus, recursively, the contents of every
        // block an Insert references, yielding each leaf entity paired with
        // the transform that maps its own local coordinates to world space.
        //
        // NOTE: Insert.Block / BlockRecord.Entities / BlockRecord.BlockEntity
        // .BasePoint / Insert.XScale / Insert.YScale / Insert.Rotation /
        // Insert.Attributes are used here based on ACadSharp's standard
        // DXF-group-code-mapped naming (10/20=insert point, 41/42=X/Y scale,
        // 50=rotation, group 0 ATTRIB entities attached to an INSERT). This
        // could not be verified by compiling in this environment — if any of
        // these member names don't match the installed ACadSharp 3.7.1 API,
        // the actual member names/signatures are needed to fix this method.
        // AttributeEntity is assumed to derive from TextEntity (so it flows
        // through the existing "case TextEntity text" branch below) — if it
        // doesn't, that switch needs an explicit case for it instead.
        private static IEnumerable<(Entity Entity, FlattenContext Context)> FlattenEntities(
            IEnumerable<Entity> entities,
            FlattenContext ctx,
            int depth)
        {
            if (depth > MaxBlockDepth)
                yield break;

            foreach (var entity in entities)
            {
                if (entity is Insert insert)
                {
                    List<Entity>? children = null;
                    double baseX = 0;
                    double baseY = 0;

                    try
                    {
                        var block = insert.Block;
                        var blockEntities = block?.Entities;

                        if (blockEntities != null)
                        {
                            children = blockEntities.Cast<Entity>().ToList();

                            var basePoint = block?.BlockEntity?.BasePoint;
                            if (basePoint != null)
                            {
                                baseX = basePoint.Value.X;
                                baseY = basePoint.Value.Y;
                            }
                        }
                    }
                    catch
                    {
                        // Malformed or unsupported block reference — skip
                        // descending into it rather than fail the whole file.
                        children = null;
                    }

                    var (wx, wy) = ctx.ToWorld(insert.InsertPoint.X, insert.InsertPoint.Y);

                    var childCtx = new FlattenContext(
                        wx, wy,
                        ctx.Rotation + insert.Rotation,
                        ctx.ScaleX * insert.XScale,
                        ctx.ScaleY * insert.YScale,
                        baseX, baseY);

                    if (children != null)
                    {
                        foreach (var item in FlattenEntities(children, childCtx, depth + 1))
                            yield return item;
                    }

                    // Attribute VALUES (ATTRIB) belong to the Insert itself, not
                    // to the block definition — Block.Entities never contains
                    // them even though they render (and are positioned) exactly
                    // like the block's own text. A title block's own "Sheet No"
                    // field is very often exactly this: an attribute on the
                    // INSERT, while hand-placed neighbour numbers around it are
                    // plain TEXT/MTEXT already reachable via Block.Entities —
                    // which is why Center alone was coming back empty.
                    List<Entity>? attributes = null;

                    try
                    {
                        attributes = insert.Attributes?.Cast<Entity>().ToList();
                    }
                    catch
                    {
                        attributes = null;
                    }

                    if (attributes != null)
                    {
                        foreach (var attribute in attributes)
                            yield return (attribute, childCtx);
                    }

                    continue;
                }

                yield return (entity, ctx);
            }
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