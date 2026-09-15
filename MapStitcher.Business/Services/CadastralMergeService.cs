using MapStitcher.Business.Contracts;
using MapStitcher.Database;
using MapStitcher.Repositories.Contracts;

namespace MapStitcher.Business.Services
{
    public class CadastralMergeService : ICadastralMergeService
    {
        private const double EdgeTolerance = 25.0;
        private const double AlignmentTolerance = 25.0;
        private const int MinimumEdgePoints = 2;
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
            var baseSheet =
                await _sheetRepo.GetByIdAsync(baseSheetId)
                ?? throw new InvalidOperationException(
                    $"Base sheet {baseSheetId} not found.");

            var adjacentSheet =
                await _sheetRepo.GetByIdAsync(adjacentSheetId)
                ?? throw new InvalidOperationException(
                    $"Adjacent sheet {adjacentSheetId} not found.");

            if (baseSheetId == adjacentSheetId)
            {
                return Failure(
                    "Cannot merge a sheet with itself.",
                    "Base and adjacent sheets must be different.");
            }

            var basePoints =
                await _tiePointRepo.GetBySheetIdAsync(
                    baseSheetId);

            var adjacentPoints =
                await _tiePointRepo.GetBySheetIdAsync(
                    adjacentSheetId);

            if (basePoints.Count == 0 ||
                adjacentPoints.Count == 0)
            {
                return Failure(
                    "One or both sheets contain no geometry coordinates.",
                    "Not enough geometry data to merge the sheets.");
            }

            var baseExtent =
                CalculateExtent(basePoints);

            var adjacentExtent =
                CalculateExtent(adjacentPoints);

            /*
             * All points are already normalized to each sheet's local
             * origin by CadastralParsingService.
             *
             * Therefore every sheet starts at:
             *
             *     MinX = 0
             *     MinY = 0
             *
             * Width and height describe the actual local sheet size.
             */
            var relationship =
                FindBestEdgeRelationship(
                    basePoints,
                    adjacentPoints,
                    baseExtent,
                    adjacentExtent);

            if (relationship == null)
            {
                return Failure(
                    $"No XY edge relationship found between " +
                    $"'{baseSheet.SheetNumber}' and " +
                    $"'{adjacentSheet.SheetNumber}'.",
                    "Sheets do not have enough matching geometry along a shared edge.");
            }

            /*
             * relationship.TranslateX/Y is local movement from the base
             * sheet's coordinate system.
             *
             * The final transform must include the base sheet's existing
             * global transform.
             *
             * A -> B -> C:
             *
             * A = 0
             * B = A + localB
             * C = B + localC
             *
             * This prevents chain merges from losing previous transforms.
             */
            var globalTranslateX =
                baseSheet.TransformTranslateX +
                relationship.TranslateX;

            var globalTranslateY =
                baseSheet.TransformTranslateY +
                relationship.TranslateY;

            foreach (var point in adjacentPoints)
            {
                point.TargetX =
                    point.SourceX +
                    globalTranslateX;

                point.TargetY =
                    point.SourceY +
                    globalTranslateY;
            }

            adjacentSheet.TransformRotation = 0.0;
            adjacentSheet.TransformScale = 1.0;

            adjacentSheet.TransformTranslateX =
                globalTranslateX;

            adjacentSheet.TransformTranslateY =
                globalTranslateY;

            adjacentSheet.Status =
                SheetStatus.Merged;

            baseSheet.Status =
                SheetStatus.Merged;

            await _tiePointRepo.SaveChangesAsync();
            await _sheetRepo.SaveChangesAsync();

            return new MergeResult
            {
                Success = true,

                MatchedPointCount =
                    relationship.MatchedPointCount,

                InlierPointCount =
                    relationship.MatchedPointCount,

                RejectedOutlierCount =
                    Math.Max(
                        0,
                        relationship.BaseEdgePointCount -
                        relationship.MatchedPointCount),

                RmsErrorMeters =
                    relationship.RmsError,

                Message =
                    $"Merged using normalized XY geometry " +
                    $"({relationship.Direction}). " +
                    $"Global translation: " +
                    $"X={globalTranslateX:F3}, " +
                    $"Y={globalTranslateY:F3}. " +
                    $"Matched {relationship.MatchedPointCount} edge points.",

                FailureReason = null
            };
        }

        private static EdgeRelationship? FindBestEdgeRelationship(
            List<TiePoint> basePoints,
            List<TiePoint> adjacentPoints,
            SheetExtent baseExtent,
            SheetExtent adjacentExtent)
        {
            var candidates =
                new List<EdgeRelationship?>();

            candidates.Add(
                EvaluateHorizontalRelationship(
                    basePoints,
                    adjacentPoints,
                    baseExtent,
                    adjacentExtent,
                    true));

            candidates.Add(
                EvaluateHorizontalRelationship(
                    basePoints,
                    adjacentPoints,
                    baseExtent,
                    adjacentExtent,
                    false));

            candidates.Add(
                EvaluateVerticalRelationship(
                    basePoints,
                    adjacentPoints,
                    baseExtent,
                    adjacentExtent,
                    true));

            candidates.Add(
                EvaluateVerticalRelationship(
                    basePoints,
                    adjacentPoints,
                    baseExtent,
                    adjacentExtent,
                    false));

            return candidates
                .Where(c => c != null)
                .OrderByDescending(c => c!.Score)
                .ThenBy(c => c!.RmsError)
                .FirstOrDefault();
        }

        private static EdgeRelationship? EvaluateHorizontalRelationship(
            List<TiePoint> basePoints,
            List<TiePoint> adjacentPoints,
            SheetExtent baseExtent,
            SheetExtent adjacentExtent,
            bool baseRight)
        {
            var baseEdge =
                baseRight
                    ? GetRightEdgePoints(
                        basePoints,
                        baseExtent)
                    : GetLeftEdgePoints(
                        basePoints,
                        baseExtent);

            var adjacentEdge =
                baseRight
                    ? GetLeftEdgePoints(
                        adjacentPoints,
                        adjacentExtent)
                    : GetRightEdgePoints(
                        adjacentPoints,
                        adjacentExtent);

            if (baseEdge.Count < MinimumEdgePoints ||
                adjacentEdge.Count < MinimumEdgePoints)
            {
                return null;
            }

            var matches =
                MatchByCoordinate(
                    baseEdge,
                    adjacentEdge,
                    false);

            if (matches.Count < MinimumEdgePoints)
                return null;

            var baseRange =
                GetRange(
                    baseEdge.Select(
                        p => p.SourceY));

            var adjacentRange =
                GetRange(
                    adjacentEdge.Select(
                        p => p.SourceY));

            var overlap =
                CalculateRangeOverlap(
                    baseRange.Min,
                    baseRange.Max,
                    adjacentRange.Min,
                    adjacentRange.Max);

            var smallerRange =
                Math.Min(
                    baseRange.Max - baseRange.Min,
                    adjacentRange.Max - adjacentRange.Min);

            var overlapRatio =
                smallerRange <= 0
                    ? 1.0
                    : overlap / smallerRange;

            if (overlapRatio <
                MinimumOverlapRatio)
            {
                return null;
            }

            /*
             * Because both sheets are normalized to local (0,0):
             *
             * Base.Right -> Adjacent.Left
             *
             * adjacent local X = 0
             * base local X = base width
             *
             * Therefore:
             *
             * adjacent global X =
             *     base global X + base width
             */
            var translateX =
                baseRight
                    ? baseExtent.MaxX -
                      adjacentExtent.MinX
                    : baseExtent.MinX -
                      adjacentExtent.MaxX;

            var translateY =
                CalculateBestAxisTranslation(
                    matches,
                    useX: false);

            var rms =
                CalculateRms(
                    matches,
                    translateX,
                    translateY);

            return new EdgeRelationship
            {
                Direction =
                    baseRight
                        ? "Base.Right -> Adjacent.Left"
                        : "Base.Left -> Adjacent.Right",

                TranslateX = translateX,
                TranslateY = translateY,

                MatchedPointCount =
                    matches.Count,

                BaseEdgePointCount =
                    baseEdge.Count,

                RmsError = rms,

                Score =
                    matches.Count * 100.0 +
                    overlapRatio * 100.0 -
                    rms
            };
        }

        private static EdgeRelationship? EvaluateVerticalRelationship(
            List<TiePoint> basePoints,
            List<TiePoint> adjacentPoints,
            SheetExtent baseExtent,
            SheetExtent adjacentExtent,
            bool baseTop)
        {
            var baseEdge =
                baseTop
                    ? GetTopEdgePoints(
                        basePoints,
                        baseExtent)
                    : GetBottomEdgePoints(
                        basePoints,
                        baseExtent);

            var adjacentEdge =
                baseTop
                    ? GetBottomEdgePoints(
                        adjacentPoints,
                        adjacentExtent)
                    : GetTopEdgePoints(
                        adjacentPoints,
                        adjacentExtent);

            if (baseEdge.Count < MinimumEdgePoints ||
                adjacentEdge.Count < MinimumEdgePoints)
            {
                return null;
            }

            var matches =
                MatchByCoordinate(
                    baseEdge,
                    adjacentEdge,
                    true);

            if (matches.Count < MinimumEdgePoints)
                return null;

            var baseRange =
                GetRange(
                    baseEdge.Select(
                        p => p.SourceX));

            var adjacentRange =
                GetRange(
                    adjacentEdge.Select(
                        p => p.SourceX));

            var overlap =
                CalculateRangeOverlap(
                    baseRange.Min,
                    baseRange.Max,
                    adjacentRange.Min,
                    adjacentRange.Max);

            var smallerRange =
                Math.Min(
                    baseRange.Max - baseRange.Min,
                    adjacentRange.Max - adjacentRange.Min);

            var overlapRatio =
                smallerRange <= 0
                    ? 1.0
                    : overlap / smallerRange;

            if (overlapRatio <
                MinimumOverlapRatio)
            {
                return null;
            }

            var translateY =
                baseTop
                    ? baseExtent.MaxY -
                      adjacentExtent.MinY
                    : baseExtent.MinY -
                      adjacentExtent.MaxY;

            var translateX =
                CalculateBestAxisTranslation(
                    matches,
                    useX: true);

            var rms =
                CalculateRms(
                    matches,
                    translateX,
                    translateY);

            return new EdgeRelationship
            {
                Direction =
                    baseTop
                        ? "Base.Top -> Adjacent.Bottom"
                        : "Base.Bottom -> Adjacent.Top",

                TranslateX = translateX,
                TranslateY = translateY,

                MatchedPointCount =
                    matches.Count,

                BaseEdgePointCount =
                    baseEdge.Count,

                RmsError = rms,

                Score =
                    matches.Count * 100.0 +
                    overlapRatio * 100.0 -
                    rms
            };
        }

        private static double CalculateBestAxisTranslation(
            List<PointPair> matches,
            bool useX)
        {
            if (matches.Count == 0)
                return 0.0;

            var differences =
                matches.Select(pair =>
                {
                    return useX
                        ? pair.Base.SourceX -
                          pair.Adjacent.SourceX
                        : pair.Base.SourceY -
                          pair.Adjacent.SourceY;
                })
                .OrderBy(v => v)
                .ToList();

            var middle =
                differences.Count / 2;

            if (differences.Count % 2 == 1)
                return differences[middle];

            return (
                differences[middle - 1] +
                differences[middle]) / 2.0;
        }

        private static List<TiePoint> GetLeftEdgePoints(
            List<TiePoint> points,
            SheetExtent extent)
        {
            return points
                .Where(
                    p =>
                        Math.Abs(
                            p.SourceX -
                            extent.MinX) <= EdgeTolerance)
                .ToList();
        }

        private static List<TiePoint> GetRightEdgePoints(
            List<TiePoint> points,
            SheetExtent extent)
        {
            return points
                .Where(
                    p =>
                        Math.Abs(
                            p.SourceX -
                            extent.MaxX) <= EdgeTolerance)
                .ToList();
        }

        private static List<TiePoint> GetBottomEdgePoints(
            List<TiePoint> points,
            SheetExtent extent)
        {
            return points
                .Where(
                    p =>
                        Math.Abs(
                            p.SourceY -
                            extent.MinY) <= EdgeTolerance)
                .ToList();
        }

        private static List<TiePoint> GetTopEdgePoints(
            List<TiePoint> points,
            SheetExtent extent)
        {
            return points
                .Where(
                    p =>
                        Math.Abs(
                            p.SourceY -
                            extent.MaxY) <= EdgeTolerance)
                .ToList();
        }

        private static List<PointPair> MatchByCoordinate(
            List<TiePoint> basePoints,
            List<TiePoint> adjacentPoints,
            bool useX)
        {
            var matches =
                new List<PointPair>();

            var orderedBase =
                useX
                    ? basePoints
                        .OrderBy(p => p.SourceX)
                        .ToList()
                    : basePoints
                        .OrderBy(p => p.SourceY)
                        .ToList();

            var orderedAdjacent =
                useX
                    ? adjacentPoints
                        .OrderBy(p => p.SourceX)
                        .ToList()
                    : adjacentPoints
                        .OrderBy(p => p.SourceY)
                        .ToList();

            var usedAdjacent =
                new HashSet<int>();

            foreach (var basePoint in orderedBase)
            {
                var baseCoordinate =
                    useX
                        ? basePoint.SourceX
                        : basePoint.SourceY;

                TiePoint? best = null;
                double bestDifference =
                    double.MaxValue;

                foreach (var adjacentPoint
                    in orderedAdjacent)
                {
                    if (usedAdjacent.Contains(
                            adjacentPoint.PointID))
                    {
                        continue;
                    }

                    var adjacentCoordinate =
                        useX
                            ? adjacentPoint.SourceX
                            : adjacentPoint.SourceY;

                    var difference =
                        Math.Abs(
                            baseCoordinate -
                            adjacentCoordinate);

                    if (difference <=
                            AlignmentTolerance &&
                        difference <
                            bestDifference)
                    {
                        best =
                            adjacentPoint;

                        bestDifference =
                            difference;
                    }
                }

                if (best == null)
                    continue;

                matches.Add(
                    new PointPair
                    {
                        Base = basePoint,
                        Adjacent = best
                    });

                usedAdjacent.Add(
                    best.PointID);
            }

            return matches;
        }

        private static SheetExtent CalculateExtent(
            List<TiePoint> points)
        {
            var minX =
                points.Min(p => p.SourceX);

            var maxX =
                points.Max(p => p.SourceX);

            var minY =
                points.Min(p => p.SourceY);

            var maxY =
                points.Max(p => p.SourceY);

            return new SheetExtent
            {
                MinX = minX,
                MaxX = maxX,
                MinY = minY,
                MaxY = maxY
            };
        }

        private static double CalculateRms(
            List<PointPair> matches,
            double translateX,
            double translateY)
        {
            if (matches.Count == 0)
                return double.MaxValue;

            var squaredErrors =
                matches.Select(pair =>
                {
                    var dx =
                        pair.Base.SourceX -
                        (
                            pair.Adjacent.SourceX +
                            translateX
                        );

                    var dy =
                        pair.Base.SourceY -
                        (
                            pair.Adjacent.SourceY +
                            translateY
                        );

                    return dx * dx + dy * dy;
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
            var overlapMin =
                Math.Max(minA, minB);

            var overlapMax =
                Math.Min(maxA, maxB);

            return Math.Max(
                0,
                overlapMax - overlapMin);
        }

        private static (double Min, double Max) GetRange(
            IEnumerable<double> values)
        {
            var list =
                values.ToList();

            return (
                list.Min(),
                list.Max()
            );
        }

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

        private sealed class SheetExtent
        {
            public double MinX { get; init; }
            public double MaxX { get; init; }
            public double MinY { get; init; }
            public double MaxY { get; init; }

            public double Width =>
                MaxX - MinX;

            public double Height =>
                MaxY - MinY;
        }

        private sealed class PointPair
        {
            public TiePoint Base { get; init; } = null!;
            public TiePoint Adjacent { get; init; } = null!;
        }

        private sealed class EdgeRelationship
        {
            public string Direction { get; init; } =
                string.Empty;

            public double TranslateX { get; init; }
            public double TranslateY { get; init; }

            public int MatchedPointCount { get; init; }

            public int BaseEdgePointCount { get; init; }

            public double RmsError { get; init; }

            public double Score { get; init; }
        }
    }
}