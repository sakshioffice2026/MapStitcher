// Services/CadastralMergeService.cs — full file
using MapStitcher.Business.Contracts;
using MapStitcher.Database;
using MapStitcher.Repositories.Contracts;
using NetTopologySuite.Geometries;
using NetTopologySuite.Geometries.Utilities;

namespace MapStitcher.Business.Services
{
    public class CadastralMergeService : ICadastralMergeService
    {
        private readonly ITiePointRepository _tiePointRepo;
        private readonly ISurveySheetRepository _sheetRepo;
        private const double RmsErrorThreshold = 5.0;
        private const double OutlierMultiplier = 2.5;

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

            var matchedPairs = FindMatchingPairs(basePoints, adjacentPoints);

            if (matchedPairs.Count < 2)
            {
                return new MergeResult
                {
                    Success = false,
                    MatchedPointCount = matchedPairs.Count,
                    Message = $"Insufficient matching tie points ({matchedPairs.Count}). Minimum 2 required."
                };
            }

            var transform = ComputeSimilarityTransform(matchedPairs, out double rotation, out double scale, out double tx, out double ty);
            var residuals = ComputeResiduals(matchedPairs, transform);

            var sortedResiduals = residuals.OrderBy(r => r).ToList();
            double median = sortedResiduals[sortedResiduals.Count / 2];
            double cutoff = Math.Max(median * OutlierMultiplier, 1e-6);

            var inlierPairs = matchedPairs
                .Where((pair, idx) => residuals[idx] <= cutoff)
                .ToList();

            int rejectedCount = matchedPairs.Count - inlierPairs.Count;

            if (inlierPairs.Count < 2)
            {
                return new MergeResult
                {
                    Success = false,
                    MatchedPointCount = matchedPairs.Count,
                    RejectedOutlierCount = rejectedCount,
                    Message = $"Too few inlier points after outlier rejection ({inlierPairs.Count}/{matchedPairs.Count})."
                };
            }

            transform = ComputeSimilarityTransform(inlierPairs, out rotation, out scale, out tx, out ty);
            double rmsError = ComputeRmsError(inlierPairs, transform);

            foreach (var point in adjacentPoints)
            {
                var source = new Coordinate(point.SourceX, point.SourceY);
                var transformed = transform.Transform(source, new Coordinate());

                point.TargetX = transformed.X;
                point.TargetY = transformed.Y;
            }

            bool success = rmsError <= RmsErrorThreshold;

            if (success)
            {
                // Persist transform so export can reproduce the same alignment
                // on the sheet's boundary geometry, not just its tie points.
                adjacentSheet.TransformRotation = rotation;
                adjacentSheet.TransformScale = scale;
                adjacentSheet.TransformTranslateX = tx;
                adjacentSheet.TransformTranslateY = ty;
            }

            adjacentSheet.Status = success ? SheetStatus.Merged : SheetStatus.Failed;
            baseSheet.Status = success ? SheetStatus.Merged : baseSheet.Status;

            await _tiePointRepo.SaveChangesAsync();
            await _sheetRepo.SaveChangesAsync();

            return new MergeResult
            {
                Success = success,
                RmsErrorMeters = rmsError,
                MatchedPointCount = matchedPairs.Count,
                InlierPointCount = inlierPairs.Count,
                RejectedOutlierCount = rejectedCount,
                Message = success
                    ? $"Merge completed using {inlierPairs.Count}/{matchedPairs.Count} points ({rejectedCount} outliers rejected)."
                    : $"RMS error ({rmsError:F3}) exceeds threshold ({RmsErrorThreshold}) even after rejecting {rejectedCount} outliers."
            };
        }

        private List<(TiePoint BasePoint, TiePoint AdjacentPoint)> FindMatchingPairs(
            List<TiePoint> basePoints, List<TiePoint> adjacentPoints)
        {
            var pairs = new List<(TiePoint, TiePoint)>();

            foreach (var basePoint in basePoints)
            {
                if (string.IsNullOrWhiteSpace(basePoint.PointLabel))
                    continue;

                var match = adjacentPoints.FirstOrDefault(a => a.PointLabel == basePoint.PointLabel);
                if (match != null)
                    pairs.Add((basePoint, match));
            }

            return pairs;
        }

        private AffineTransformation ComputeSimilarityTransform(
            List<(TiePoint BasePoint, TiePoint AdjacentPoint)> matchedPairs,
            out double rotation, out double scale, out double translateX, out double translateY)
        {
            double srcCentroidX = matchedPairs.Average(p => p.AdjacentPoint.SourceX);
            double srcCentroidY = matchedPairs.Average(p => p.AdjacentPoint.SourceY);
            double dstCentroidX = matchedPairs.Average(p => p.BasePoint.SourceX);
            double dstCentroidY = matchedPairs.Average(p => p.BasePoint.SourceY);

            double a = 0;
            double b = 0;
            double srcVarSum = 0;

            foreach (var (basePoint, adjacentPoint) in matchedPairs)
            {
                double px = adjacentPoint.SourceX - srcCentroidX;
                double py = adjacentPoint.SourceY - srcCentroidY;
                double qx = basePoint.SourceX - dstCentroidX;
                double qy = basePoint.SourceY - dstCentroidY;

                a += px * qx + py * qy;
                b += px * qy - py * qx;
                srcVarSum += px * px + py * py;
            }

            if (srcVarSum == 0)
                throw new InvalidOperationException("Matched tie points have zero variance; cannot compute transform.");

            rotation = Math.Atan2(b, a);
            scale = Math.Sqrt(a * a + b * b) / srcVarSum;

            var transform = new AffineTransformation();
            transform.Compose(AffineTransformation.TranslationInstance(-srcCentroidX, -srcCentroidY));
            transform.Compose(AffineTransformation.RotationInstance(rotation));
            transform.Compose(AffineTransformation.ScaleInstance(scale, scale));
            transform.Compose(AffineTransformation.TranslationInstance(dstCentroidX, dstCentroidY));

            // Net translation term for the composed transform (used at export time)
            translateX = dstCentroidX;
            translateY = dstCentroidY;

            return transform;
        }

        private List<double> ComputeResiduals(
            List<(TiePoint BasePoint, TiePoint AdjacentPoint)> matchedPairs,
            AffineTransformation transform)
        {
            var residuals = new List<double>();

            foreach (var (basePoint, adjacentPoint) in matchedPairs)
            {
                var source = new Coordinate(adjacentPoint.SourceX, adjacentPoint.SourceY);
                var transformed = transform.Transform(source, new Coordinate());

                double dx = transformed.X - basePoint.SourceX;
                double dy = transformed.Y - basePoint.SourceY;

                residuals.Add(Math.Sqrt(dx * dx + dy * dy));
            }

            return residuals;
        }

        private double ComputeRmsError(
            List<(TiePoint BasePoint, TiePoint AdjacentPoint)> matchedPairs,
            AffineTransformation transform)
        {
            var residuals = ComputeResiduals(matchedPairs, transform);
            double sumSquared = residuals.Sum(r => r * r);
            return Math.Sqrt(sumSquared / residuals.Count);
        }
    }
}