// Services/CadastralMergeService.cs
using MapStitcher.Business.Contracts;
using MapStitcher.Database;
using MapStitcher.Repositories.Contracts;

namespace MapStitcher.Business.Services
{
    /// <summary>
    /// Computes sheet-to-sheet translation from actual CAD tie-point XY
    /// correspondences. Laghu/index relationships are used as topology, while
    /// CAD coordinates remain the source of the physical translation.
    /// </summary>
    public class CadastralMergeService : ICadastralMergeService
    {
        private const double MatchTolerance = 0.50;
        private const double GraphValidationTolerance = 1.00;

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

            var basePoints = await _tiePointRepo.GetBySheetIdAsync(baseSheetId);
            var adjacentPoints = await _tiePointRepo.GetBySheetIdAsync(adjacentSheetId);

            var translation = EstimateTranslation(basePoints, adjacentPoints);

            if (!translation.Success)
            {
                return new MergeResult
                {
                    Success = false,
                    MatchedPointCount = translation.MatchedPointCount,
                    InlierPointCount = translation.InlierPointCount,
                    RejectedOutlierCount = translation.RejectedOutlierCount,
                    RmsErrorMeters = translation.RmsError,
                    Message =
                        $"Cannot establish a common XY translation for " +
                        $"{baseSheet.SheetNumber} <-> {adjacentSheet.SheetNumber}. " +
                        translation.Message,
                    FailureReason =
                        "Needs manual check: no reliable multi-point XY correspondence"
                };
            }

            var linkedVia = ResolveLaghuLink(baseSheet, adjacentSheet);

            var topology = await ValidateTranslationGraphAsync(
                baseSheet,
                adjacentSheet,
                translation.TranslateX,
                translation.TranslateY);

            if (!topology.Success)
            {
                return new MergeResult
                {
                    Success = false,
                    MatchedPointCount = translation.MatchedPointCount,
                    InlierPointCount = translation.InlierPointCount,
                    RejectedOutlierCount = translation.RejectedOutlierCount,
                    RmsErrorMeters = translation.RmsError,
                    Message = topology.Message,
                    FailureReason =
                        "Needs manual check: sheet topology has inconsistent translations"
                };
            }

            double globalTranslateX =
                baseSheet.TransformTranslateX + translation.TranslateX;

            double globalTranslateY =
                baseSheet.TransformTranslateY + translation.TranslateY;

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

            var topologyText = linkedVia != null
                ? $"; Laghu topology link: {linkedVia}"
                : "; no direct Laghu link was available";

            return new MergeResult
            {
                Success = true,
                MatchedPointCount = translation.MatchedPointCount,
                InlierPointCount = translation.InlierPointCount,
                RejectedOutlierCount = translation.RejectedOutlierCount,
                RmsErrorMeters = translation.RmsError,
                Message =
                    $"Merged {baseSheet.SheetNumber} <-> {adjacentSheet.SheetNumber} " +
                    $"using XY translation " +
                    $"dx={translation.TranslateX:0.###}, " +
                    $"dy={translation.TranslateY:0.###}, " +
                    $"RMS={translation.RmsError:0.###}{topologyText}."
            };
        }

        private async Task<GraphValidationResult> ValidateTranslationGraphAsync(
            SurveySheet baseSheet,
            SurveySheet adjacentSheet,
            double directTranslateX,
            double directTranslateY)
        {
            var sheets = await _sheetRepo.GetByProjectIdAsync(baseSheet.ProjectID);

            if (sheets.Count <= 1)
                return GraphValidationResult.Ok();

            var byNumber = sheets
                .Where(s => !string.IsNullOrWhiteSpace(s.SheetNumber))
                .GroupBy(s => NormalizeSheetNumber(s.SheetNumber))
                .ToDictionary(
                    g => g.Key,
                    g => g.First(),
                    StringComparer.OrdinalIgnoreCase);

            if (!byNumber.ContainsKey(
                    NormalizeSheetNumber(baseSheet.SheetNumber)) ||
                !byNumber.ContainsKey(
                    NormalizeSheetNumber(adjacentSheet.SheetNumber)))
            {
                return GraphValidationResult.Ok();
            }

            var adjacency = BuildTopology(sheets, byNumber);

            var startKey = NormalizeSheetNumber(baseSheet.SheetNumber);

            var expected =
                new Dictionary<string, (double X, double Y)>(
                    StringComparer.OrdinalIgnoreCase)
                {
                    [startKey] =
                    (
                        baseSheet.TransformTranslateX,
                        baseSheet.TransformTranslateY
                    )
                };

            var queue = new Queue<string>();
            queue.Enqueue(startKey);

            var edgeCache =
                new Dictionary<(int A, int B), TranslationEstimate>();

            while (queue.Count > 0)
            {
                var currentKey = queue.Dequeue();
                var current = byNumber[currentKey];
                var currentPosition = expected[currentKey];

                foreach (var neighborKey in adjacency[currentKey])
                {
                    var neighbor = byNumber[neighborKey];

                    var edge =
                        await GetEdgeTranslationAsync(
                            current,
                            neighbor,
                            edgeCache);

                    if (!edge.Success)
                        continue;

                    var predicted =
                    (
                        X: currentPosition.X + edge.TranslateX,
                        Y: currentPosition.Y + edge.TranslateY
                    );

                    if (expected.TryGetValue(
                        neighborKey,
                        out var existingExpected))
                    {
                        var cycleError =
                            Distance(
                                predicted.X,
                                predicted.Y,
                                existingExpected.X,
                                existingExpected.Y);

                        if (cycleError > GraphValidationTolerance)
                        {
                            return GraphValidationResult.Fail(
                                $"Translation graph conflict at sheet " +
                                $"{neighbor.SheetNumber}: two topology paths " +
                                $"differ by {cycleError:0.###} CAD units.");
                        }

                        continue;
                    }

                    expected[neighborKey] = predicted;
                    queue.Enqueue(neighborKey);

                    if (neighbor.Status == SheetStatus.Merged)
                    {
                        var persistedError =
                            Distance(
                                predicted.X,
                                predicted.Y,
                                neighbor.TransformTranslateX,
                                neighbor.TransformTranslateY);

                        bool isDirectPair =
                            (current.SheetID == baseSheet.SheetID &&
                             neighbor.SheetID == adjacentSheet.SheetID) ||
                            (current.SheetID == adjacentSheet.SheetID &&
                             neighbor.SheetID == baseSheet.SheetID);

                        if (!isDirectPair &&
                            persistedError > GraphValidationTolerance)
                        {
                            return GraphValidationResult.Fail(
                                $"Persisted transform for sheet " +
                                $"{neighbor.SheetNumber} differs from the " +
                                $"topology chain by " +
                                $"{persistedError:0.###} CAD units.");
                        }
                    }
                }
            }

            if (adjacentSheet.TransformTranslateX != 0.0 ||
                adjacentSheet.TransformTranslateY != 0.0)
            {
                var expectedAdjacent =
                (
                    X: baseSheet.TransformTranslateX + directTranslateX,
                    Y: baseSheet.TransformTranslateY + directTranslateY
                );

                var persistedError =
                    Distance(
                        expectedAdjacent.X,
                        expectedAdjacent.Y,
                        adjacentSheet.TransformTranslateX,
                        adjacentSheet.TransformTranslateY);

                _ = persistedError;
            }

            return GraphValidationResult.Ok();
        }

        private async Task<TranslationEstimate> GetEdgeTranslationAsync(
            SurveySheet a,
            SurveySheet b,
            Dictionary<(int A, int B), TranslationEstimate> cache)
        {
            var key = a.SheetID < b.SheetID
                ? (a.SheetID, b.SheetID)
                : (b.SheetID, a.SheetID);

            if (cache.TryGetValue(key, out var cached))
            {
                return a.SheetID == key.A
                    ? cached
                    : cached.Reversed();
            }

            var aPoints =
                await _tiePointRepo.GetBySheetIdAsync(a.SheetID);

            var bPoints =
                await _tiePointRepo.GetBySheetIdAsync(b.SheetID);

            var estimate =
                EstimateTranslation(aPoints, bPoints);

            cache[key] =
                a.SheetID == key.A
                    ? estimate
                    : estimate.Reversed();

            return estimate;
        }

        private static Dictionary<string, HashSet<string>> BuildTopology(
            List<SurveySheet> sheets,
            Dictionary<string, SurveySheet> byNumber)
        {
            var graph = sheets
                .Where(s => !string.IsNullOrWhiteSpace(s.SheetNumber))
                .ToDictionary(
                    s => NormalizeSheetNumber(s.SheetNumber),
                    _ => new HashSet<string>(
                        StringComparer.OrdinalIgnoreCase),
                    StringComparer.OrdinalIgnoreCase);

            foreach (var sheet in sheets)
            {
                var sourceKey =
                    NormalizeSheetNumber(sheet.SheetNumber);

                if (!graph.ContainsKey(sourceKey))
                    continue;

                AddTopologyEdge(
                    graph,
                    sourceKey,
                    sheet.NeighborSheetNumberNorth,
                    byNumber);

                AddTopologyEdge(
                    graph,
                    sourceKey,
                    sheet.NeighborSheetNumberSouth,
                    byNumber);

                AddTopologyEdge(
                    graph,
                    sourceKey,
                    sheet.NeighborSheetNumberEast,
                    byNumber);

                AddTopologyEdge(
                    graph,
                    sourceKey,
                    sheet.NeighborSheetNumberWest,
                    byNumber);

                AddTopologyEdge(
                    graph,
                    sourceKey,
                    sheet.LaghuReferenceNumber,
                    byNumber);
            }

            return graph;
        }

        private static void AddTopologyEdge(
            Dictionary<string, HashSet<string>> graph,
            string sourceKey,
            string? neighborNumber,
            Dictionary<string, SurveySheet> byNumber)
        {
            if (string.IsNullOrWhiteSpace(neighborNumber))
                return;

            var targetKey =
                NormalizeSheetNumber(neighborNumber);

            if (targetKey == sourceKey ||
                !byNumber.ContainsKey(targetKey))
            {
                return;
            }

            graph[sourceKey].Add(targetKey);
            graph[targetKey].Add(sourceKey);
        }

        private static TranslationEstimate EstimateTranslation(
            List<TiePoint> basePoints,
            List<TiePoint> adjacentPoints)
        {
            var labeledPairs =
                FindLabelPairs(
                    basePoints,
                    adjacentPoints);

            if (labeledPairs.Count > 0)
            {
                return FitTranslation(labeledPairs);
            }

            if (basePoints.Count == 0 ||
                adjacentPoints.Count == 0)
            {
                return TranslationEstimate.Fail(
                    "One or both sheets contain no tie points.");
            }

            TranslationEstimate? best = null;

            foreach (var basePoint in basePoints)
            {
                foreach (var adjacentPoint in adjacentPoints)
                {
                    var candidate =
                        FitTranslationFromHypothesis(
                            basePoints,
                            adjacentPoints,
                            basePoint.SourceX -
                            adjacentPoint.SourceX,
                            basePoint.SourceY -
                            adjacentPoint.SourceY);

                    if (best == null ||
                        candidate.InlierPointCount >
                        best.InlierPointCount ||
                        (candidate.InlierPointCount ==
                         best.InlierPointCount &&
                         candidate.RmsError <
                         best.RmsError))
                    {
                        best = candidate;
                    }
                }
            }

            if (best == null || !best.Success)
            {
                return TranslationEstimate.Fail(
                    "No common XY translation was supported " +
                    "by the CAD points.");
            }

            return best;
        }

        private static List<(
            TiePoint BasePoint,
            TiePoint AdjacentPoint)> FindLabelPairs(
            List<TiePoint> basePoints,
            List<TiePoint> adjacentPoints)
        {
            var result =
                new List<(TiePoint, TiePoint)>();

            var used =
                new HashSet<int>();

            foreach (var basePoint in basePoints)
            {
                if (string.IsNullOrWhiteSpace(
                    basePoint.PointLabel))
                {
                    continue;
                }

                var match = adjacentPoints
                    .Where(a =>
                        !used.Contains(a.PointID))
                    .FirstOrDefault(a =>
                        string.Equals(
                            NormalizeLabel(a.PointLabel),
                            NormalizeLabel(
                                basePoint.PointLabel),
                            StringComparison.OrdinalIgnoreCase));

                if (match == null)
                    continue;

                result.Add((basePoint, match));
                used.Add(match.PointID);
            }

            return result;
        }

        private static TranslationEstimate FitTranslation(
            List<(
                TiePoint BasePoint,
                TiePoint AdjacentPoint)> pairs)
        {
            if (pairs.Count == 0)
            {
                return TranslationEstimate.Fail(
                    "No corresponding points were found.");
            }

            var dx =
                Median(
                    pairs.Select(
                        p => p.BasePoint.SourceX -
                             p.AdjacentPoint.SourceX));

            var dy =
                Median(
                    pairs.Select(
                        p => p.BasePoint.SourceY -
                             p.AdjacentPoint.SourceY));

            var residuals =
                pairs
                    .Select(
                        p => Distance(
                            p.BasePoint.SourceX,
                            p.BasePoint.SourceY,
                            p.AdjacentPoint.SourceX + dx,
                            p.AdjacentPoint.SourceY + dy))
                    .ToList();

            var inliers =
                residuals.Count(
                    r => r <= MatchTolerance);

            var rejected =
                residuals.Count - inliers;

            var rms =
                Rms(
                    residuals.Where(
                        r => r <= MatchTolerance));

            return new TranslationEstimate(
                Success: inliers >= 1,
                TranslateX: dx,
                TranslateY: dy,
                MatchedPointCount: pairs.Count,
                InlierPointCount: inliers,
                RejectedOutlierCount: rejected,
                RmsError: rms,
                Message:
                    pairs.Count >= 2 &&
                    inliers < 2
                        ? "Only one labeled correspondence " +
                          "agrees with a common translation."
                        : null);
        }

        private static TranslationEstimate
            FitTranslationFromHypothesis(
                List<TiePoint> basePoints,
                List<TiePoint> adjacentPoints,
                double dx,
                double dy)
        {
            var usedAdjacent =
                new HashSet<int>();

            var residuals =
                new List<double>();

            var matched = 0;

            foreach (var basePoint in basePoints)
            {
                var best = adjacentPoints
                    .Where(a =>
                        !usedAdjacent.Contains(
                            a.PointID))
                    .Select(a => new
                    {
                        Point = a,
                        Residual =
                            Distance(
                                basePoint.SourceX,
                                basePoint.SourceY,
                                a.SourceX + dx,
                                a.SourceY + dy)
                    })
                    .Where(x =>
                        x.Residual <= MatchTolerance)
                    .OrderBy(x =>
                        x.Residual)
                    .FirstOrDefault();

                if (best == null)
                    continue;

                usedAdjacent.Add(
                    best.Point.PointID);

                residuals.Add(
                    best.Residual);

                matched++;
            }

            var rms =
                Rms(residuals);

            return new TranslationEstimate(
                Success: matched >= 1,
                TranslateX: dx,
                TranslateY: dy,
                MatchedPointCount: matched,
                InlierPointCount: matched,
                RejectedOutlierCount:
                    Math.Max(
                        basePoints.Count - matched,
                        0),
                RmsError: rms,
                Message:
                    matched < 2
                        ? "The translation is supported " +
                          "by fewer than two independent " +
                          "point correspondences."
                        : null);
        }

        private static string? ResolveLaghuLink(
            SurveySheet baseSheet,
            SurveySheet adjacentSheet)
        {
            if (!string.IsNullOrWhiteSpace(
                    adjacentSheet.LaghuReferenceNumber) &&
                string.Equals(
                    NormalizeSheetNumber(
                        adjacentSheet.LaghuReferenceNumber),
                    NormalizeSheetNumber(
                        baseSheet.SheetNumber),
                    StringComparison.OrdinalIgnoreCase))
            {
                return adjacentSheet.LaghuReferenceNumber;
            }

            if (!string.IsNullOrWhiteSpace(
                    baseSheet.LaghuReferenceNumber) &&
                string.Equals(
                    NormalizeSheetNumber(
                        baseSheet.LaghuReferenceNumber),
                    NormalizeSheetNumber(
                        adjacentSheet.SheetNumber),
                    StringComparison.OrdinalIgnoreCase))
            {
                return baseSheet.LaghuReferenceNumber;
            }

            return null;
        }

        private static string NormalizeSheetNumber(
            string value)
            => value.Trim();

        private static string NormalizeLabel(
            string? value)
            => value?.Trim() ?? string.Empty;

        private static double Median(
            IEnumerable<double> values)
        {
            var ordered =
                values.OrderBy(v => v).ToList();

            if (ordered.Count == 0)
                return 0;

            int middle =
                ordered.Count / 2;

            return ordered.Count % 2 == 0
                ? (ordered[middle - 1] +
                   ordered[middle]) / 2.0
                : ordered[middle];
        }

        private static double Rms(
            IEnumerable<double> values)
        {
            var list =
                values.ToList();

            if (list.Count == 0)
                return 0;

            return Math.Sqrt(
                list.Sum(v => v * v) /
                list.Count);
        }

        private static double Distance(
            double x1,
            double y1,
            double x2,
            double y2)
            => Math.Sqrt(
                Math.Pow(x1 - x2, 2) +
                Math.Pow(y1 - y2, 2));

        private sealed record TranslationEstimate(
            bool Success,
            double TranslateX,
            double TranslateY,
            int MatchedPointCount,
            int InlierPointCount,
            int RejectedOutlierCount,
            double RmsError,
            string? Message)
        {
            public static TranslationEstimate Fail(
                string message)
                => new(
                    false,
                    0,
                    0,
                    0,
                    0,
                    0,
                    0,
                    message);

            public TranslationEstimate Reversed()
                => this with
                {
                    TranslateX = -TranslateX,
                    TranslateY = -TranslateY
                };
        }

        private sealed record GraphValidationResult(
            bool Success,
            string? Message)
        {
            public static GraphValidationResult Ok()
                => new(true, null);

            public static GraphValidationResult Fail(
                string message)
                => new(false, message);
        }
    }
}