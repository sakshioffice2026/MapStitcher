// Services/CadastralMergeService.cs
//
// Pure XY-based cadastral sheet merging.
//
// Strategy:
//   1. Ignore LaghuReferenceNumber.
//   2. Ignore PointLabel.
//   3. Determine each sheet's coordinate extent from its TiePoints.
//   4. Identify points close to each sheet edge.
//   5. Compare opposing edges:
//        Base.Right  <-> Adjacent.Left
//        Base.Left   <-> Adjacent.Right
//        Base.Top    <-> Adjacent.Bottom
//        Base.Bottom <-> Adjacent.Top
//   6. Require sufficient overlap/alignment along the shared edge.
//   7. Calculate a translation that places the adjacent sheet beside
//      the base sheet in the same coordinate system.
//
// This is intentionally geometry-first because cadastral DWGs may not
// expose Marathi/Sakal text labels reliably.

using MapStitcher.Business.Contracts;
using MapStitcher.Database;
using MapStitcher.Repositories.Contracts;

namespace MapStitcher.Business.Services
{
    public class CadastralMergeService : ICadastralMergeService
    {
        // Distance from the calculated sheet boundary within which a
        // TiePoint is considered an edge point.
        private const double EdgeTolerance = 25.0;

        // Maximum allowed difference between normalized coordinates on
        // the shared edge.
        private const double AlignmentTolerance = 25.0;

        // Minimum number of edge points required to consider a geometric
        // relationship reliable.
        private const int MinimumEdgePoints = 2;

        // Minimum fraction of points that should overlap/alignment-match.
        private const double MinimumOverlapRatio = 0.50;

        private readonly ITiePointRepository _tiePointRepo;
        private readonly ISurveySheetRepository _sheetRepo;

        public CadastralMergeService(
            ITiePointRepository tiePointRepo,
            ISurveySheetRepository sheetRepo)
        {
            _tiePointRepo = tiePointRepo;
            _sheetRepo = sheetRepo;
        }

        public async Task<MergeResult> MergeSheetsAsync(
            int baseSheetId,
            int adjacentSheetId)
        {
            var baseSheet = await _sheetRepo.GetByIdAsync(baseSheetId)
                ?? throw new InvalidOperationException(
                    $"Base sheet {baseSheetId} not found.");

            var adjacentSheet = await _sheetRepo.GetByIdAsync(adjacentSheetId)
                ?? throw new InvalidOperationException(
                    $"Adjacent sheet {adjacentSheetId} not found.");

            if (baseSheetId == adjacentSheetId)
            {
                return Failure(
                    "Cannot merge a sheet with itself.",
                    "Base and adjacent sheets must be different.");
            }

            var basePoints = await _tiePointRepo.GetBySheetIdAsync(baseSheetId);
            var adjacentPoints =
                await _tiePointRepo.GetBySheetIdAsync(adjacentSheetId);

            if (basePoints.Count == 0 || adjacentPoints.Count == 0)
            {
                return Failure(
                    "One or both sheets contain no coordinate tie points.",
                    "Not enough coordinate data to identify the sheet relationship.");
            }

            var baseExtent = CalculateExtent(basePoints);
            var adjacentExtent = CalculateExtent(adjacentPoints);

            var relationship = FindBestEdgeRelationship(
                basePoints,
                adjacentPoints,
                baseExtent,
                adjacentExtent);

            if (relationship == null)
            {
                return Failure(
                    $"No XY edge relationship found between " +
                    $"'{baseSheet.SheetNumber}' and '{adjacentSheet.SheetNumber}'.",
                    "Sheets do not have enough matching edge coordinates to merge.");
            }

            var translateX = relationship.TranslateX;
            var translateY = relationship.TranslateY;

            // Apply the calculated transform to every point on the
            // adjacent sheet.
            foreach (var point in adjacentPoints)
            {
                point.TargetX = point.SourceX + translateX;
                point.TargetY = point.SourceY + translateY;
            }

            // This merge is translation-only. No rotation or scale is
            // introduced by the XY cadastral sheet strategy.
            adjacentSheet.TransformRotation = 0.0;
            adjacentSheet.TransformScale = 1.0;
            adjacentSheet.TransformTranslateX = translateX;
            adjacentSheet.TransformTranslateY = translateY;

            adjacentSheet.Status = SheetStatus.Merged;
            baseSheet.Status = SheetStatus.Merged;

            await _tiePointRepo.SaveChangesAsync();
            await _sheetRepo.SaveChangesAsync();

            return new MergeResult
            {
                Success = true,

                // These are actual geometric edge matches, not text-label
                // matches.
                MatchedPointCount = relationship.MatchedPointCount,
                InlierPointCount = relationship.MatchedPointCount,
                RejectedOutlierCount =
                    Math.Max(
                        0,
                        relationship.BaseEdgePointCount -
                        relationship.MatchedPointCount),

                RmsErrorMeters = relationship.RmsError,

                Message =
                    $"Merged using pure XY edge matching " +
                    $"({relationship.Direction}). " +
                    $"Translation: X={translateX:F3}, Y={translateY:F3}. " +
                    $"Matched {relationship.MatchedPointCount} edge points.",

                FailureReason = null
            };
        }

        // -----------------------------------------------------------------
        // EDGE RELATIONSHIP DETECTION
        // -----------------------------------------------------------------

        private static EdgeRelationship? FindBestEdgeRelationship(
            List<TiePoint> basePoints,
            List<TiePoint> adjacentPoints,
            SheetExtent baseExtent,
            SheetExtent adjacentExtent)
        {
            var candidates = new List<EdgeRelationship>();

            // Base right -> adjacent left.
            //
            // Example:
            //
            // Base:     X = 0 ... 1000
            // Adjacent: X = 0 ... 1000
            //
            // Result:
            // Adjacent X=0 moves to Base MaxX.
            candidates.Add(
                EvaluateHorizontalRelationship(
                    basePoints,
                    adjacentPoints,
                    baseExtent,
                    adjacentExtent,
                    baseRight: true));

            // Base left -> adjacent right.
            candidates.Add(
                EvaluateHorizontalRelationship(
                    basePoints,
                    adjacentPoints,
                    baseExtent,
                    adjacentExtent,
                    baseRight: false));

            // Base top -> adjacent bottom.
            candidates.Add(
                EvaluateVerticalRelationship(
                    basePoints,
                    adjacentPoints,
                    baseExtent,
                    adjacentExtent,
                    baseTop: true));

            // Base bottom -> adjacent top.
            candidates.Add(
                EvaluateVerticalRelationship(
                    basePoints,
                    adjacentPoints,
                    baseExtent,
                    adjacentExtent,
                    baseTop: false));

            return candidates
                .Where(c => c != null)
                .OrderByDescending(c => c!.Score)
                .FirstOrDefault();
        }

        // -----------------------------------------------------------------
        // HORIZONTAL RELATIONSHIPS
        // -----------------------------------------------------------------

        private static EdgeRelationship? EvaluateHorizontalRelationship(
            List<TiePoint> basePoints,
            List<TiePoint> adjacentPoints,
            SheetExtent baseExtent,
            SheetExtent adjacentExtent,
            bool baseRight)
        {
            var baseEdge = baseRight
                ? GetRightEdgePoints(basePoints, baseExtent)
                : GetLeftEdgePoints(basePoints, baseExtent);

            var adjacentEdge = baseRight
                ? GetLeftEdgePoints(adjacentPoints, adjacentExtent)
                : GetRightEdgePoints(adjacentPoints, adjacentExtent);

            if (baseEdge.Count < MinimumEdgePoints ||
                adjacentEdge.Count < MinimumEdgePoints)
            {
                return null;
            }

            var matches = MatchByCoordinate(
                baseEdge,
                adjacentEdge,
                useX: false);

            if (matches.Count < MinimumEdgePoints)
                return null;

            var baseRange = GetRange(baseEdge.Select(p => p.SourceY));
            var adjacentRange = GetRange(
                adjacentEdge.Select(p => p.SourceY));

            var overlap = CalculateRangeOverlap(
                baseRange.Min,
                baseRange.Max,
                adjacentRange.Min,
                adjacentRange.Max);

            var smallerRange = Math.Min(
                baseRange.Max - baseRange.Min,
                adjacentRange.Max - adjacentRange.Min);

            // If the edge has effectively no length, fall back to
            // point-count matching.
            var overlapRatio = smallerRange <= 0
                ? 1.0
                : overlap / smallerRange;

            if (overlapRatio < MinimumOverlapRatio)
                return null;

            // Translation is determined from the opposing sheet edges.
            //
            // Base.Right + Adjacent.Left:
            //     targetX = adjacentX + (Base.MaxX - Adjacent.MinX)
            //
            // Base.Left + Adjacent.Right:
            //     targetX = adjacentX + (Base.MinX - Adjacent.MaxX)
            //
            var translateX = baseRight
                ? baseExtent.MaxX - adjacentExtent.MinX
                : baseExtent.MinX - adjacentExtent.MaxX;

            // Y is determined from the matched edge points rather than
            // relying on labels.
            var translateY = matches.Average(
                pair => pair.Base.SourceY - pair.Adjacent.SourceY);

            var rms = CalculateRms(
                matches,
                translateX,
                translateY);

            return new EdgeRelationship
            {
                Direction = baseRight
                    ? "Base.Right -> Adjacent.Left"
                    : "Base.Left -> Adjacent.Right",

                TranslateX = translateX,
                TranslateY = translateY,

                MatchedPointCount = matches.Count,
                BaseEdgePointCount = baseEdge.Count,

                RmsError = rms,

                Score =
                    (matches.Count * 100.0) +
                    (overlapRatio * 100.0) -
                    rms
            };
        }

        // -----------------------------------------------------------------
        // VERTICAL RELATIONSHIPS
        // -----------------------------------------------------------------

        private static EdgeRelationship? EvaluateVerticalRelationship(
            List<TiePoint> basePoints,
            List<TiePoint> adjacentPoints,
            SheetExtent baseExtent,
            SheetExtent adjacentExtent,
            bool baseTop)
        {
            var baseEdge = baseTop
                ? GetTopEdgePoints(basePoints, baseExtent)
                : GetBottomEdgePoints(basePoints, baseExtent);

            var adjacentEdge = baseTop
                ? GetBottomEdgePoints(adjacentPoints, adjacentExtent)
                : GetTopEdgePoints(adjacentPoints, adjacentExtent);

            if (baseEdge.Count < MinimumEdgePoints ||
                adjacentEdge.Count < MinimumEdgePoints)
            {
                return null;
            }

            var matches = MatchByCoordinate(
                baseEdge,
                adjacentEdge,
                useX: true);

            if (matches.Count < MinimumEdgePoints)
                return null;

            var baseRange = GetRange(baseEdge.Select(p => p.SourceX));
            var adjacentRange = GetRange(
                adjacentEdge.Select(p => p.SourceX));

            var overlap = CalculateRangeOverlap(
                baseRange.Min,
                baseRange.Max,
                adjacentRange.Min,
                adjacentRange.Max);

            var smallerRange = Math.Min(
                baseRange.Max - baseRange.Min,
                adjacentRange.Max - adjacentRange.Min);

            var overlapRatio = smallerRange <= 0
                ? 1.0
                : overlap / smallerRange;

            if (overlapRatio < MinimumOverlapRatio)
                return null;

            var translateY = baseTop
                ? baseExtent.MaxY - adjacentExtent.MinY
                : baseExtent.MinY - adjacentExtent.MaxY;

            var translateX = matches.Average(
                pair => pair.Base.SourceX - pair.Adjacent.SourceX);

            var rms = CalculateRms(
                matches,
                translateX,
                translateY);

            return new EdgeRelationship
            {
                Direction = baseTop
                    ? "Base.Top -> Adjacent.Bottom"
                    : "Base.Bottom -> Adjacent.Top",

                TranslateX = translateX,
                TranslateY = translateY,

                MatchedPointCount = matches.Count,
                BaseEdgePointCount = baseEdge.Count,

                RmsError = rms,

                Score =
                    (matches.Count * 100.0) +
                    (overlapRatio * 100.0) -
                    rms
            };
        }

        // -----------------------------------------------------------------
        // EDGE DETECTION
        // -----------------------------------------------------------------

        private static List<TiePoint> GetLeftEdgePoints(
            List<TiePoint> points,
            SheetExtent extent)
        {
            return points
                .Where(p =>
                    Math.Abs(p.SourceX - extent.MinX) <= EdgeTolerance)
                .ToList();
        }

        private static List<TiePoint> GetRightEdgePoints(
            List<TiePoint> points,
            SheetExtent extent)
        {
            return points
                .Where(p =>
                    Math.Abs(p.SourceX - extent.MaxX) <= EdgeTolerance)
                .ToList();
        }

        private static List<TiePoint> GetBottomEdgePoints(
            List<TiePoint> points,
            SheetExtent extent)
        {
            return points
                .Where(p =>
                    Math.Abs(p.SourceY - extent.MinY) <= EdgeTolerance)
                .ToList();
        }

        private static List<TiePoint> GetTopEdgePoints(
            List<TiePoint> points,
            SheetExtent extent)
        {
            return points
                .Where(p =>
                    Math.Abs(p.SourceY - extent.MaxY) <= EdgeTolerance)
                .ToList();
        }

        // -----------------------------------------------------------------
        // COORDINATE MATCHING
        // -----------------------------------------------------------------

        private static List<PointPair> MatchByCoordinate(
            List<TiePoint> basePoints,
            List<TiePoint> adjacentPoints,
            bool useX)
        {
            var matches = new List<PointPair>();

            // Sort by the coordinate running along the shared edge.
            var orderedBase = useX
                ? basePoints.OrderBy(p => p.SourceX).ToList()
                : basePoints.OrderBy(p => p.SourceY).ToList();

            var orderedAdjacent = useX
                ? adjacentPoints.OrderBy(p => p.SourceX).ToList()
                : adjacentPoints.OrderBy(p => p.SourceY).ToList();

            var usedAdjacent = new HashSet<int>();

            foreach (var basePoint in orderedBase)
            {
                var baseCoordinate = useX
                    ? basePoint.SourceX
                    : basePoint.SourceY;

                TiePoint? best = null;
                double bestDifference = double.MaxValue;

                foreach (var adjacentPoint in orderedAdjacent)
                {
                    if (usedAdjacent.Contains(adjacentPoint.PointID))
                        continue;

                    var adjacentCoordinate = useX
                        ? adjacentPoint.SourceX
                        : adjacentPoint.SourceY;

                    var difference =
                        Math.Abs(baseCoordinate - adjacentCoordinate);

                    if (difference <= AlignmentTolerance &&
                        difference < bestDifference)
                    {
                        best = adjacentPoint;
                        bestDifference = difference;
                    }
                }

                if (best != null)
                {
                    matches.Add(
                        new PointPair
                        {
                            Base = basePoint,
                            Adjacent = best
                        });

                    usedAdjacent.Add(best.PointID);
                }
            }

            return matches;
        }

        // -----------------------------------------------------------------
        // EXTENT
        // -----------------------------------------------------------------

        private static SheetExtent CalculateExtent(
            List<TiePoint> points)
        {
            return new SheetExtent
            {
                MinX = points.Min(p => p.SourceX),
                MaxX = points.Max(p => p.SourceX),
                MinY = points.Min(p => p.SourceY),
                MaxY = points.Max(p => p.SourceY)
            };
        }

        // -----------------------------------------------------------------
        // ERROR / OVERLAP
        // -----------------------------------------------------------------

        private static double CalculateRms(
            List<PointPair> matches,
            double translateX,
            double translateY)
        {
            if (matches.Count == 0)
                return double.MaxValue;

            var squaredErrors = matches.Select(pair =>
            {
                var dx =
                    pair.Base.SourceX -
                    (pair.Adjacent.SourceX + translateX);

                var dy =
                    pair.Base.SourceY -
                    (pair.Adjacent.SourceY + translateY);

                return (dx * dx) + (dy * dy);
            });

            return Math.Sqrt(
                squaredErrors.Average());
        }

        private static double CalculateRangeOverlap(
            double minA,
            double maxA,
            double minB,
            double maxB)
        {
            var overlapMin = Math.Max(minA, minB);
            var overlapMax = Math.Min(maxA, maxB);

            return Math.Max(
                0,
                overlapMax - overlapMin);
        }

        private static (double Min, double Max) GetRange(
            IEnumerable<double> values)
        {
            var list = values.ToList();

            return (
                list.Min(),
                list.Max()
            );
        }

        // -----------------------------------------------------------------
        // RESULT HELPERS
        // -----------------------------------------------------------------

        private static MergeResult Failure(
            string technicalMessage,
            string userMessage)
        {
            return new MergeResult
            {
                Success = false,
                MatchedPointCount = 0,
                InlierPointCount = 0,
                RejectedOutlierCount = 0,
                RmsErrorMeters = 0,
                Message = technicalMessage,
                FailureReason = userMessage
            };
        }

        // -----------------------------------------------------------------
        // INTERNAL TYPES
        // -----------------------------------------------------------------

        private sealed class SheetExtent
        {
            public double MinX { get; init; }
            public double MaxX { get; init; }
            public double MinY { get; init; }
            public double MaxY { get; init; }
        }

        private sealed class PointPair
        {
            public TiePoint Base { get; init; } = null!;
            public TiePoint Adjacent { get; init; } = null!;
        }

        private sealed class EdgeRelationship
        {
            public string Direction { get; init; } = string.Empty;

            public double TranslateX { get; init; }
            public double TranslateY { get; init; }

            public int MatchedPointCount { get; init; }
            public int BaseEdgePointCount { get; init; }

            public double RmsError { get; init; }

            public double Score { get; init; }
        }
    }
}

