// MapStitcher.Business/Services/TopologyGridService.cs
using MapStitcher.Business.Contracts;
using MapStitcher.Model;

namespace MapStitcher.Business.Services
{
    public class TopologyGridService : ITopologyGridService
    {
        public TopologyBuildResult BuildGrid(List<SheetTopologyInput> inputs)
        {
            var result = new TopologyBuildResult();
            if (inputs.Count == 0) return result;

            var bySheetNumber = inputs
                .Where(i => !string.IsNullOrWhiteSpace(i.CenterSheetNumber))
                .GroupBy(i => i.CenterSheetNumber!.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            var positions = new Dictionary<string, (int X, int Y)>();
            var queue = new Queue<SheetTopologyInput>();

            var start = inputs[0];
            positions[start.SheetId] = (0, 0);
            queue.Enqueue(start);

            while (queue.Count > 0)
            {
                var u = queue.Dequeue();
                var (ux, uy) = positions[u.SheetId];

                Visit(u.TopSheetNumber, ux, uy + 1, u.SheetId);
                Visit(u.BottomSheetNumber, ux, uy - 1, u.SheetId);
                Visit(u.LeftSheetNumber, ux - 1, uy, u.SheetId);
                Visit(u.RightSheetNumber, ux + 1, uy, u.SheetId);
            }

            void Visit(string? neighborSheetNumber, int nx, int ny, string fromId)
            {
                if (string.IsNullOrWhiteSpace(neighborSheetNumber)) return;

                if (!bySheetNumber.TryGetValue(neighborSheetNumber.Trim(), out var v))
                {
                    result.Anomalies.Add($"{fromId}: neighbor '{neighborSheetNumber}' not found among uploaded sheets.");
                    return;
                }

                if (positions.TryGetValue(v.SheetId, out var existing))
                {
                    if (existing != (nx, ny))
                        result.Anomalies.Add(
                            $"Conflict: sheet '{v.CenterSheetNumber}' computed at ({nx},{ny}) via {fromId}, " +
                            $"but already placed at ({existing.X},{existing.Y}).");
                    return;
                }

                positions[v.SheetId] = (nx, ny);
                queue.Enqueue(v);
            }

            foreach (var input in inputs)
            {
                if (!positions.ContainsKey(input.SheetId))
                    result.Anomalies.Add($"{input.SheetId}: could not be placed — no connecting neighbor reference found.");
            }

            int minX = positions.Count > 0 ? positions.Values.Min(p => p.X) : 0;
            int minY = positions.Count > 0 ? positions.Values.Min(p => p.Y) : 0;

            foreach (var input in inputs)
            {
                bool placed = positions.TryGetValue(input.SheetId, out var pos);
                result.Sheets.Add(new SheetTopologyResult
                {
                    SheetId = input.SheetId,
                    SheetNumber = input.CenterSheetNumber,
                    GridX = placed ? pos.X - minX : 0,
                    GridY = placed ? pos.Y - minY : 0,
                    Placed = placed
                });
            }

            return result;
        }
    }
}