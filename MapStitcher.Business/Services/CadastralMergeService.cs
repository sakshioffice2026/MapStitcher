// Services/CadastralMergeService.cs
using MapStitcher.Business.Contracts;
using MapStitcher.Database;
using MapStitcher.Repositories.Contracts;

namespace MapStitcher.Business.Services
{
    // Cadastral merge strategy:
    // - No Laghu Reference dependency.
    // - No PointLabel dependency.
    // - Detect the shared sheet edge from XY bounds.
    // - Match boundary tie points by their perpendicular coordinate.
    // - Compute translation from the opposing edge bounds and the matched
    //   perpendicular-coordinate offset.
    public class CadastralMergeService : ICadastralMergeService
    {
        private const double EdgeToleranceFraction = 0.05;
        private const double PointMatchThreshold = 5.0;
        private const int MinOverlapPoints = 2;

        private readonly ITiePointRepository _tiePointRepo;
        private readonly ISurveySheetRepository _sheetRepo;

        public CadastralMergeService(
            ITiePointRepository tiePointRepo,
            ISurveySheetRepository sheetRepo)
        {
            _tiePointRepo = tiePointRepo;
            _sheetRepo = sheetRepo;
        }

        public async Task<MergeResult> MergeSheetsAsync(int baseSheetId, int adjacentSheetId)
        {
            var baseSheet = await _sheetRepo.GetByIdAsync(baseSheetId)
                ?? throw new InvalidOperationException($"Base sheet {baseSheetId} not found.");

            var adjacentSheet = await _sheetRepo.GetByIdAsync(adjacentSheetId)
                ?? throw new InvalidOperationException($"Adjacent sheet {adjacentSheetId} not found.");

            var basePoints = await _tiePointRepo.GetBySheetIdAsync(baseSheetId);
            var adjacentPoints = await _tiePointRepo.GetBySheetIdAsync(adjacentSheetId);

            if (basePoints.Count == 0 || adjacentPoints.Count == 0)
            {
                return new MergeResult
                {
                    Success = false,
                    MatchedPointCount = 0,
                    InlierPointCount = 0,
                    RejectedOutlierCount = 0,
                    Message = "Cannot merge: both sheets must contain tie points.",
                    FailureReason = "Insufficient tie points for XY edge matching"
                };
            }

            var baseBounds = ComputeBounds(basePoints);
            var adjacentBounds = ComputeBounds(adjacentPoints);
            var relationship = FindBestEdgeRelationship(basePoints, adjacentPoints, baseBounds, adjacentBounds);

            if (relationship == null)
            {
                return new MergeResult
                {
                    Success = false,
                    MatchedPointCount = 0,
                    InlierPointCount = 0,
                    RejectedOutlierCount = 0,
                    Message = $"No XY edge relationship found between {baseSheet.SheetNumber} and {adjacentSheet.SheetNumber}.",
                    FailureReason = "Needs manual check: no matching boundary edge tie points"
                };
            }

            var translateX = relationship.TranslateX;
            var translateY = relationship.TranslateY;

            foreach (var point in adjacentPoints)
            {
                point.TargetX = point.SourceX + translateX;
                point.TargetY = point.SourceY + translateY;
            }

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
                RmsErrorMeters = relationship.RmsError,
                MatchedPointCount = relationship.MatchedPointCount,
                InlierPointCount = relationship.MatchedPointCount,
                RejectedOutlierCount = 0,
                Message = $"Merged via XY {relationship.Direction} edge geometry ({baseSheet.SheetNumber} <-> {adjacentSheet.SheetNumber}).",
                FailureReason = null
            };
        }

        private static Bounds ComputeBounds(List<TiePoint> points)
        {
            return new Bounds(
                points.Min(p => p.SourceX),
                points.Max(p => p.SourceX),
                points.Min(p => p.SourceY),
                points.Max(p => p.SourceY));
        }

        private static List<TiePoint> GetEdgePoints(
            List<TiePoint> points,
            Bounds bounds,
            Edge edge)
        {
            var span = edge is Edge.Left or Edge.Right
                ? bounds.MaxX - bounds.MinX
                : bounds.MaxY - bounds.MinY;

            var tolerance = Math.Max(span * EdgeToleranceFraction, PointMatchThreshold);

            return edge switch
            {
                Edge.Left => points.Where(p => Math.Abs(p.SourceX - bounds.MinX) <= tolerance).ToList(),
                Edge.Right => points.Where(p => Math.Abs(p.SourceX - bounds.MaxX) <= tolerance).ToList(),
                Edge.Bottom => points.Where(p => Math.Abs(p.SourceY - bounds.MinY) <= tolerance).ToList(),
                Edge.Top => points.Where(p => Math.Abs(p.SourceY - bounds.MaxY) <= tolerance).ToList(),
                _ => new List<TiePoint>()
            };
        }

        private static EdgeRelationship? FindBestEdgeRelationship(
            List<TiePoint> basePoints,
            List<TiePoint> adjacentPoints,
            Bounds baseBounds,
            Bounds adjacentBounds)
        {
            var candidates = new List<EdgeRelationship?>
            {
                EvaluateHorizontal(
                    GetEdgePoints(basePoints, baseBounds, Edge.Right),
                    GetEdgePoints(adjacentPoints, adjacentBounds, Edge.Left),
                    baseBounds.MaxX - adjacentBounds.MinX,
                    "right-left"),

                EvaluateHorizontal(
                    GetEdgePoints(basePoints, baseBounds, Edge.Left),
                    GetEdgePoints(adjacentPoints, adjacentBounds, Edge.Right),
                    baseBounds.MinX - adjacentBounds.MaxX,
                    "left-right"),

                EvaluateVertical(
                    GetEdgePoints(basePoints, baseBounds, Edge.Top),
                    GetEdgePoints(adjacentPoints, adjacentBounds, Edge.Bottom),
                    baseBounds.MaxY - adjacentBounds.MinY,
                    "top-bottom"),

                EvaluateVertical(
                    GetEdgePoints(basePoints, baseBounds, Edge.Bottom),
                    GetEdgePoints(adjacentPoints, adjacentBounds, Edge.Top),
                    baseBounds.MinY - adjacentBounds.MaxY,
                    "bottom-top")
            };

            return candidates
                .Where(c => c != null)
                .OrderByDescending(c => c!.MatchedPointCount)
                .ThenBy(c => c!.RmsError)
                .FirstOrDefault();
        }

        private static EdgeRelationship? EvaluateHorizontal(
            List<TiePoint> baseEdge,
            List<TiePoint> adjacentEdge,
            double translateX,
            string direction)
        {
            var matches = MatchByCoordinate(baseEdge, adjacentEdge, useX: false);

            if (matches.Count < MinOverlapPoints)
                return null;

            var translateY = matches.Average(m => m.BasePoint.SourceY - m.AdjacentPoint.SourceY);
            var rms = CalculateRms(matches, translateX, translateY);

            return new EdgeRelationship(direction, translateX, translateY, rms, matches.Count);
        }

        private static EdgeRelationship? EvaluateVertical(
            List<TiePoint> baseEdge,
            List<TiePoint> adjacentEdge,
            double translateY,
            string direction)
        {
            var matches = MatchByCoordinate(baseEdge, adjacentEdge, useX: true);

            if (matches.Count < MinOverlapPoints)
                return null;

            var translateX = matches.Average(m => m.BasePoint.SourceX - m.AdjacentPoint.SourceX);
            var rms = CalculateRms(matches, translateX, translateY);

            return new EdgeRelationship(direction, translateX, translateY, rms, matches.Count);
        }

        private static List<(TiePoint BasePoint, TiePoint AdjacentPoint)> MatchByCoordinate(
            List<TiePoint> baseEdge,
            List<TiePoint> adjacentEdge,
            bool useX)
        {
            var matches = new List<(TiePoint BasePoint, TiePoint AdjacentPoint)>();
            var usedAdjacent = new HashSet<int>();

            foreach (var basePoint in baseEdge.OrderBy(p => useX ? p.SourceX : p.SourceY))
            {
                var candidate = adjacentEdge
                    .Where(p => !usedAdjacent.Contains(p.PointID))
                    .Select(p => new
                    {
                        Point = p,
                        Difference = Math.Abs((useX ? p.SourceX : p.SourceY) -
                                              (useX ? basePoint.SourceX : basePoint.SourceY))
                    })
                    .Where(x => x.Difference <= PointMatchThreshold)
                    .OrderBy(x => x.Difference)
                    .FirstOrDefault();

                if (candidate == null)
                    continue;

                matches.Add((basePoint, candidate.Point));
                usedAdjacent.Add(candidate.Point.PointID);
            }

            return matches;
        }

        private static double CalculateRms(
            List<(TiePoint BasePoint, TiePoint AdjacentPoint)> matches,
            double translateX,
            double translateY)
        {
            var squaredError = matches.Sum(m =>
            {
                var dx = (m.AdjacentPoint.SourceX + translateX) - m.BasePoint.SourceX;
                var dy = (m.AdjacentPoint.SourceY + translateY) - m.BasePoint.SourceY;
                return (dx * dx) + (dy * dy);
            });

            return Math.Sqrt(squaredError / matches.Count);
        }

        private readonly record struct Bounds(
            double MinX,
            double MaxX,
            double MinY,
            double MaxY);

        private readonly record struct EdgeRelationship(
            string Direction,
            double TranslateX,
            double TranslateY,
            double RmsError,
            int MatchedPointCount);

        private enum Edge
        {
            Left,
            Right,
            Bottom,
            Top
        }
    }
}