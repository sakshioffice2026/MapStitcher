// Services/CadastralMergeService.cs — full file
using MapStitcher.Business.Contracts;
using MapStitcher.Database;
using MapStitcher.Repositories.Contracts;

namespace MapStitcher.Business.Services
{
    // Merge strategy: tie-point X,Y coordinate matching is the primary and only
    // gate for merging (label match first, spatial-proximity fallback second).
    // The Laghu Reference chain, when present, is recorded for traceability only
    // — it no longer blocks a merge that tie points otherwise support.
    // Translation is composed onto the base sheet's existing global position, so
    // a root/anchor sheet (TransformTranslateX/Y = 0) stays at the origin and
    // every merge downstream of it accumulates correctly in one shared frame.
    //
    // Offset computation (XY, not one-axis): every matched tie-point pair gives
    // its own candidate (Dx, Dy) = (base.SourceX - adjacent.SourceX, base.SourceY
    // - adjacent.SourceY). Pairs that disagree with the group median by more than
    // OutlierResidualThreshold are rejected before the final (ΔX, ΔY) mean and
    // RMS error are computed from the surviving inliers only.
    public class CadastralMergeService : ICadastralMergeService
    {
        private const double SpatialThreshold = 25.0; // pixels — used for centroid fallback matching

        // Same units as TiePoint.SourceX/SourceY. A pair whose (dx,dy) residual
        // from the group median exceeds this is treated as a bad/mismatched
        // tie-point label and excluded from the final offset + RMS.
        private const double OutlierResidualThreshold = 25.0;

        private readonly ITiePointRepository _tiePointRepo;
        private readonly ISurveySheetRepository _sheetRepo;

        public CadastralMergeService(
            ITiePointRepository tiePointRepo,
            ISurveySheetRepository sheetRepo)
        {
            _tiePointRepo = tiePointRepo;
            _sheetRepo = sheetRepo;
        }

        public Task<MergeResult> MergeSheetsAsync(int baseSheetId, int adjacentSheetId) =>
            ComputeAsync(baseSheetId, adjacentSheetId, persist: true);

        public Task<MergeResult> PreviewMergeAsync(int baseSheetId, int adjacentSheetId) =>
            ComputeAsync(baseSheetId, adjacentSheetId, persist: false);

        private async Task<MergeResult> ComputeAsync(
            int baseSheetId,
            int adjacentSheetId,
            bool persist)
        {
            var baseSheet =
                await _sheetRepo.GetByIdAsync(baseSheetId)
                ?? throw new InvalidOperationException(
                    $"Base sheet {baseSheetId} not found.");

            var adjacentSheet =
                await _sheetRepo.GetByIdAsync(adjacentSheetId)
                ?? throw new InvalidOperationException(
                    $"Adjacent sheet {adjacentSheetId} not found.");

            var basePoints = await _tiePointRepo.GetBySheetIdAsync(baseSheetId);
            var adjacentPoints = await _tiePointRepo.GetBySheetIdAsync(adjacentSheetId);
            var matchedPairs = FindMatchingPairs(basePoints, adjacentPoints);

            string direction = DetermineDirection(baseSheet, adjacentSheet);
            string gridRelationship = DescribeGridRelationship(direction);

            var offset = ComputeRobustOffset(matchedPairs);

            if (!offset.HaveOffset)
            {
                return new MergeResult
                {
                    Success = false,
                    MatchedPointCount = matchedPairs.Count,
                    Direction = direction,
                    GridRelationship = gridRelationship,
                    MatchedPoints = offset.Details,
                    Message = $"No tie points on sheet {baseSheet.SheetNumber} and/or {adjacentSheet.SheetNumber} to compute an X,Y offset.",
                    FailureReason = "Needs manual check: no tie points available for coordinate matching"
                };
            }

            // Compose onto the base sheet's existing global position. A never-merged
            // root sheet has TransformTranslateX/Y == 0, so it anchors the mosaic at
            // the origin; every sheet merged onto it (directly or via a chain)
            // accumulates the correct absolute X,Y from there.
            double globalTranslateX = baseSheet.TransformTranslateX + offset.DeltaX;
            double globalTranslateY = baseSheet.TransformTranslateY + offset.DeltaY;

            string? linkedVia = ResolveLaghuLink(baseSheet, adjacentSheet);

            if (persist)
            {
                foreach (var point in adjacentPoints)
                {
                    point.TargetX = point.SourceX + globalTranslateX;
                    point.TargetY = point.SourceY + globalTranslateY;
                }

                adjacentSheet.TransformRotation = 0.0;
                adjacentSheet.TransformScale = 1.0;
                adjacentSheet.TransformTranslateX = globalTranslateX;
                adjacentSheet.TransformTranslateY = globalTranslateY;

                adjacentSheet.Status = SheetStatus.Merged;
                baseSheet.Status = SheetStatus.Merged;

                await _tiePointRepo.SaveChangesAsync();
                await _sheetRepo.SaveChangesAsync();
            }

            return new MergeResult
            {
                Success = true,
                MatchedPointCount = matchedPairs.Count,
                InlierPointCount = offset.InlierCount,
                RejectedOutlierCount = offset.RejectedCount,
                RmsErrorMeters = offset.RmsError,
                DeltaX = offset.DeltaX,
                DeltaY = offset.DeltaY,
                Direction = direction,
                GridRelationship = gridRelationship,
                MatchedPoints = offset.Details,
                Message = linkedVia != null
                    ? $"Merged via tie-point X,Y match, confirmed by Laghu Reference chain ({linkedVia}: {baseSheet.SheetNumber} <-> {adjacentSheet.SheetNumber})."
                    : $"Merged via tie-point X,Y match ({baseSheet.SheetNumber} <-> {adjacentSheet.SheetNumber}).",
                FailureReason = null
            };
        }

        // Position of adjacentSheet relative to baseSheet, from the title-block
        // index-box N/S/E/W fields resolved by CadastralParsingService. Checked
        // both ways round since either sheet may be the one whose index box was
        // readable. Falls back to "Unknown" if neither sheet references the other.
        private static string DetermineDirection(SurveySheet baseSheet, SurveySheet adjacentSheet)
        {
            if (Eq(baseSheet.NeighborSheetNumberNorth, adjacentSheet.SheetNumber) ||
                Eq(adjacentSheet.NeighborSheetNumberSouth, baseSheet.SheetNumber))
                return "North";

            if (Eq(baseSheet.NeighborSheetNumberSouth, adjacentSheet.SheetNumber) ||
                Eq(adjacentSheet.NeighborSheetNumberNorth, baseSheet.SheetNumber))
                return "South";

            if (Eq(baseSheet.NeighborSheetNumberEast, adjacentSheet.SheetNumber) ||
                Eq(adjacentSheet.NeighborSheetNumberWest, baseSheet.SheetNumber))
                return "East";

            if (Eq(baseSheet.NeighborSheetNumberWest, adjacentSheet.SheetNumber) ||
                Eq(adjacentSheet.NeighborSheetNumberEast, baseSheet.SheetNumber))
                return "West";

            return "Unknown";

            static bool Eq(string? a, string? b) =>
                !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b) &&
                string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        // Row increases southward / col increases eastward, matching
        // SheetGridArrangementService's BFS convention.
        private static string DescribeGridRelationship(string direction) => direction switch
        {
            "North" => "North (ΔRow=-1, ΔCol=0)",
            "South" => "South (ΔRow=+1, ΔCol=0)",
            "East" => "East (ΔRow=0, ΔCol=+1)",
            "West" => "West (ΔRow=0, ΔCol=-1)",
            _ => "Unknown — no index-box neighbor link between these two sheets"
        };

        // Returns the matched reference number if either sheet's LaghuReferenceNumber
        // points to the other sheet's SheetNumber; null if no index link exists.
        // Informational only — no longer gates whether a merge is allowed.
        private static string? ResolveLaghuLink(SurveySheet baseSheet, SurveySheet adjacentSheet)
        {
            if (!string.IsNullOrWhiteSpace(adjacentSheet.LaghuReferenceNumber) &&
                adjacentSheet.LaghuReferenceNumber == baseSheet.SheetNumber)
                return adjacentSheet.LaghuReferenceNumber;

            if (!string.IsNullOrWhiteSpace(baseSheet.LaghuReferenceNumber) &&
                baseSheet.LaghuReferenceNumber == adjacentSheet.SheetNumber)
                return baseSheet.LaghuReferenceNumber;

            return null;
        }

        private static List<(TiePoint BasePoint, TiePoint AdjacentPoint)> FindMatchingPairs(
            List<TiePoint> basePoints, List<TiePoint> adjacentPoints)
        {
            var pairs = new List<(TiePoint, TiePoint)>();

            // 1) Primary: exact PointLabel matching
            foreach (var basePoint in basePoints)
            {
                if (string.IsNullOrWhiteSpace(basePoint.PointLabel))
                    continue;

                var match = adjacentPoints.FirstOrDefault(a => a.PointLabel == basePoint.PointLabel);
                if (match != null)
                    pairs.Add((basePoint, match));
            }

            // 2) Spatial fallback: if no labeled pairs matched, match by closest
            // centroid proximity using the spatial threshold. This ensures at least
            // some translation is computed even when PointLabels are missing or mismatched.
            if (pairs.Count == 0)
            {
                var labeledBase = basePoints.Where(p => !string.IsNullOrWhiteSpace(p.PointLabel)).ToList();
                var labeledAdjacent = adjacentPoints.Where(p => !string.IsNullOrWhiteSpace(p.PointLabel)).ToList();

                if (labeledBase.Any() && labeledAdjacent.Any())
                {
                    foreach (var basePoint in labeledBase)
                    {
                        var bestMatch = labeledAdjacent
                            .Select(a => new
                            {
                                Point = a,
                                Dist = Math.Sqrt(
                                    Math.Pow(a.SourceX - basePoint.SourceX, 2) +
                                    Math.Pow(a.SourceY - basePoint.SourceY, 2))
                            })
                            .Where(a => a.Dist <= SpatialThreshold)
                            .OrderBy(a => a.Dist)
                            .FirstOrDefault();

                        if (bestMatch != null)
                            pairs.Add((basePoint, bestMatch.Point));
                    }
                }
            }

            return pairs;
        }

        private readonly struct OffsetResult
        {
            public bool HaveOffset { get; init; }
            public double DeltaX { get; init; }
            public double DeltaY { get; init; }
            public double RmsError { get; init; }
            public int InlierCount { get; init; }
            public int RejectedCount { get; init; }
            public List<MatchedPointDetail> Details { get; init; }
        }

        // XY offset from every matched pair, with outlier rejection and RMS:
        //   1) Dx/Dy per pair (ΔX = base.X - adjacent.X, ΔY = base.Y - adjacent.Y).
        //   2) Median Dx/Dy across all pairs (robust to one or two bad labels).
        //   3) Reject any pair whose distance from the median exceeds
        //      OutlierResidualThreshold.
        //   4) Final ΔX/ΔY = mean of the surviving (inlier) pairs.
        //   5) RMS = root-mean-square distance of inlier pairs from that final mean.
        private static OffsetResult ComputeRobustOffset(
            List<(TiePoint BasePoint, TiePoint AdjacentPoint)> pairs)
        {
            var details = new List<MatchedPointDetail>();

            if (pairs.Count == 0)
                return new OffsetResult { HaveOffset = false, Details = details };

            var raw = pairs
                .Select(p => new
                {
                    p.BasePoint,
                    p.AdjacentPoint,
                    Dx = p.BasePoint.SourceX - p.AdjacentPoint.SourceX,
                    Dy = p.BasePoint.SourceY - p.AdjacentPoint.SourceY
                })
                .ToList();

            double medianDx = Median(raw.Select(r => r.Dx));
            double medianDy = Median(raw.Select(r => r.Dy));

            // With fewer than 3 pairs there isn't enough data to safely reject one
            // as "the outlier" — keep every pair as an inlier instead.
            bool canRejectOutliers = raw.Count >= 3;

            var inliers = new List<(double Dx, double Dy)>();

            foreach (var r in raw)
            {
                double residual = Math.Sqrt(
                    Math.Pow(r.Dx - medianDx, 2) + Math.Pow(r.Dy - medianDy, 2));

                bool isInlier = !canRejectOutliers || residual <= OutlierResidualThreshold;

                if (isInlier)
                    inliers.Add((r.Dx, r.Dy));

                details.Add(new MatchedPointDetail
                {
                    Label = r.BasePoint.PointLabel ?? r.AdjacentPoint.PointLabel,
                    BaseX = r.BasePoint.SourceX,
                    BaseY = r.BasePoint.SourceY,
                    AdjacentX = r.AdjacentPoint.SourceX,
                    AdjacentY = r.AdjacentPoint.SourceY,
                    Dx = r.Dx,
                    Dy = r.Dy,
                    IsInlier = isInlier
                });
            }

            // Should not happen given canRejectOutliers logic, but guard anyway —
            // never end up with zero usable pairs when at least one was matched.
            if (inliers.Count == 0)
            {
                inliers = raw.Select(r => (r.Dx, r.Dy)).ToList();
                foreach (var d in details) d.IsInlier = true;
            }

            double finalDx = inliers.Average(i => i.Dx);
            double finalDy = inliers.Average(i => i.Dy);

            double sumSquares = inliers.Sum(i =>
                Math.Pow(i.Dx - finalDx, 2) + Math.Pow(i.Dy - finalDy, 2));
            double rms = Math.Sqrt(sumSquares / inliers.Count);

            return new OffsetResult
            {
                HaveOffset = true,
                DeltaX = finalDx,
                DeltaY = finalDy,
                RmsError = rms,
                InlierCount = inliers.Count,
                RejectedCount = raw.Count - inliers.Count,
                Details = details
            };
        }

        private static double Median(IEnumerable<double> values)
        {
            var sorted = values.OrderBy(v => v).ToList();
            int n = sorted.Count;
            if (n == 0) return 0.0;
            return n % 2 == 1
                ? sorted[n / 2]
                : (sorted[n / 2 - 1] + sorted[n / 2]) / 2.0;
        }
    }
}