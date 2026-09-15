using MapStitcher.Business.Contracts;
using MapStitcher.Database;
using MapStitcher.Repositories.Contracts;

namespace MapStitcher.Business.Services
{
    public class SheetPlacementService : ISheetPlacementService
    {
        private static readonly (
            int dRow,
            int dCol
        )[] AdjacencyOffsets =
        {
            (0, 1),
            (1, 0),
            (0, -1),
            (-1, 0)
        };

        private readonly ISurveySheetRepository _sheetRepo;
        private readonly ITiePointRepository _tiePointRepo;

        public SheetPlacementService(
            ISurveySheetRepository sheetRepo,
            ITiePointRepository tiePointRepo)
        {
            _sheetRepo = sheetRepo;
            _tiePointRepo = tiePointRepo;
        }

        public async Task<PlacementResult> AutoPlaceSheetAsync(
            int sheetId)
        {
            var sheet =
                await _sheetRepo.GetByIdAsync(sheetId);

            if (sheet == null)
                return Fail(
                    sheetId,
                    "Sheet not found.");

            var siblings =
                await _sheetRepo.GetByProjectIdAsync(
                    sheet.ProjectID);

            var placedSheets =
                siblings
                    .Where(
                        s =>
                            s.SheetID != sheetId &&
                            s.GridRow.HasValue &&
                            s.GridCol.HasValue)
                    .ToList();

            if (placedSheets.Count == 0)
            {
                sheet.TransformTranslateX = 0.0;
                sheet.TransformTranslateY = 0.0;

                return await CommitPlacement(
                    sheet,
                    0,
                    0,
                    PlacementMode.Auto);
            }

            /*
             * Geometry is now the primary placement signal.
             *
             * Labels and centroid calculations are intentionally not used.
             */
            var incomingPoints =
                await _tiePointRepo.GetBySheetIdAsync(
                    sheet.SheetID);

            if (incomingPoints.Count == 0)
            {
                return Fail(
                    sheetId,
                    "Sheet contains no geometry coordinates.");
            }

            var incomingExtent =
                CalculateExtent(incomingPoints);

            /*
             * Try to find the strongest geometric relationship with an
             * already placed sheet.
             */
            var candidates =
                new List<PlacementCandidate>();

            foreach (var placedSheet in placedSheets)
            {
                var placedPoints =
                    await _tiePointRepo.GetBySheetIdAsync(
                        placedSheet.SheetID);

                if (placedPoints.Count == 0)
                    continue;

                var placedExtent =
                    CalculateExtent(placedPoints);

                var candidate =
                    FindBestPlacement(
                        placedSheet,
                        placedPoints,
                        incomingPoints,
                        placedExtent,
                        incomingExtent);

                if (candidate != null)
                    candidates.Add(candidate);
            }

            var bestCandidate =
                candidates
                    .OrderByDescending(c => c.Score)
                    .ThenBy(c => c.AlignmentError)
                    .FirstOrDefault();

            if (bestCandidate != null)
            {
                return await CommitPlacement(
                    sheet,
                    bestCandidate.GridRow,
                    bestCandidate.GridCol,
                    PlacementMode.Auto);
            }

            /*
             * If there is no geometric edge match yet, preserve the
             * project's grid structure without using labels.
             */
            var fallback =
                FindFirstOpenGridCell(
                    placedSheets);

            if (fallback != null)
            {
                return await CommitPlacement(
                    sheet,
                    fallback.Value.row,
                    fallback.Value.col,
                    PlacementMode.Auto);
            }

            return Fail(
                sheetId,
                "Could not find an empty grid cell.");
        }

        public async Task<List<OpenSlot>> GetOpenTargetSlotsAsync(
            int sheetId)
        {
            var slots =
                new List<OpenSlot>();

            var sheet =
                await _sheetRepo.GetByIdAsync(sheetId);

            if (sheet == null)
                return slots;

            var placedSheets =
                (await _sheetRepo.GetByProjectIdAsync(
                    sheet.ProjectID))
                .Where(
                    s =>
                        s.SheetID != sheetId &&
                        s.GridRow.HasValue &&
                        s.GridCol.HasValue)
                .ToList();

            if (placedSheets.Count == 0)
                return slots;

            foreach (var placedSheet in placedSheets)
            {
                foreach (var (dRow, dCol)
                    in AdjacencyOffsets)
                {
                    var row =
                        placedSheet.GridRow!.Value +
                        dRow;

                    var col =
                        placedSheet.GridCol!.Value +
                        dCol;

                    var occupied =
                        placedSheets.Any(
                            s =>
                                s.GridRow == row &&
                                s.GridCol == col);

                    var alreadyListed =
                        slots.Any(
                            s =>
                                s.GridRow == row &&
                                s.GridCol == col);

                    if (!occupied &&
                        !alreadyListed)
                    {
                        slots.Add(
                            new OpenSlot
                            {
                                GridRow = row,
                                GridCol = col
                            });
                    }
                }
            }

            return slots;
        }

        public async Task<PlacementResult> ManualPlaceSheetAsync(
            int sheetId,
            int gridRow,
            int gridCol)
        {
            var sheet =
                await _sheetRepo.GetByIdAsync(sheetId);

            if (sheet == null)
            {
                return Fail(
                    sheetId,
                    "Sheet not found.");
            }

            var siblings =
                await _sheetRepo.GetByProjectIdAsync(
                    sheet.ProjectID);

            var occupied =
                siblings.Any(
                    s =>
                        s.SheetID != sheetId &&
                        s.GridRow == gridRow &&
                        s.GridCol == gridCol);

            if (occupied)
            {
                return Fail(
                    sheetId,
                    $"Grid cell ({gridRow},{gridCol}) " +
                    "is already occupied by another sheet.");
            }

            return await CommitPlacement(
                sheet,
                gridRow,
                gridCol,
                PlacementMode.Manual);
        }

        private static PlacementCandidate? FindBestPlacement(
            SurveySheet placedSheet,
            List<TiePoint> placedPoints,
            List<TiePoint> incomingPoints,
            SheetExtent placedExtent,
            SheetExtent incomingExtent)
        {
            var candidates =
                new List<PlacementCandidate?>();

            candidates.Add(
                EvaluateHorizontalPlacement(
                    placedSheet,
                    placedPoints,
                    incomingPoints,
                    placedExtent,
                    incomingExtent,
                    true));

            candidates.Add(
                EvaluateHorizontalPlacement(
                    placedSheet,
                    placedPoints,
                    incomingPoints,
                    placedExtent,
                    incomingExtent,
                    false));

            candidates.Add(
                EvaluateVerticalPlacement(
                    placedSheet,
                    placedPoints,
                    incomingPoints,
                    placedExtent,
                    incomingExtent,
                    true));

            candidates.Add(
                EvaluateVerticalPlacement(
                    placedSheet,
                    placedPoints,
                    incomingPoints,
                    placedExtent,
                    incomingExtent,
                    false));

            return candidates
                .Where(c => c != null)
                .OrderByDescending(c => c!.Score)
                .ThenBy(c => c!.AlignmentError)
                .FirstOrDefault();
        }

        private static PlacementCandidate?
            EvaluateHorizontalPlacement(
                SurveySheet placedSheet,
                List<TiePoint> placedPoints,
                List<TiePoint> incomingPoints,
                SheetExtent placedExtent,
                SheetExtent incomingExtent,
                bool incomingIsRight)
        {
            var baseEdge =
                incomingIsRight
                    ? GetRightEdgePoints(
                        placedPoints,
                        placedExtent)
                    : GetLeftEdgePoints(
                        placedPoints,
                        placedExtent);

            var incomingEdge =
                incomingIsRight
                    ? GetLeftEdgePoints(
                        incomingPoints,
                        incomingExtent)
                    : GetRightEdgePoints(
                        incomingPoints,
                        incomingExtent);

            if (baseEdge.Count < 2 ||
                incomingEdge.Count < 2)
            {
                return null;
            }

            var matches =
                MatchCoordinates(
                    baseEdge,
                    incomingEdge,
                    false);

            if (matches.Count < 2)
                return null;

            var baseRange =
                GetRange(
                    baseEdge.Select(
                        p => p.SourceY));

            var incomingRange =
                GetRange(
                    incomingEdge.Select(
                        p => p.SourceY));

            var overlap =
                CalculateOverlapRatio(
                    baseRange,
                    incomingRange);

            if (overlap < 0.50)
                return null;

            var alignmentError =
                CalculateAlignmentError(
                    matches,
                    false);

            var row =
                placedSheet.GridRow!.Value;

            var col =
                placedSheet.GridCol!.Value +
                (incomingIsRight ? 1 : -1);

            var score =
                matches.Count * 100.0 +
                overlap * 100.0 -
                alignmentError;

            return new PlacementCandidate
            {
                GridRow = row,
                GridCol = col,
                Score = score,
                AlignmentError = alignmentError
            };
        }

        private static PlacementCandidate?
            EvaluateVerticalPlacement(
                SurveySheet placedSheet,
                List<TiePoint> placedPoints,
                List<TiePoint> incomingPoints,
                SheetExtent placedExtent,
                SheetExtent incomingExtent,
                bool incomingIsTop)
        {
            var baseEdge =
                incomingIsTop
                    ? GetTopEdgePoints(
                        placedPoints,
                        placedExtent)
                    : GetBottomEdgePoints(
                        placedPoints,
                        placedExtent);

            var incomingEdge =
                incomingIsTop
                    ? GetBottomEdgePoints(
                        incomingPoints,
                        incomingExtent)
                    : GetTopEdgePoints(
                        incomingPoints,
                        incomingExtent);

            if (baseEdge.Count < 2 ||
                incomingEdge.Count < 2)
            {
                return null;
            }

            var matches =
                MatchCoordinates(
                    baseEdge,
                    incomingEdge,
                    true);

            if (matches.Count < 2)
                return null;

            var baseRange =
                GetRange(
                    baseEdge.Select(
                        p => p.SourceX));

            var incomingRange =
                GetRange(
                    incomingEdge.Select(
                        p => p.SourceX));

            var overlap =
                CalculateOverlapRatio(
                    baseRange,
                    incomingRange);

            if (overlap < 0.50)
                return null;

            var alignmentError =
                CalculateAlignmentError(
                    matches,
                    true);

            var row =
                placedSheet.GridRow!.Value +
                (incomingIsTop ? -1 : 1);

            var col =
                placedSheet.GridCol!.Value;

            var score =
                matches.Count * 100.0 +
                overlap * 100.0 -
                alignmentError;

            return new PlacementCandidate
            {
                GridRow = row,
                GridCol = col,
                Score = score,
                AlignmentError = alignmentError
            };
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
                            extent.MinX) <= 25.0)
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
                            extent.MaxX) <= 25.0)
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
                            extent.MinY) <= 25.0)
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
                            extent.MaxY) <= 25.0)
                .ToList();
        }

        private static List<PointPair> MatchCoordinates(
            List<TiePoint> basePoints,
            List<TiePoint> adjacentPoints,
            bool useX)
        {
            var result =
                new List<PointPair>();

            var orderedBase =
                useX
                    ? basePoints.OrderBy(
                        p => p.SourceX).ToList()
                    : basePoints.OrderBy(
                        p => p.SourceY).ToList();

            var orderedAdjacent =
                useX
                    ? adjacentPoints.OrderBy(
                        p => p.SourceX).ToList()
                    : adjacentPoints.OrderBy(
                        p => p.SourceY).ToList();

            var used =
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
                    if (used.Contains(
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

                    if (difference <= 25.0 &&
                        difference < bestDifference)
                    {
                        best =
                            adjacentPoint;

                        bestDifference =
                            difference;
                    }
                }

                if (best == null)
                    continue;

                result.Add(
                    new PointPair
                    {
                        Base = basePoint,
                        Adjacent = best
                    });

                used.Add(
                    best.PointID);
            }

            return result;
        }

        private static double CalculateAlignmentError(
            List<PointPair> matches,
            bool useX)
        {
            if (matches.Count == 0)
                return double.MaxValue;

            var errors =
                matches.Select(
                    pair =>
                    {
                        var a =
                            useX
                                ? pair.Base.SourceX
                                : pair.Base.SourceY;

                        var b =
                            useX
                                ? pair.Adjacent.SourceX
                                : pair.Adjacent.SourceY;

                        var difference =
                            a - b;

                        return difference *
                               difference;
                    });

            return Math.Sqrt(
                errors.Average());
        }

        private static SheetExtent CalculateExtent(
            List<TiePoint> points)
        {
            return new SheetExtent
            {
                MinX =
                    points.Min(p => p.SourceX),

                MaxX =
                    points.Max(p => p.SourceX),

                MinY =
                    points.Min(p => p.SourceY),

                MaxY =
                    points.Max(p => p.SourceY)
            };
        }

        private static double CalculateOverlapRatio(
            (double Min, double Max) a,
            (double Min, double Max) b)
        {
            var overlapMin =
                Math.Max(a.Min, b.Min);

            var overlapMax =
                Math.Min(a.Max, b.Max);

            var overlap =
                Math.Max(
                    0,
                    overlapMax - overlapMin);

            var smaller =
                Math.Min(
                    a.Max - a.Min,
                    b.Max - b.Min);

            return smaller <= 0
                ? 1.0
                : overlap / smaller;
        }

        private static (double Min, double Max)
            GetRange(
                IEnumerable<double> values)
        {
            var list =
                values.ToList();

            return (
                list.Min(),
                list.Max()
            );
        }

        private static (int row, int col)?
            FindFirstOpenGridCell(
                List<SurveySheet> placedSheets)
        {
            for (var row = 0; row < 100; row++)
            {
                for (var col = 0; col < 100; col++)
                {
                    if (!placedSheets.Any(
                            s =>
                                s.GridRow == row &&
                                s.GridCol == col))
                    {
                        return (row, col);
                    }
                }
            }

            return null;
        }

        private async Task<PlacementResult>
            CommitPlacement(
                SurveySheet sheet,
                int row,
                int col,
                PlacementMode mode)
        {
            sheet.GridRow = row;
            sheet.GridCol = col;
            sheet.Status = SheetStatus.Parsed;

            await _sheetRepo.SaveChangesAsync();

            return new PlacementResult
            {
                Success = true,
                SheetID = sheet.SheetID,
                GridRow = row,
                GridCol = col,
                Mode = mode,
                Message =
                    $"Sheet placed at ({row},{col}) via {mode}."
            };
        }

        private static PlacementResult Fail(
            int sheetId,
            string message)
        {
            return new PlacementResult
            {
                Success = false,
                SheetID = sheetId,
                Message = message
            };
        }

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

        private sealed class PlacementCandidate
        {
            public int GridRow { get; init; }
            public int GridCol { get; init; }
            public double Score { get; init; }
            public double AlignmentError { get; init; }
        }
    }
}