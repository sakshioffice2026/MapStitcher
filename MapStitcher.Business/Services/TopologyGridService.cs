
using MapStitcher.Business.Contracts;
using MapStitcher.Model;
using System.Diagnostics.Metrics;

namespace MapStitcher.Business.Services
{
    public class TopologyGridService : ITopologyGridService
    {
        public TopologyBuildResult BuildGrid(List<SheetTopologyInput> inputs)
        {
            var result = new TopologyBuildResult();

            if (inputs.Count == 0)
                return result;

            var duplicateGroups = inputs
                .Where(i => !string.IsNullOrWhiteSpace(i.CenterSheetNumber))
                .GroupBy(
                    i => i.CenterSheetNumber!.Trim(),
                    StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1);

            foreach (var group in duplicateGroups)
            {
                result.Anomalies.Add(
                    $"Duplicate sheet number '{group.Key}' found in multiple uploaded files.");
            }

            var bySheetNumber = inputs
                .Where(i => !string.IsNullOrWhiteSpace(i.CenterSheetNumber))
                .GroupBy(
                    i => i.CenterSheetNumber!.Trim(),
                    StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() == 1)
                .ToDictionary(
                    g => g.Key,
                    g => g.Single(),
                    StringComparer.OrdinalIgnoreCase);

            var positions = new Dictionary<string, (int X, int Y)>();
            var queue = new Queue<SheetTopologyInput>();

            var start = inputs
                .FirstOrDefault(i =>
                    !string.IsNullOrWhiteSpace(i.CenterSheetNumber) &&
                    bySheetNumber.ContainsKey(i.CenterSheetNumber.Trim()));

            if (start == null)
            {
                result.Anomalies.Add(
                    "No unique valid starting sheet was found.");
                return result;
            }

            positions[start.SheetId] = (0, 0);
            queue.Enqueue(start);

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                var (x, y) = positions[current.SheetId];

                Visit(
                    current,
                    current.TopSheetNumber,
                    expectedOpposite: "Bottom",
                    nx: x,
                    ny: y + 1);

                Visit(
                    current,
                    current.BottomSheetNumber,
                    expectedOpposite: "Top",
                    nx: x,
                    ny: y - 1);

                Visit(
                    current,
                    current.LeftSheetNumber,
                    expectedOpposite: "Right",
                    nx: x - 1,
                    ny: y);

                Visit(
                    current,
                    current.RightSheetNumber,
                    expectedOpposite: "Left",
                    nx: x + 1,
                    ny: y);
            }

            foreach (var input in inputs)
            {
                if (!positions.ContainsKey(input.SheetId))
                {
                    result.Anomalies.Add(
                        $"{input.SheetId}: could not be placed because it is disconnected or has invalid reciprocal topology.");
                }
            }

            int minX = positions.Count > 0
                ? positions.Values.Min(p => p.X)
                : 0;

            int minY = positions.Count > 0
                ? positions.Values.Min(p => p.Y)
                : 0;

            foreach (var input in inputs)
            {
                bool placed = positions.TryGetValue(
                    input.SheetId,
                    out var position);

                result.Sheets.Add(new SheetTopologyResult
                {
                    SheetId = input.SheetId,
                    SheetNumber = input.CenterSheetNumber,
                    GridX = placed ? position.X - minX : 0,
                    GridY = placed ? position.Y - minY : 0,
                    Placed = placed
                });
            }

            return result;

            void Visit(
                SheetTopologyInput current,
                string? neighborSheetNumber,
                string expectedOpposite,
                int nx,
                int ny)
            {
                if (string.IsNullOrWhiteSpace(neighborSheetNumber))
                    return;

                var number = neighborSheetNumber.Trim();

                if (!bySheetNumber.TryGetValue(number, out var neighbor))
                {
                    result.Anomalies.Add(
                        $"{current.SheetId}: neighbor '{number}' not found among uploaded sheets.");
                    return;
                }

                bool reciprocal = expectedOpposite switch
                {
                    "Top" =>
                        SameSheetNumber(
                            neighbor.TopSheetNumber,
                            current.CenterSheetNumber),

                    "Bottom" =>
                        SameSheetNumber(
                            neighbor.BottomSheetNumber,
                            current.CenterSheetNumber),

                    "Left" =>
                        SameSheetNumber(
                            neighbor.LeftSheetNumber,
                            current.CenterSheetNumber),

                    "Right" =>
                        SameSheetNumber(
                            neighbor.RightSheetNumber,
                            current.CenterSheetNumber),

                    _ => false
                };

                if (!reciprocal)
                {
                    result.Anomalies.Add(
                        $"Invalid reciprocal relationship: " +
                        $"sheet '{current.CenterSheetNumber}' points {GetDirection(current, number)} to " +
                        $"'{number}', but '{number}' does not point {expectedOpposite} back to " +
                        $"'{current.CenterSheetNumber}'.");
                    return;
                }

                if (positions.TryGetValue(
                    neighbor.SheetId,
                    out var existing))
                {
                    if (existing != (nx, ny))
                    {
                        result.Anomalies.Add(
                            $"Spatial conflict: sheet '{neighbor.CenterSheetNumber}' " +
                            $"computed at ({nx},{ny}) via '{current.CenterSheetNumber}', " +
                            $"but already placed at ({existing.X},{existing.Y}).");
                    }

                    return;
                }

                positions[neighbor.SheetId] = (nx, ny);
                queue.Enqueue(neighbor);
            }

            static bool SameSheetNumber(
                string? actual,
                string? expected)
            {
                return !string.IsNullOrWhiteSpace(actual) &&
                       !string.IsNullOrWhiteSpace(expected) &&
                       string.Equals(
                           actual.Trim(),
                           expected.Trim(),
                           StringComparison.OrdinalIgnoreCase);
            }

            static string GetDirection(
                SheetTopologyInput input,
                string neighbor)
            {
                if (SameSheetNumber(input.TopSheetNumber, neighbor))
                    return "North";

                if (SameSheetNumber(input.BottomSheetNumber, neighbor))
                    return "South";

                if (SameSheetNumber(input.LeftSheetNumber, neighbor))
                    return "West";

                if (SameSheetNumber(input.RightSheetNumber, neighbor))
                    return "East";

                return "toward";
            }
        }
    }
}
