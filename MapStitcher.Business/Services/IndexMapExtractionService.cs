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

        // Width/height of the genuine title-box Sym_Hatch symbol is
        // consistently ~0.49 across every sheet where detection is known
        // to work correctly. When more than one HATCH entity shares the
        // Sym_Hatch layer (stray fill patterns, page borders, etc.), the
        // candidate whose own aspect ratio is closest to this value is the
        // real title box; the others are noise and must not be merged in.
        private const double ExpectedHatchAspectRatio = 0.4926;

        // Every genuine title-box hatch across the known-good sheets has
        // deviated from ExpectedHatchAspectRatio by at most ~0.32; the
        // unrelated stamp/logo hatch present in every file deviates by
        // ~1.22. This sits comfortably between the two, so a candidate
        // whose closest aspect still exceeds it is not the title box —
        // even if it is the only candidate found.
        private const double MaxAspectDeviation = 0.6;

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
             * 1. Locate the Sym_Hatch title-box (if present) — used only
             *    as a positional anchor for "self", never as a spatial
             *    containment filter. Neighbour numbers on real drawings
             *    routinely sit well outside this box.
             * ---------------------------------------------------------
             */

            var hatchCandidates =
                new List<(double MinX, double MinY, double MaxX, double MaxY)>();

            foreach (var (entity, ctx) in flattened)
            {
                if (!LayerMatches(entity, HatchLayer))
                    continue;

                if (entity is not Hatch hatch)
                    continue;

                var bounds = hatch.GetBoundingBox();

                double cMinX = double.MaxValue, cMinY = double.MaxValue;
                double cMaxX = double.MinValue, cMaxY = double.MinValue;

                foreach (var (cx, cy) in new[]
                {
                    (bounds.Min.X, bounds.Min.Y),
                    (bounds.Min.X, bounds.Max.Y),
                    (bounds.Max.X, bounds.Min.Y),
                    (bounds.Max.X, bounds.Max.Y)
                })
                {
                    var (wx, wy) = ctx.ToWorld(cx, cy);
                    cMinX = Math.Min(cMinX, wx);
                    cMinY = Math.Min(cMinY, wy);
                    cMaxX = Math.Max(cMaxX, wx);
                    cMaxY = Math.Max(cMaxY, wy);
                }

                hatchCandidates.Add((cMinX, cMinY, cMaxX, cMaxY));
            }

            (double X, double Y)? hatchCenter = null;

            if (hatchCandidates.Count > 0)
            {
                if (hatchCandidates.Count > 1)
                {
                    foreach (var c in hatchCandidates)
                    {
                        double cw = c.MaxX - c.MinX;
                        double ch = c.MaxY - c.MinY;
                        double aspect = ch > 0 ? cw / ch : double.NaN;

                        result.Warnings.Add(
                            $"[DIAG] Sym_Hatch candidate: X[{c.MinX:F2}..{c.MaxX:F2}] " +
                            $"Y[{c.MinY:F2}..{c.MaxY:F2}] size={cw:F2}x{ch:F2} " +
                            $"aspect={aspect:F4}");
                    }
                }

                var best =
                    hatchCandidates
                        .OrderBy(c =>
                        {
                            double cw = c.MaxX - c.MinX;
                            double ch = c.MaxY - c.MinY;
                            double aspect = ch > 0 ? cw / ch : double.MaxValue;
                            return Math.Abs(aspect - ExpectedHatchAspectRatio);
                        })
                        .First();

                double bestW = best.MaxX - best.MinX;
                double bestH = best.MaxY - best.MinY;
                double bestAspect = bestH > 0 ? bestW / bestH : double.MaxValue;
                double bestDeviation = Math.Abs(bestAspect - ExpectedHatchAspectRatio);

                if (bestDeviation <= MaxAspectDeviation)
                {
                    hatchCenter = ((best.MinX + best.MaxX) / 2.0, (best.MinY + best.MaxY) / 2.0);

                    result.Warnings.Add(
                        $"[DIAG] Sym_Hatch anchor: {hatchCandidates.Count} candidate(s); " +
                        $"using center ({hatchCenter.Value.X:F2},{hatchCenter.Value.Y:F2}) " +
                        "as self-number anchor only (not a containment filter).");
                }
                else
                {
                    result.Warnings.Add(
                        $"[DIAG] Sym_Hatch: closest candidate aspect={bestAspect:F4} " +
                        $"deviates {bestDeviation:F4} from expected {ExpectedHatchAspectRatio:F4} " +
                        $"(> {MaxAspectDeviation:F4} tolerance) — no genuine title-box hatch on " +
                        "this sheet; falling back to 'N)' self-label token.");
                }
            }
            else
            {
                result.Warnings.Add(
                    $"No HATCH found on layer '{HatchLayer}'; self number will be read " +
                    "from the 'N)' sheet-label token on Sym_Title instead, and neighbour " +
                    "detection will be skipped for this sheet.");
            }

            /*
             * ---------------------------------------------------------
             * 2. Read every Sym_Title TEXT/MTEXT entity, in world space,
             *    with no spatial filtering at all.
             * ---------------------------------------------------------
             */

            var plainNumeric = new List<(string Value, double X, double Y)>();
            (string Value, double X, double Y)? parenSelf = null;

            int titleEntityCount = 0;
            var plainNumericRegex = new Regex(@"^\d+$", RegexOptions.Compiled);
            var parenNumericRegex = new Regex(@"^(\d+)\)$", RegexOptions.Compiled);

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

                if (plainNumericRegex.IsMatch(cleaned))
                {
                    plainNumeric.Add((cleaned, x, y));
                    result.Warnings.Add(
                        $"[DIAG] Sym_Title text='{cleaned}' at ({x:F2},{y:F2}) — numeric candidate.");
                    continue;
                }

                var parenMatch = parenNumericRegex.Match(cleaned);
                if (parenMatch.Success)
                {
                    // The sheet's own "शीट क्र (N)" self-declaration label.
                    // Kept only as a fallback self anchor (see below) — not
                    // fully trusted as primary, since this token is
                    // occasionally corrupted in the source drawing.
                    parenSelf ??= (parenMatch.Groups[1].Value, x, y);

                    result.Warnings.Add(
                        $"[DIAG] Sym_Title text='{cleaned}' at ({x:F2},{y:F2}) — " +
                        "self-label token ('N)').");
                    continue;
                }

                result.Warnings.Add(
                    $"[DIAG] Sym_Title raw='{rawText}' at ({x:F2},{y:F2}) — " +
                    $"cleaned='{cleaned}' ignored (not a bare number or 'N)' label).");
            }

            result.Warnings.Add(
                $"[DIAG] Total Sym_Title TEXT/MTEXT/ATTRIB entities found: {titleEntityCount}");

            /*
             * ---------------------------------------------------------
             * 3. Resolve "self": nearest plain-numeric candidate to the
             *    Sym_Hatch anchor when a hatch was found; otherwise fall
             *    back to the 'N)' self-label token.
             * ---------------------------------------------------------
             */

            (string Value, double X, double Y)? self = null;

            if (hatchCenter != null && plainNumeric.Count > 0)
            {
                self =
                    plainNumeric
                        .OrderBy(c => Distance(c.X, c.Y, hatchCenter.Value.X, hatchCenter.Value.Y))
                        .Select(c => ((string Value, double X, double Y)?)c)
                        .First();

                result.Warnings.Add(
                    $"[DIAG] Self resolved from nearest-to-hatch-center: " +
                    $"'{self.Value.Value}' at ({self.Value.X:F2},{self.Value.Y:F2}).");
            }
            else if (parenSelf != null)
            {
                self = parenSelf;

                result.Warnings.Add(
                    $"[DIAG] Self resolved from 'N)' label fallback (no usable hatch anchor): " +
                    $"'{self.Value.Value}' at ({self.Value.X:F2},{self.Value.Y:F2}).");
            }

            result.CenterSheetNumber = self?.Value;

            if (result.CenterSheetNumber == null)
            {
                result.Warnings.Add(
                    $"No sheet number found on layer '{TitleLayer}' " +
                    "(checked model space and block inserts).");
            }

            /*
             * ---------------------------------------------------------
             * 4. Resolve neighbours by displacement direction from self,
             *    not by containment in any bounding box. Skipped entirely
             *    when self came from the 'N)' fallback, since that anchor
             *    has no reliable spatial relationship to any neighbour
             *    cluster on the sheet.
             * ---------------------------------------------------------
             */

            if (self != null && hatchCenter != null)
            {
                var directional =
                    new Dictionary<string, List<(string Text, double X, double Y)>>
                    {
                        ["Top"] = new(),
                        ["Bottom"] = new(),
                        ["Left"] = new(),
                        ["Right"] = new()
                    };

                foreach (var candidate in plainNumeric)
                {
                    if (candidate.X == self.Value.X && candidate.Y == self.Value.Y)
                        continue;

                    double dx = candidate.X - self.Value.X;
                    double dy = candidate.Y - self.Value.Y;

                    // Dominant axis decides North/South vs East/West;
                    // CAD coordinates: Y increases upward.
                    string direction =
                        Math.Abs(dy) >= Math.Abs(dx)
                            ? (dy > 0 ? "Top" : "Bottom")
                            : (dx > 0 ? "Right" : "Left");

                    directional[direction].Add((candidate.Value, candidate.X, candidate.Y));

                    result.Warnings.Add(
                        $"[DIAG] Sym_Title text='{candidate.Value}' at " +
                        $"({candidate.X:F2},{candidate.Y:F2}) → dx={dx:F2},dy={dy:F2} → {direction}.");
                }

                result.TopSheetNumber =
                    PickNearest(directional["Top"], "Top", self.Value.X, self.Value.Y, result.Warnings);

                result.BottomSheetNumber =
                    PickNearest(directional["Bottom"], "Bottom", self.Value.X, self.Value.Y, result.Warnings);

                result.LeftSheetNumber =
                    PickNearest(directional["Left"], "Left", self.Value.X, self.Value.Y, result.Warnings);

                result.RightSheetNumber =
                    PickNearest(directional["Right"], "Right", self.Value.X, self.Value.Y, result.Warnings);
            }
            else
            {
                result.Warnings.Add(
                    "[DIAG] Neighbour detection skipped: no Sym_Hatch anchor available " +
                    "for this sheet.");
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

        private static string? PickNearest(
            List<(string Text, double X, double Y)> candidates,
            string direction,
            double selfX,
            double selfY,
            List<string> warnings)
        {
            if (candidates.Count == 0)
                return null;

            var ordered =
                candidates
                    .OrderBy(c => Distance(c.X, c.Y, selfX, selfY))
                    .ToList();

            var distinctTexts =
                ordered
                    .Select(c => c.Text)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

            if (distinctTexts.Count > 1)
            {
                warnings.Add(
                    $"{direction} direction has conflicting labels: " +
                    $"{string.Join(", ", distinctTexts)}. " +
                    $"Using '{ordered[0].Text}' (nearest to self).");
            }

            return ordered[0].Text;
        }

        private static double Distance(double x1, double y1, double x2, double y2)
        {
            double dx = x1 - x2;
            double dy = y1 - y2;
            return Math.Sqrt(dx * dx + dy * dy);
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