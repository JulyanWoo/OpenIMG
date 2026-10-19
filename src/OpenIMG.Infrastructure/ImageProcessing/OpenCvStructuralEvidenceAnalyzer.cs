using OpenCvSharp;
using OpenIMG.Core.Enums;
using OpenIMG.Core.Models;

namespace OpenIMG.Infrastructure.ImageProcessing;

/// <summary>
/// Detects and evaluates structural edge/component evidence. It has no policy
/// decision authority; it only produces reusable evidence and diagnostics.
/// </summary>
internal static class OpenCvStructuralEvidenceAnalyzer
{
    internal sealed record TolerantEdgeResult : IDisposable
    {
        public double Precision { get; init; }
        public double Recall { get; init; }
        public double F1 { get; init; }
        public double MeanDist { get; init; }
        public double UnexpectedEdgeRatio { get; init; }
        public double MissingEdgeRatio { get; init; }
        public int UnexpectedEdgePixelCount { get; init; }
        public int MissingEdgePixelCount { get; init; }
        public Mat UnexpectedEdgeMap { get; init; } = null!;
        public Mat MissingEdgeMap { get; init; } = null!;

        public void Dispose()
        {
            UnexpectedEdgeMap?.Dispose();
            MissingEdgeMap?.Dispose();
        }
    }

    internal static TolerantEdgeResult CalculateTolerantEdgeAgreement(
        Mat edgesOrig,
        Mat edgesRest,
        double tolerance)
    {
        var countOrig = Cv2.CountNonZero(edgesOrig);
        var countRest = Cv2.CountNonZero(edgesRest);
        int rows = edgesOrig.Rows;
        int cols = edgesOrig.Cols;

        var unexpectedMap = new Mat(rows, cols, MatType.CV_8UC1, new Scalar(0));
        var missingMap = new Mat(rows, cols, MatType.CV_8UC1, new Scalar(0));

        if (countOrig == 0 && countRest == 0)
        {
            return new TolerantEdgeResult
            {
                Precision = 1.0,
                Recall = 1.0,
                F1 = 1.0,
                MeanDist = 0.0,
                UnexpectedEdgeRatio = 0.0,
                MissingEdgeRatio = 0.0,
                UnexpectedEdgePixelCount = 0,
                MissingEdgePixelCount = 0,
                UnexpectedEdgeMap = unexpectedMap,
                MissingEdgeMap = missingMap
            };
        }

        if (countOrig == 0 && countRest > 0)
        {
            edgesRest.CopyTo(unexpectedMap);
            return new TolerantEdgeResult
            {
                Precision = 0.0,
                Recall = 0.0,
                F1 = 0.0,
                MeanDist = 100.0,
                UnexpectedEdgeRatio = 1.0,
                MissingEdgeRatio = 0.0,
                UnexpectedEdgePixelCount = countRest,
                MissingEdgePixelCount = 0,
                UnexpectedEdgeMap = unexpectedMap,
                MissingEdgeMap = missingMap
            };
        }

        if (countRest == 0 && countOrig > 0)
        {
            edgesOrig.CopyTo(missingMap);
            return new TolerantEdgeResult
            {
                Precision = 0.0,
                Recall = 0.0,
                F1 = 0.0,
                MeanDist = 100.0,
                UnexpectedEdgeRatio = 0.0,
                MissingEdgeRatio = 1.0,
                UnexpectedEdgePixelCount = 0,
                MissingEdgePixelCount = countOrig,
                UnexpectedEdgeMap = unexpectedMap,
                MissingEdgeMap = missingMap
            };
        }

        // Invert edge maps so edge pixels are 0 and background is 255
        using var invOrig = new Mat();
        using var invRest = new Mat();
        Cv2.BitwiseNot(edgesOrig, invOrig);
        Cv2.BitwiseNot(edgesRest, invRest);

        // Compute Euclidean distance transform (CV_32FC1)
        using var distOrig = new Mat();
        using var distRest = new Mat();
        Cv2.DistanceTransform(invOrig, distOrig, DistanceTypes.L2, DistanceTransformMasks.Mask5);
        Cv2.DistanceTransform(invRest, distRest, DistanceTypes.L2, DistanceTransformMasks.Mask5);

        long matchedRest = 0;
        int unexpectedCount = 0;
        double sumDistRestToOrig = 0.0;

        long matchedOrig = 0;
        int missingCount = 0;
        double sumDistOrigToRest = 0.0;

        for (int y = 0; y < rows; y++)
        {
            for (int x = 0; x < cols; x++)
            {
                if (edgesRest.At<byte>(y, x) > 0)
                {
                    var d = distOrig.At<float>(y, x);
                    sumDistRestToOrig += d;
                    if (d <= tolerance)
                    {
                        matchedRest++;
                    }
                    else
                    {
                        unexpectedCount++;
                        unexpectedMap.Set<byte>(y, x, 255);
                    }
                }

                if (edgesOrig.At<byte>(y, x) > 0)
                {
                    var d = distRest.At<float>(y, x);
                    sumDistOrigToRest += d;
                    if (d <= tolerance)
                    {
                        matchedOrig++;
                    }
                    else
                    {
                        missingCount++;
                        missingMap.Set<byte>(y, x, 255);
                    }
                }
            }
        }

        double precision = countRest > 0 ? (double)matchedRest / countRest : 1.0;
        double recall = countOrig > 0 ? (double)matchedOrig / countOrig : 1.0;
        double f1 = (precision + recall > 0) ? (2.0 * precision * recall / (precision + recall)) : 0.0;
        double meanDist = ((sumDistRestToOrig / countRest) + (sumDistOrigToRest / countOrig)) / 2.0;

        double unexpectedRatio = countRest > 0 ? (double)unexpectedCount / countRest : 0.0;
        double missingRatio = countOrig > 0 ? (double)missingCount / countOrig : 0.0;

        return new TolerantEdgeResult
        {
            Precision = precision,
            Recall = recall,
            F1 = f1,
            MeanDist = meanDist,
            UnexpectedEdgeRatio = unexpectedRatio,
            MissingEdgeRatio = missingRatio,
            UnexpectedEdgePixelCount = unexpectedCount,
            MissingEdgePixelCount = missingCount,
            UnexpectedEdgeMap = unexpectedMap,
            MissingEdgeMap = missingMap
        };
    }

    internal static (int ClusterCount, int LargestPixels, double LargestRatio, Mat ClusterMap) AnalyzeUnexpectedEdgeClusters(
        Mat unexpectedEdgeMap,
        int totalRestoredEdges,
        int minClusterPixels)
    {
        using var labels = new Mat();
        using var stats = new Mat();
        using var centroids = new Mat();

        var numLabels = Cv2.ConnectedComponentsWithStats(unexpectedEdgeMap, labels, stats, centroids, PixelConnectivity.Connectivity8);
        var clusterCount = 0;
        var largestPixels = 0;
        var clusterMap = new Mat(unexpectedEdgeMap.Rows, unexpectedEdgeMap.Cols, MatType.CV_8UC1, new Scalar(0));

        if (numLabels > 1)
        {
            var isMeaningful = new bool[numLabels];
            for (int i = 1; i < numLabels; i++)
            {
                var area = stats.At<int>(i, (int)ConnectedComponentsTypes.Area);
                if (area >= minClusterPixels)
                {
                    clusterCount++;
                    isMeaningful[i] = true;
                    if (area > largestPixels)
                    {
                        largestPixels = area;
                    }
                }
            }

            if (clusterCount > 0)
            {
                int mapRows = unexpectedEdgeMap.Rows;
                int mapCols = unexpectedEdgeMap.Cols;
                for (int y = 0; y < mapRows; y++)
                {
                    for (int x = 0; x < mapCols; x++)
                    {
                        if (unexpectedEdgeMap.At<byte>(y, x) > 0)
                        {
                            var lbl = labels.At<int>(y, x);
                            if (lbl > 0 && isMeaningful[lbl])
                            {
                                clusterMap.Set<byte>(y, x, 255);
                            }
                        }
                    }
                }
            }
        }

        var largestRatio = totalRestoredEdges > 0 ? (double)largestPixels / totalRestoredEdges : 0.0;
        return (clusterCount, largestPixels, largestRatio, clusterMap);
    }

    internal static bool IsProtectedOrHardwareComponent(FurnitureComponent comp)
    {
        if (comp.Importance?.Equals("protected", StringComparison.OrdinalIgnoreCase) == true) return true;
        var type = (comp.Type ?? "").ToLowerInvariant();
        var id = (comp.Id ?? "").ToLowerInvariant();
        return type.Contains("hinge") || type.Contains("strut") || type.Contains("handle") ||
               type.Contains("door") || type.Contains("drawer") || type.Contains("rail") ||
               type.Contains("glass") || type.Contains("panel") || type.Contains("shelf") ||
               id.Contains("hinge") || id.Contains("strut") || id.Contains("handle") ||
               id.Contains("door") || id.Contains("drawer");
    }

    internal static List<ComponentValidationResult> EvaluateComponentLevel(
        Mat origMat,
        Mat alignedMat,
        Mat origGray,
        Mat alignedGray,
        Mat origCoarseEdges,
        Mat restCoarseEdges,
        Mat origLab,
        Mat alignedLab,
        Mat diffBinary,
        Mat edgeAlignedChanges,
        Mat interiorChanges,
        Mat notEdgeDilated,
        FurnitureAnalysis analysis,
        double effectiveTolerance,
        int minClusterPixels,
        bool photographicEdgeEnhancementDetected,
        List<ValidationEvidence> evidences,
        ref double maxInteriorChange)
    {
        var results = new List<ComponentValidationResult>();

        foreach (var comp in analysis.Components)
        {
            if (comp.Region == null) continue;
            var reg = comp.Region;
            if (reg.Width <= 0 || reg.Height <= 0) continue;
            if (reg.X < 0 || reg.Y < 0 || reg.X >= 1.0 || reg.Y >= 1.0) continue;

            int x = Math.Clamp((int)(reg.X * origMat.Width), 0, origMat.Width - 1);
            int y = Math.Clamp((int)(reg.Y * origMat.Height), 0, origMat.Height - 1);
            int w = Math.Clamp((int)(reg.Width * origMat.Width), 1, origMat.Width - x);
            int h = Math.Clamp((int)(reg.Height * origMat.Height), 1, origMat.Height - y);

            if (w < 6 || h < 6) continue;

            var rect = new Rect(x, y, w, h);
            using var subOrigGray = new Mat(origGray, rect);
            using var subAlignedGray = new Mat(alignedGray, rect);
            using var subOrigEdges = new Mat(origCoarseEdges, rect);
            using var subRestEdges = new Mat(restCoarseEdges, rect);
            using var subOrigLab = new Mat(origLab, rect);
            using var subAlignedLab = new Mat(alignedLab, rect);
            using var subDiff = new Mat(diffBinary, rect);
            using var subEdgeAligned = new Mat(edgeAlignedChanges, rect);
            using var subInterior = new Mat(interiorChanges, rect);
            using var subNotEdgeDilated = new Mat(notEdgeDilated, rect);

            var compSsim = OpenCvPhotometricMetrics.CalculateSsim(subOrigGray, subAlignedGray);
            using var compEdgeRes = CalculateTolerantEdgeAgreement(subOrigEdges, subRestEdges, effectiveTolerance);

            // Part 6: Unexpected Edge Cluster in Interior (away from original edges)
            using var interiorUnexpectedEdges = new Mat();
            Cv2.BitwiseAnd(compEdgeRes.UnexpectedEdgeMap, subNotEdgeDilated, interiorUnexpectedEdges);

            var subRestEdgeCount = Cv2.CountNonZero(subRestEdges);
            var compMinCluster = Math.Max(10, minClusterPixels);
            var (clusterCount, largestPixels, largestRatio, compClusterMap) =
                AnalyzeUnexpectedEdgeClusters(interiorUnexpectedEdges, subRestEdgeCount, compMinCluster);
            compClusterMap.Dispose();

            var (_, normColorDiff) = OpenCvPhotometricMetrics.CalculateColorMetrics(subOrigLab, subAlignedLab);
            var (lumDelta, chromaDelta, chromaticDist) = OpenCvPhotometricMetrics.CalculateLabColorShift(subOrigLab, subAlignedLab);

            var totalSubPixels = w * h;
            var totalChangedPixels = Cv2.CountNonZero(subDiff);
            var totalChangedRatio = (double)totalChangedPixels / totalSubPixels;
            var edgeAlignedPixels = Cv2.CountNonZero(subEdgeAligned);
            var edgeAlignedRatio = (double)edgeAlignedPixels / totalSubPixels;
            var interiorPixels = Cv2.CountNonZero(subInterior);
            var interiorRatio = (double)interiorPixels / totalSubPixels;

            var isProtected = IsProtectedOrHardwareComponent(comp);
            var importance = comp.Importance ?? (isProtected ? "high" : "normal");

            var compEvidences = new List<ValidationEvidence>();
            var decisionHint = "Pass";

            var isGlass = comp.Type.Contains("glass", StringComparison.OrdinalIgnoreCase) ||
                          (comp.Material ?? "").Contains("glass", StringComparison.OrdinalIgnoreCase);

            var compChangedAreaThreshold = isGlass ? 0.35 : 0.05;

            // Micro-region size awareness: require at least 20 interior pixels or 15% if region is smaller
            var minInteriorPixels = Math.Min(20, Math.Max(5, (int)(totalSubPixels * 0.15)));
            var isSignificantInterior = interiorRatio > compChangedAreaThreshold && interiorPixels >= minInteriorPixels;

            // Micro-region & edge-corroboration awareness (Phase 4E.3 Tier Separation):
            // In micro-regions where original edges are highly preserved (Recall >= 0.85)
            // and chromatic color identity is intact (Delta C <= 8.0), boundary edge changes
            // reflect photographic sharpening; emit Warning (Tier B / NeedsReview), not Critical.
            var isPhotographicLuminanceShift = chromaticDist <= 8.0;
            var isPhotoEdgeGain = photographicEdgeEnhancementDetected || evidences.Any(e => e.Type == "EdgeAlignedPhotometricChange" && e.Passed);
            var isPhotographicEnhancement = isPhotoEdgeGain &&
                                            isPhotographicLuminanceShift &&
                                            (compEdgeRes.Recall >= 0.85 || Cv2.CountNonZero(subOrigEdges) <= 15);

            var hasNewStructure = clusterCount > 0 && largestPixels >= compMinCluster && largestRatio >= 0.05 && interiorPixels >= minInteriorPixels;
            var hasMissingStructure = compEdgeRes.MissingEdgeRatio > 0.40 && Cv2.CountNonZero(subOrigEdges) > 15;

            var isCorroboratedPhysicalAddition = !isPhotographicEnhancement &&
                                                 (largestPixels >= 60 ||
                                                  interiorPixels >= 80 ||
                                                  (compEdgeRes.Recall < 0.85 && Cv2.CountNonZero(subOrigEdges) > 15) ||
                                                  (interiorRatio > edgeAlignedRatio && totalSubPixels >= 3000));

            if (isProtected && !isGlass && ((chromaticDist > 8.0 && interiorRatio > compChangedAreaThreshold) || (hasNewStructure && isCorroboratedPhysicalAddition) || hasMissingStructure))
            {
                if (interiorRatio > maxInteriorChange)
                    maxInteriorChange = interiorRatio;
            }

            if (hasNewStructure)
            {
                var severity = (isProtected && isCorroboratedPhysicalAddition)
                    ? ValidationSeverity.Critical
                    : ValidationSeverity.Warning;
                decisionHint = "NeedsReview";

                var msg = isProtected
                    ? (severity == ValidationSeverity.Critical
                        ? $"Added structure detected inside protected component: {comp.Id} ({largestPixels} px cluster, {largestRatio:P1} of component edges)."
                        : $"Possible added detail detected inside protected component: {comp.Id} ({largestPixels} px cluster, {largestRatio:P1} of component edges). Requires human review.")
                    : $"Unexpected localized structural cluster detected in component: {comp.Id} ({largestPixels} px cluster).";

                var ev = new ValidationEvidence
                {
                    Category = comp.IsStructural ? ValidationEvidenceCategory.Structural : ValidationEvidenceCategory.Component,
                    Type = "ComponentStructureAdded",
                    Score = largestRatio,
                    Threshold = 0.05,
                    Severity = severity,
                    Passed = false,
                    Region = comp.Id,
                    Message = msg
                };
                compEvidences.Add(ev);
                evidences.Add(ev);
            }

            if (hasMissingStructure)
            {
                var severity = isProtected ? ValidationSeverity.Critical : ValidationSeverity.Warning;
                if (decisionHint == "Pass") decisionHint = "NeedsReview";

                var ev = new ValidationEvidence
                {
                    Category = comp.IsStructural ? ValidationEvidenceCategory.Structural : ValidationEvidenceCategory.Component,
                    Type = "ComponentStructureRemoved",
                    Score = compEdgeRes.MissingEdgeRatio,
                    Threshold = 0.40,
                    Severity = severity,
                    Passed = false,
                    Region = comp.Id,
                    Message = $"Original structural edges missing from component: {comp.Id} ({compEdgeRes.MissingEdgeRatio:P1} missing)."
                };
                compEvidences.Add(ev);
                evidences.Add(ev);
            }

            // Parts 4, 8 & 9: Interior Area Alteration vs. Chromatic Recoloring vs. Photometric Luminance Change
            if (isSignificantInterior)
            {
                if (decisionHint == "Pass") decisionHint = "NeedsReview";

                if (chromaticDist > 8.0)
                {
                    var severity = isProtected ? ValidationSeverity.Critical : ValidationSeverity.Warning;
                    var ev = new ValidationEvidence
                    {
                        Category = ValidationEvidenceCategory.Component,
                        Type = "ComponentColorIdentityChanged",
                        Score = chromaticDist,
                        Threshold = 8.0,
                        Severity = severity,
                        Passed = false,
                        Region = comp.Id,
                        Message = $"Component '{comp.Id}' exhibited chromatic color shift (Delta C = {chromaticDist:F1} > 8.0, interior changed {interiorRatio:P1}). Possible recoloring."
                    };
                    compEvidences.Add(ev);
                    evidences.Add(ev);
                }
                else if (!hasNewStructure)
                {
                    var ev = new ValidationEvidence
                    {
                        Category = comp.IsStructural ? ValidationEvidenceCategory.Structural : ValidationEvidenceCategory.Component,
                        Type = "PhotometricLuminanceChange",
                        Score = interiorRatio,
                        Threshold = compChangedAreaThreshold,
                        Severity = ValidationSeverity.Warning,
                        Passed = false,
                        Region = comp.Id,
                        Message = $"Component '{comp.Id}' interior luminance changed ({interiorRatio:P1} > {compChangedAreaThreshold:P1}, {interiorPixels} interior px, chromatic Delta C = {chromaticDist:F1} <= 8.0)."
                    };
                    compEvidences.Add(ev);
                    evidences.Add(ev);
                }
            }
            else if (totalChangedRatio > compChangedAreaThreshold && !hasNewStructure && !hasMissingStructure && compEdgeRes.Recall >= 0.85)
            {
                // Parts 3 & 5: Edge-Aligned Photometric Enhancement
                var ev = new ValidationEvidence
                {
                    Category = ValidationEvidenceCategory.Component,
                    Type = "EdgeAlignedPhotometricChange",
                    Score = edgeAlignedRatio,
                    Threshold = compChangedAreaThreshold,
                    Severity = ValidationSeverity.Info,
                    Passed = true,
                    Region = comp.Id,
                    Message = $"Component '{comp.Id}' exhibited photographic edge enhancement ({edgeAlignedRatio:P1} edge-aligned, {interiorRatio:P1} interior). Physical geometry preserved."
                };
                compEvidences.Add(ev);
                evidences.Add(ev);
            }

            results.Add(new ComponentValidationResult
            {
                ComponentId = comp.Id,
                ComponentType = comp.Type,
                Region = comp.Region,
                IsStructural = comp.IsStructural,
                Importance = importance,
                StructuralSimilarity = compSsim,
                EdgePrecision = compEdgeRes.Precision,
                EdgeRecall = compEdgeRes.Recall,
                EdgeF1 = compEdgeRes.F1,
                UnexpectedEdgeRatio = compEdgeRes.UnexpectedEdgeRatio,
                MissingEdgeRatio = compEdgeRes.MissingEdgeRatio,
                UnexpectedEdgeClusterCount = clusterCount,
                LargestUnexpectedEdgeClusterPixels = largestPixels,
                LargestUnexpectedEdgeClusterRatio = largestRatio,
                NormalizedColorDifference = normColorDiff,
                ChangedAreaPercentage = totalChangedRatio,
                DecisionHint = decisionHint,
                Evidences = compEvidences,
                TotalChangedAreaRatio = totalChangedRatio,
                EdgeAlignedChangedAreaRatio = edgeAlignedRatio,
                InteriorChangedAreaRatio = interiorRatio,
                TotalPixels = totalSubPixels,
                TotalChangedPixels = totalChangedPixels,
                EdgeAlignedChangedPixels = edgeAlignedPixels,
                InteriorChangedPixels = interiorPixels,
                ColorIdentityDelta = chromaticDist,
                LuminanceDelta = lumDelta,
                ChromaDelta = chromaDelta,
                StructuralBoundaryShift = compEdgeRes.MeanDist
            });
        }

        return results;
    }

    internal static void DrawComponentRegions(Mat mat, List<ComponentValidationResult> compResults)
    {
        foreach (var comp in compResults)
        {
            if (comp.Region == null) continue;
            var x = (int)(comp.Region.X * mat.Width);
            var y = (int)(comp.Region.Y * mat.Height);
            var w = (int)(comp.Region.Width * mat.Width);
            var h = (int)(comp.Region.Height * mat.Height);

            var color = comp.DecisionHint switch
            {
                "Reject" => new Scalar(0, 0, 255), // Red
                "NeedsReview" => new Scalar(0, 165, 255), // Orange
                _ => new Scalar(0, 255, 0) // Green
            };

            Cv2.Rectangle(mat, new Rect(x, y, w, h), color, 2);
            var label = $"{comp.ComponentId} ({comp.DecisionHint})";
            Cv2.PutText(mat, label, new Point(x + 4, Math.Max(16, y + 16)), HersheyFonts.HersheySimplex, 0.45, color, 1);
        }
    }
}
