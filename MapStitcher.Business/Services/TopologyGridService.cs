using MapStitcher.Business.Contracts;
using MapStitcher.Model;

namespace MapStitcher.Business.Services
{
    // Builds a relative (GridX, GridY) layout from index-map neighbour data.
    //
    // Real cadastral index boxes are hand-drafted, so links are frequently
    // one-sided: sheet 4's index box names 7 to its north, but sheet 7's box
    // leaves its south cell blank or misspells it. Treating that as fatal
    // (the previous behaviour) rejected the link, aborted that BFS branch, and
    // cascaded into every downstream sheet being reported "disconnected".
    //
    // This builder never discards a sheet. It places what it can at the highest
    // available confidence and downgrades rather than dropping:
    //
    //   Pass 1  Reciprocal links only — both sheets agree. Highest confidence.
    //   Pass 2  One-sided links — only one sheet names the other. Logged as a
    //           warning, still placed.
    //   Pass 3  Remaining sheets form their own components, laid out in separate
    //           bands below the main component so nothing is lost.
    //
    // Sheets with no extractable centre number and duplicate centre numbers are
    // given synthetic identities instead of being excluded from the graph.
    public class TopologyGridService : ITopologyGridService
    {
        private const int ComponentBandGap = 1;

        public TopologyBuildResult BuildGrid(List<SheetTopologyInput> inputs)
        {
            var result = new TopologyBuildResult();

            if (inputs.Count == 0)
                return result;

            // ---------------------------------------------------------------
            // Identity resolution.
            //
            // Every sheet gets a resolved key, even when its centre number is
            // missing or shared with another file. Only the FIRST file claiming
            // a given number becomes the routable target for that number;
            // later claimants keep a synthetic key so they stay in the graph as
            // their own node rather than vanishing.
            // ---------------------------------------------------------------
            var routableByNumber = new Dictionary<string, SheetTopologyInput>(
                StringComparer.OrdinalIgnoreCase);

            var numberlessSheets = new List<SheetTopologyInput>();

            foreach (var input in inputs)
            {
                var number = Normalize(input.CenterSheetNumber);

                if (number == null)
                {
                    numberlessSheets.Add(input);
                    result.Anomalies.Add(
                        $"{ShortName(input)}: no centre sheet number extracted; " +
                        $"placed as a standalone sheet (no neighbour links usable).");
                    continue;
                }

                if (routableByNumber.TryGetValue(number, out var existing))
                {
                    result.Anomalies.Add(
                        $"Duplicate sheet number '{number}': {ShortName(input)} and " +
                        $"{ShortName(existing)}. Neighbour links pointing at '{number}' " +
                        $"resolve to {ShortName(existing)}; {ShortName(input)} is placed separately.");
                    continue;
                }

                routableByNumber[number] = input;
            }

            var positions = new Dictionary<string, (int X, int Y)>();
            var reciprocalPlacement = new HashSet<string>();

            // ---------------------------------------------------------------
            // Pass 1 & 2 — grow components out of every seed, reciprocal links
            // first, then one-sided links. Repeating until nothing new is
            // placed lets a sheet reached only by a weak link still act as a
            // hub for strong links further out.
            // ---------------------------------------------------------------
            var componentAnchors = new List<string>();

            foreach (var seed in inputs)
            {
                if (positions.ContainsKey(seed.SheetId))
                    continue;

                // New component: start it at its own local origin. Absolute
                // offsets between components are applied after all growth.
                positions[seed.SheetId] = (0, 0);
                reciprocalPlacement.Add(seed.SheetId);
                componentAnchors.Add(seed.SheetId);

                bool grew;
                do
                {
                    grew = Grow(requireReciprocal: true);

                    if (!grew)
                        grew = Grow(requireReciprocal: false);
                }
                while (grew);
            }

            // ---------------------------------------------------------------
            // Component packing.
            //
            // Each component was grown around its own (0,0). Pack them into a
            // compact shelf layout rather than giving each its own full-width
            // band — otherwise a handful of isolated sheets produce a grid that
            // is mostly empty cells.
            // ---------------------------------------------------------------
            PackComponents();

            NormalizeToOrigin();

            ReportMissingNeighbours();

            // Growth sweeps re-traverse placed sheets, so the same observation
            // can be recorded more than once. Collapse to unique messages.
            result.Anomalies = result.Anomalies
                .Distinct(StringComparer.Ordinal)
                .ToList();

            foreach (var input in inputs)
            {
                bool placed = positions.TryGetValue(input.SheetId, out var position);

                result.Sheets.Add(new SheetTopologyResult
                {
                    SheetId = input.SheetId,
                    SheetNumber = input.CenterSheetNumber,
                    GridX = placed ? position.X : 0,
                    GridY = placed ? position.Y : 0,
                    Placed = placed
                });
            }

            return result;

            // ===============================================================
            // Local functions
            // ===============================================================

            // One sweep over every already-placed sheet, trying to attach its
            // named neighbours. Returns true if anything new was placed.
            bool Grow(bool requireReciprocal)
            {
                bool placedAnything = false;

                // Snapshot: positions is mutated inside the loop.
                var frontier = positions.Keys.ToList();

                foreach (var sheetId in frontier)
                {
                    var current = inputs.FirstOrDefault(i => i.SheetId == sheetId);
                    if (current == null)
                        continue;

                    var (x, y) = positions[sheetId];

                    placedAnything |= TryAttach(current, current.TopSheetNumber, "Top", "Bottom", x, y + 1, requireReciprocal);
                    placedAnything |= TryAttach(current, current.BottomSheetNumber, "Bottom", "Top", x, y - 1, requireReciprocal);
                    placedAnything |= TryAttach(current, current.LeftSheetNumber, "Left", "Right", x - 1, y, requireReciprocal);
                    placedAnything |= TryAttach(current, current.RightSheetNumber, "Right", "Left", x + 1, y, requireReciprocal);
                }

                return placedAnything;
            }

            bool TryAttach(
                SheetTopologyInput current,
                string? neighbourSheetNumber,
                string direction,
                string expectedOpposite,
                int nx,
                int ny,
                bool requireReciprocal)
            {
                var number = Normalize(neighbourSheetNumber);
                if (number == null)
                    return false;

                if (!routableByNumber.TryGetValue(number, out var neighbour))
                {
                    // Reported once after all growth completes, not here — this
                    // path is re-entered on every sweep.
                    return false;
                }

                if (neighbour.SheetId == current.SheetId)
                    return false;

                bool reciprocal = IsReciprocal(neighbour, expectedOpposite, current.CenterSheetNumber);

                if (requireReciprocal && !reciprocal)
                    return false;

                if (positions.TryGetValue(neighbour.SheetId, out var existing))
                {
                    // Already placed. Only flag a genuine spatial disagreement,
                    // and only when both placements were reciprocal — a conflict
                    // against a weakly-placed sheet is expected noise.
                    if (existing != (nx, ny) &&
                        reciprocal &&
                        reciprocalPlacement.Contains(neighbour.SheetId) &&
                        reciprocalPlacement.Contains(current.SheetId))
                    {
                        result.Anomalies.Add(
                            $"Spatial conflict: '{neighbour.CenterSheetNumber}' computed at " +
                            $"({nx},{ny}) via '{current.CenterSheetNumber}', but already placed " +
                            $"at ({existing.X},{existing.Y}). Keeping the earlier position.");
                    }
                    return false;
                }

                // Cell already taken by a different sheet in this component —
                // don't stack two sheets on one cell.
                if (positions.Any(p => p.Value == (nx, ny)))
                {
                    if (reciprocal)
                    {
                        result.Anomalies.Add(
                            $"Cell ({nx},{ny}) is already occupied, so '{neighbour.CenterSheetNumber}' " +
                            $"could not be placed {Compass(direction)} of '{current.CenterSheetNumber}'.");
                    }
                    return false;
                }

                positions[neighbour.SheetId] = (nx, ny);

                if (reciprocal)
                {
                    reciprocalPlacement.Add(neighbour.SheetId);
                }
                else
                {
                    result.Anomalies.Add(
                        $"One-sided link: '{current.CenterSheetNumber}' points {Compass(direction)} " +
                        $"to '{neighbour.CenterSheetNumber}', but '{neighbour.CenterSheetNumber}' does " +
                        $"not point {Compass(expectedOpposite)} back. Placed anyway — verify this edge.");
                }

                return true;
            }

            // Reports each unresolvable neighbour reference once, after the
            // graph has stopped growing.
            void ReportMissingNeighbours()
            {
                foreach (var sheet in inputs)
                {
                    Check(sheet.TopSheetNumber, "Top");
                    Check(sheet.BottomSheetNumber, "Bottom");
                    Check(sheet.LeftSheetNumber, "Left");
                    Check(sheet.RightSheetNumber, "Right");

                    void Check(string? neighbourNumber, string direction)
                    {
                        var number = Normalize(neighbourNumber);

                        if (number == null || routableByNumber.ContainsKey(number))
                            return;

                        result.Anomalies.Add(
                            $"{Describe(sheet)}: index box names '{number}' to the " +
                            $"{Compass(direction)}, but that sheet has not been uploaded.");
                    }
                }
            }

            // Packs every connected component into a compact shelf layout.
            //
            // Components are laid left to right along a shelf and wrapped onto a
            // new shelf once the target width is reached, so a set of isolated
            // single sheets fills a tight block instead of one sheet per row.
            void PackComponents()
            {
                var components = BuildComponents();

                if (components.Count <= 1)
                    return;

                // Largest components first so the layout is dominated by real
                // topology, with loose sheets filling in around it.
                components = components
                    .OrderByDescending(c => c.Count)
                    .ToList();

                int totalCells = components.Sum(c => c.Count);

                int targetWidth = Math.Max(
                    components.Max(ComponentWidth),
                    (int)Math.Ceiling(Math.Sqrt(Math.Max(totalCells, 1))));

                int cursorX = 0;
                int shelfTopY = 0;
                int shelfHeight = 0;

                foreach (var component in components)
                {
                    int width = ComponentWidth(component);
                    int height = ComponentHeight(component);

                    if (cursorX > 0 && cursorX + width > targetWidth)
                    {
                        shelfTopY -= shelfHeight + ComponentBandGap;
                        cursorX = 0;
                        shelfHeight = 0;
                    }

                    int localMinX = component.Min(id => positions[id].X);
                    int localMaxY = component.Max(id => positions[id].Y);

                    int shiftX = cursorX - localMinX;
                    int shiftY = shelfTopY - localMaxY;

                    foreach (var id in component)
                    {
                        var p = positions[id];
                        positions[id] = (p.X + shiftX, p.Y + shiftY);
                    }

                    cursorX += width + ComponentBandGap;
                    shelfHeight = Math.Max(shelfHeight, height);
                }
            }

            // Flood-fills the placement graph from each anchor through actual
            // neighbour links to recover component membership.
            List<List<string>> BuildComponents()
            {
                var seen = new HashSet<string>();
                var components = new List<List<string>>();

                foreach (var anchor in componentAnchors)
                {
                    if (seen.Contains(anchor))
                        continue;

                    var members = new List<string>();
                    var stack = new Stack<string>();
                    stack.Push(anchor);

                    while (stack.Count > 0)
                    {
                        var id = stack.Pop();

                        if (!seen.Add(id))
                            continue;

                        members.Add(id);

                        var sheet = inputs.FirstOrDefault(i => i.SheetId == id);
                        if (sheet == null)
                            continue;

                        foreach (var linked in LinkedSheetIds(sheet))
                        {
                            if (positions.ContainsKey(linked) && !seen.Contains(linked))
                                stack.Push(linked);
                        }
                    }

                    if (members.Count > 0)
                        components.Add(members);
                }

                return components;
            }

            int ComponentWidth(List<string> component) =>
                component.Max(id => positions[id].X) -
                component.Min(id => positions[id].X) + 1;

            int ComponentHeight(List<string> component) =>
                component.Max(id => positions[id].Y) -
                component.Min(id => positions[id].Y) + 1;

            IEnumerable<string> LinkedSheetIds(SheetTopologyInput sheet)
            {
                foreach (var number in new[]
                {
                    sheet.TopSheetNumber,
                    sheet.BottomSheetNumber,
                    sheet.LeftSheetNumber,
                    sheet.RightSheetNumber
                })
                {
                    var normalized = Normalize(number);
                    if (normalized != null &&
                        routableByNumber.TryGetValue(normalized, out var target))
                    {
                        yield return target.SheetId;
                    }
                }
            }

            void NormalizeToOrigin()
            {
                if (positions.Count == 0)
                    return;

                int minX = positions.Values.Min(p => p.X);
                int minY = positions.Values.Min(p => p.Y);

                foreach (var id in positions.Keys.ToList())
                {
                    var p = positions[id];
                    positions[id] = (p.X - minX, p.Y - minY);
                }
            }

            string Describe(SheetTopologyInput input) =>
                string.IsNullOrWhiteSpace(input.CenterSheetNumber)
                    ? ShortName(input)
                    : $"Sheet '{input.CenterSheetNumber}'";
        }

        private static bool IsReciprocal(
            SheetTopologyInput neighbour,
            string expectedOpposite,
            string? currentCenterNumber)
        {
            var actual = expectedOpposite switch
            {
                "Top" => neighbour.TopSheetNumber,
                "Bottom" => neighbour.BottomSheetNumber,
                "Left" => neighbour.LeftSheetNumber,
                "Right" => neighbour.RightSheetNumber,
                _ => null
            };

            var a = Normalize(actual);
            var b = Normalize(currentCenterNumber);

            return a != null && b != null &&
                   string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        // Strips the surveyor annotations that commonly occupy index-box cells
        // in place of a real sheet number ("-", "NIL", "RIVER", "VILLAGE
        // BOUNDARY"), so they are treated as "no neighbour" rather than as a
        // sheet that can never be found.
        private static string? Normalize(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;

            var trimmed = value.Trim();

            if (trimmed.Length == 0)
                return null;

            if (trimmed.All(c => c == '-' || c == '_' || c == '.'))
                return null;

            string[] nonSheetTokens =
            {
                "NIL", "NA", "N/A", "NONE", "RIVER", "NALA", "ROAD",
                "VILLAGE BOUNDARY", "BOUNDARY", "X"
            };

            if (nonSheetTokens.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
                return null;

            return trimmed;
        }

        private static string Compass(string direction) => direction switch
        {
            "Top" => "north",
            "Bottom" => "south",
            "Left" => "west",
            "Right" => "east",
            _ => direction.ToLowerInvariant()
        };

        private static string ShortName(SheetTopologyInput input)
        {
            try
            {
                return Path.GetFileName(input.SheetId);
            }
            catch
            {
                return input.SheetId;
            }
        }
    }
}