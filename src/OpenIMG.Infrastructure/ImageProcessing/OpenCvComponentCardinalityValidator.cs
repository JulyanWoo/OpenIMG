using OpenCvSharp;
using OpenIMG.Application.Abstractions.Validation;
using OpenIMG.Core.Enums;
using OpenIMG.Core.Models;

namespace OpenIMG.Infrastructure.ImageProcessing;

public sealed class OpenCvComponentCardinalityValidator : IComponentCardinalityValidator
{
    private const double ExpansionMarginRatio = 0.05;

    public IReadOnlyList<ValidationEvidence> Validate(
        string originalPath,
        string restoredPath,
        IReadOnlyList<ComponentCardinalityConstraint> constraints)
    {
        return Validate(originalPath, restoredPath, constraints, null, null);
    }

    public IReadOnlyList<ValidationEvidence> Validate(
        string originalPath,
        string restoredPath,
        IReadOnlyList<ComponentCardinalityConstraint> constraints,
        string? diagnosticOutputDir,
        string? assetBaseName)
    {
        var detailedResults = ValidateDetailed(originalPath, restoredPath, constraints, diagnosticOutputDir, assetBaseName);
        var evidences = new List<ValidationEvidence>();
        foreach (var res in detailedResults)
        {
            evidences.AddRange(res.Evidences);
        }
        return evidences;
    }

    public List<CardinalityValidationResult> ValidateDetailed(
        string originalPath,
        string restoredPath,
        IReadOnlyList<ComponentCardinalityConstraint> constraints,
        string? diagnosticOutputDir = null,
        string? assetBaseName = null)
    {
        var results = new List<CardinalityValidationResult>();
        if (!File.Exists(originalPath) || !File.Exists(restoredPath))
            return results;

        using var origMat = Cv2.ImRead(originalPath, ImreadModes.Grayscale);
        using var restMat = Cv2.ImRead(restoredPath, ImreadModes.Grayscale);
        if (origMat.Empty() || restMat.Empty())
            return results;

        foreach (var c in constraints.Where(v => v.Strict))
        {
            var res = ValidateSingleConstraint(origMat, restMat, c, diagnosticOutputDir, assetBaseName);
            results.Add(res);
        }

        return results;
    }

    private CardinalityValidationResult ValidateSingleConstraint(
        Mat origMat,
        Mat restMat,
        ComponentCardinalityConstraint c,
        string? diagnosticOutputDir,
        string? assetBaseName)
    {
        var res = new CardinalityValidationResult
        {
            ConstraintId = c.ConstraintId,
            Category = c.Category,
            OriginalObservedCount = c.DeduplicatedCount
        };

        var roiOrig = GetExpandedRoi(c.Region, origMat, ExpansionMarginRatio);
        var roiRest = GetExpandedRoi(c.Region, restMat, ExpansionMarginRatio);

        using var origSub = new Mat(origMat, roiOrig);
        using var restSub = new Mat(restMat, roiRest);

        // 1. Extract instances from original to serve as geometric template
        var origContours = ExtractContoursMultiScale(origSub, out _);
        var origFiltered = FilterFamilyContours(origContours, roiOrig.Width, roiOrig.Height, c.Category);
        var origInstances = FormInstances(origFiltered, origSub.Width, origSub.Height);
        origInstances = DeduplicateInstances(origInstances);
        res.OriginalCandidates = origInstances;

        // 2. Extract raw contours from restored at base and smoothed scales
        var restContours = ExtractContoursMultiScale(restSub, out var restSmoothContours);

        // 3. Filter restored contours by component family (reject seams, boundaries, woodgrain)
        var restFiltered = FilterFamilyContours(restContours, roiRest.Width, roiRest.Height, c.Category);

        // 4. Form candidate instances
        var candidateInstances = FormInstances(restFiltered, restSub.Width, restSub.Height);

        // 5. Multi-scale stability check
        EvaluateMultiScaleStability(candidateInstances, restSmoothContours);

        // 6. Better deduplication
        candidateInstances = DeduplicateInstances(candidateInstances);

        // 7. Original-Template Consistency
        EvaluateTemplateConsistency(candidateInstances, origInstances, roiRest.Width, roiRest.Height);

        // 8. Count Confidence Evaluation
        // Compute combined candidate confidence
        foreach (var inst in candidateInstances)
        {
            inst.Confidence = Math.Clamp(
                (0.4 * inst.Compactness + 0.6) * inst.GeometryCompatibility * inst.MultiScaleStability,
                0.1, 1.0);
        }

        // Determine confidence status
        var strongCandidates = candidateInstances.Where(i => i.Confidence >= 0.58).ToList();
        CardinalityConfidenceStatus status;
        int detectedCount;

        double avgConf = candidateInstances.Count > 0 ? candidateInstances.Average(i => i.Confidence) : 0.0;
        bool isCleanHighConfidence = strongCandidates.Count > 0 && avgConf >= 0.75 && candidateInstances.All(c => c.Confidence >= 0.55);

        if (candidateInstances.Count == 0 && c.DeduplicatedCount == 0)
        {
            status = CardinalityConfidenceStatus.Reliable;
            detectedCount = 0;
        }
        else if (candidateInstances.Count == 0 && c.DeduplicatedCount > 0)
        {
            status = CardinalityConfidenceStatus.Ambiguous;
            detectedCount = 0;
        }
        else if (isCleanHighConfidence)
        {
            status = CardinalityConfidenceStatus.Reliable;
            detectedCount = strongCandidates.Count;
        }
        else
        {
            // Real photographic data or complex noise: explicit Ambiguous rather than false certainty
            status = CardinalityConfidenceStatus.Ambiguous;
            detectedCount = strongCandidates.Count > 0 ? strongCandidates.Count : candidateInstances.Count;
        }

        res.Status = status;
        res.RestoredDetectedCount = detectedCount;
        res.RestoredCandidates = candidateInstances;
        res.AverageConfidence = candidateInstances.Count > 0
            ? candidateInstances.Average(i => i.Confidence)
            : 0.0;

        // Assign IDs to final instances for inspection
        for (int i = 0; i < candidateInstances.Count; i++)
        {
            candidateInstances[i].Id = i + 1;
        }

        // Generate diagnostic images if requested
        if (!string.IsNullOrWhiteSpace(diagnosticOutputDir))
        {
            GenerateDiagnostics(
                diagnosticOutputDir,
                assetBaseName ?? c.ConstraintId,
                restSub,
                origSub,
                restContours,
                restFiltered,
                candidateInstances,
                roiRest,
                res.DiagnosticImagePaths);
        }

        // Create validation evidence
        var expected = c.DeduplicatedCount;
        if (detectedCount != expected)
        {
            // If detector cannot distinguish instances: Status = Ambiguous, and cardinality evidence must NOT become Critical.
            ValidationSeverity severity;
            if (status == CardinalityConfidenceStatus.Ambiguous)
            {
                severity = ValidationSeverity.Warning;
            }
            else
            {
                severity = c.Confidence >= 0.8 ? ValidationSeverity.Critical : ValidationSeverity.Warning;
            }

            res.Evidences.Add(new ValidationEvidence
            {
                Category = ValidationEvidenceCategory.Component,
                Type = "ComponentCardinalityMismatch",
                Region = c.ConstraintId,
                Score = Math.Abs(detectedCount - expected),
                Threshold = 0,
                Severity = severity,
                Passed = false,
                Message = $"{c.Category}: observed {expected}, restored physical candidates {detectedCount} (Status: {status}); source IDs: {string.Join(',', c.SourceRegionIds)}."
            });
        }

        return res;
    }

    private static Rect GetExpandedRoi(ProtectedRegion p, Mat m, double expansionRatio)
    {
        int xMargin = (int)(p.Width * m.Width * expansionRatio);
        int yMargin = (int)(p.Height * m.Height * expansionRatio);

        int x = Math.Clamp((int)(p.X * m.Width) - xMargin, 0, m.Width - 1);
        int y = Math.Clamp((int)(p.Y * m.Height) - yMargin, 0, m.Height - 1);
        int w = Math.Max(1, Math.Min((int)(p.Width * m.Width) + 2 * xMargin, m.Width - x));
        int h = Math.Max(1, Math.Min((int)(p.Height * m.Height) + 2 * yMargin, m.Height - y));

        return new Rect(x, y, w, h);
    }

    private static Point[][] ExtractContoursMultiScale(Mat roi, out Point[][] smoothContours)
    {
        // Base edge map
        using var blurredBase = new Mat();
        Cv2.GaussianBlur(roi, blurredBase, new Size(3, 3), 0.8);
        using var edgeBase = new Mat();
        Cv2.Canny(blurredBase, edgeBase, 60, 140);
        Cv2.FindContours(edgeBase, out var baseContours, out _, RetrievalModes.CComp, ContourApproximationModes.ApproxSimple);

        // Slightly smoothed edge map for multi-scale stability check (using adaptive threshold for smoothed gradients)
        using var blurredSmooth = new Mat();
        Cv2.GaussianBlur(roi, blurredSmooth, new Size(3, 3), 1.2);
        using var edgeSmooth = new Mat();
        Cv2.Canny(blurredSmooth, edgeSmooth, 40, 100);
        Cv2.FindContours(edgeSmooth, out smoothContours, out _, RetrievalModes.CComp, ContourApproximationModes.ApproxSimple);

        return baseContours;
    }

    private static List<ContourInfo> FilterFamilyContours(
        Point[][] contours,
        int roiWidth,
        int roiHeight,
        string category)
    {
        var filtered = new List<ContourInfo>();
        double roiArea = roiWidth * roiHeight;

        for (int i = 0; i < contours.Length; i++)
        {
            var pts = contours[i];
            if (pts.Length < 3) continue;

            var rect = Cv2.BoundingRect(pts);
            var area = Cv2.ContourArea(pts);
            var perimeter = Cv2.ArcLength(pts, true);

            // Basic area / dimension threshold: reject tiny texture edges and isolated Canny fragments
            if (rect.Width < 4 || rect.Height < 4) continue;
            if (rect.Width * rect.Height < 60 && area < 30) continue;

            // Reject panel seams and drawer edges spanning substantial portions of the ROI
            if (roiWidth >= 100 && rect.Width >= 0.35 * roiWidth && rect.Height <= 0.12 * roiHeight) continue;
            if (roiHeight >= 100 && rect.Height >= 0.35 * roiHeight && rect.Width <= 0.12 * roiWidth) continue;

            // Reject large carcase boundaries or cavity outlines
            if (rect.Width * rect.Height >= 0.20 * roiArea) continue;

            // Reject long thin panel seams (e.g. aspect > 8 with thin thickness)
            double aspect = Math.Max(rect.Width / (double)rect.Height, rect.Height / (double)rect.Width);
            if (aspect > 7.0 && Math.Min(rect.Width, rect.Height) <= 10) continue;
            if (aspect > 12.0) continue; // single long panel seam

            // Reject boundary cut artifacts flush with ROI borders
            if (roiHeight >= 100 && (rect.X <= 2 || rect.Right >= roiWidth - 2) && rect.Height > 0.25 * roiHeight) continue;
            if (roiWidth >= 100 && (rect.Y <= 2 || rect.Bottom >= roiHeight - 2) && rect.Width > 0.25 * roiWidth) continue;

            // Reject irregular woodgrain / texture noise (long perimeter but negligible enclosed area)
            if (perimeter > 60 && area / (rect.Width * rect.Height) < 0.03 && aspect < 3.5) continue;

            filtered.Add(new ContourInfo
            {
                Id = i,
                Points = pts,
                Bounds = rect,
                Area = area,
                Perimeter = perimeter,
                Aspect = aspect
            });
        }

        return filtered;
    }

    private static List<CardinalityInstanceCandidate> FormInstances(
        List<ContourInfo> contours,
        int roiWidth,
        int roiHeight)
    {
        if (contours.Count == 0) return new List<CardinalityInstanceCandidate>();

        // Disjoint-set grouping
        int n = contours.Count;
        var parent = new int[n];
        for (int i = 0; i < n; i++) parent[i] = i;

        int Find(int i) => parent[i] == i ? i : (parent[i] = Find(parent[i]));
        void Union(int i, int j)
        {
            int rootI = Find(i);
            int rootJ = Find(j);
            if (rootI != rootJ) parent[rootI] = rootJ;
        }

        for (int i = 0; i < n; i++)
        {
            for (int j = i + 1; j < n; j++)
            {
                if (ShouldGroupContours(contours[i], contours[j]))
                {
                    Union(i, j);
                }
            }
        }

        var groups = new Dictionary<int, List<ContourInfo>>();
        for (int i = 0; i < n; i++)
        {
            int root = Find(i);
            if (!groups.ContainsKey(root)) groups[root] = new List<ContourInfo>();
            groups[root].Add(contours[i]);
        }

        var instances = new List<CardinalityInstanceCandidate>();
        foreach (var grp in groups.Values)
        {
            var allPoints = grp.SelectMany(c => c.Points).ToList();
            var minX = grp.Min(c => c.Bounds.X);
            var minY = grp.Min(c => c.Bounds.Y);
            var maxX = grp.Max(c => c.Bounds.Right);
            var maxY = grp.Max(c => c.Bounds.Bottom);
            var bounds = new Rect(minX, minY, maxX - minX, maxY - minY);

            // Reject if merged envelope is a giant carcase cavity
            if (bounds.Width * bounds.Height >= 0.25 * (roiWidth * roiHeight)) continue;
            if (roiWidth >= 100 && bounds.Width >= 0.60 * roiWidth && bounds.Height <= 0.15 * roiHeight) continue;
            if (roiWidth >= 100 && bounds.Width * bounds.Height < 75) continue;

            double totalContourArea = grp.Sum(c => Math.Max(c.Area, c.Bounds.Width * c.Bounds.Height * 0.25));
            double boundsArea = Math.Max(1, bounds.Width * bounds.Height);
            double compactness = Math.Clamp(totalContourArea / boundsArea, 0.05, 1.0);
            double aspect = Math.Max(bounds.Width / (double)bounds.Height, bounds.Height / (double)bounds.Width);

            var moments = Cv2.Moments(allPoints);
            var centroid = moments.M00 != 0
                ? new Point2d(moments.M10 / moments.M00, moments.M01 / moments.M00)
                : new Point2d(bounds.X + bounds.Width * 0.5, bounds.Y + bounds.Height * 0.5);

            double orientation = 0.0;
            if (allPoints.Count >= 5)
            {
                var minRect = Cv2.MinAreaRect(allPoints);
                orientation = minRect.Angle;
            }

            instances.Add(new CardinalityInstanceCandidate
            {
                Bounds = bounds,
                Centroid = centroid,
                Orientation = orientation,
                AspectRatio = aspect,
                Elongation = aspect,
                Area = boundsArea,
                Compactness = compactness,
                Confidence = 0.80,
                SourceContourIds = grp.Select(c => c.Id).ToList(),
                Points = allPoints
            });
        }

        return instances;
    }

    private static bool ShouldGroupContours(ContourInfo a, ContourInfo b)
    {
        var rA = a.Bounds;
        var rB = b.Bounds;

        // 1. Spatial overlap or containment
        var interX = Math.Max(rA.X, rB.X);
        var interY = Math.Max(rA.Y, rB.Y);
        var interR = Math.Min(rA.Right, rB.Right);
        var interB = Math.Min(rA.Bottom, rB.Bottom);
        var interW = Math.Max(0, interR - interX);
        var interH = Math.Max(0, interB - interY);
        var interArea = interW * interH;

        if (interArea > 0)
        {
            double minArea = Math.Min(rA.Width * rA.Height, rB.Width * rB.Height);
            if (minArea > 0 && interArea / minArea >= 0.25) return true;
        }

        // 2. Parallel lines / edges of the same elongated hardware component:
        // Top and bottom edges of a horizontal component overlap in X and are close in Y.
        bool aIsHoriz = rA.Width >= rA.Height;
        bool bIsHoriz = rB.Width >= rB.Height;

        if (aIsHoriz && bIsHoriz)
        {
            // Must have substantial X overlap
            double overlapX = Math.Max(0, Math.Min(rA.Right, rB.Right) - Math.Max(rA.X, rB.X));
            double minW = Math.Min(rA.Width, rB.Width);
            if (minW > 0 && overlapX / minW >= 0.50)
            {
                // And small Y separation (thickness of the physical component)
                double gapY = Math.Max(0, Math.Max(rA.Y, rB.Y) - Math.Min(rA.Bottom, rB.Bottom));
                if (gapY <= Math.Max(12, Math.Min(rA.Height, rB.Height) * 2))
                {
                    return true;
                }
            }
        }
        else if (!aIsHoriz && !bIsHoriz)
        {
            // Vertical parallel edges: must have substantial Y overlap
            double overlapY = Math.Max(0, Math.Min(rA.Bottom, rB.Bottom) - Math.Max(rA.Y, rB.Y));
            double minH = Math.Min(rA.Height, rB.Height);
            if (minH > 0 && overlapY / minH >= 0.50)
            {
                // And small X separation
                double gapX = Math.Max(0, Math.Max(rA.X, rB.X) - Math.Min(rA.Right, rB.Right));
                if (gapX <= Math.Max(12, Math.Min(rA.Width, rB.Width) * 2))
                {
                    return true;
                }
            }
        }

        // 3. Containment with small padding
        var paddedA = new Rect(rA.X - 2, rA.Y - 2, rA.Width + 4, rA.Height + 4);
        if (paddedA.Contains(rB) || rB.Contains(rA)) return true;

        return false;
    }

    private static void EvaluateMultiScaleStability(
        List<CardinalityInstanceCandidate> instances,
        Point[][] smoothContours)
    {
        var smoothRects = smoothContours.Select(Cv2.BoundingRect).ToList();
        foreach (var inst in instances)
        {
            bool hasMatch = smoothRects.Any(sr =>
            {
                var interX = Math.Max(inst.Bounds.X, sr.X);
                var interY = Math.Max(inst.Bounds.Y, sr.Y);
                var interR = Math.Min(inst.Bounds.Right, sr.Right);
                var interB = Math.Min(inst.Bounds.Bottom, sr.Bottom);
                var interW = Math.Max(0, interR - interX);
                var interH = Math.Max(0, interB - interY);
                var interArea = interW * interH;
                if (interArea <= 0)
                {
                    var srCx = sr.X + sr.Width * 0.5;
                    var srCy = sr.Y + sr.Height * 0.5;
                    var dist = Math.Sqrt(Math.Pow(inst.Centroid.X - srCx, 2) + Math.Pow(inst.Centroid.Y - srCy, 2));
                    return dist <= 12;
                }

                var unionArea = (inst.Bounds.Width * inst.Bounds.Height) + (sr.Width * sr.Height) - interArea;
                var iou = unionArea > 0 ? interArea / (double)unionArea : 0;
                return iou >= 0.15;
            });

            inst.MultiScaleStability = hasMatch ? 1.0 : 0.50;
        }
    }

    private static List<CardinalityInstanceCandidate> DeduplicateInstances(
        List<CardinalityInstanceCandidate> instances)
    {
        var list = instances.OrderByDescending(i => i.Area).ToList();
        bool mergedAny;
        do
        {
            mergedAny = false;
            for (int i = 0; i < list.Count; i++)
            {
                for (int j = i + 1; j < list.Count; j++)
                {
                    if (ShouldDeduplicateInstances(list[i], list[j]))
                    {
                        // Merge j into i
                        var minX = Math.Min(list[i].Bounds.X, list[j].Bounds.X);
                        var minY = Math.Min(list[i].Bounds.Y, list[j].Bounds.Y);
                        var maxX = Math.Max(list[i].Bounds.Right, list[j].Bounds.Right);
                        var maxY = Math.Max(list[i].Bounds.Bottom, list[j].Bounds.Bottom);
                        list[i].Bounds = new Rect(minX, minY, maxX - minX, maxY - minY);
                        list[i].Area = list[i].Bounds.Width * list[i].Bounds.Height;
                        list[i].SourceContourIds.AddRange(list[j].SourceContourIds);
                        list[i].MultiScaleStability = Math.Max(list[i].MultiScaleStability, list[j].MultiScaleStability);
                        list[i].Compactness = (list[i].Compactness + list[j].Compactness) * 0.5;

                        list.RemoveAt(j);
                        mergedAny = true;
                        break;
                    }
                }
                if (mergedAny) break;
            }
        } while (mergedAny);

        return list;
    }

    private static bool ShouldDeduplicateInstances(
        CardinalityInstanceCandidate a,
        CardinalityInstanceCandidate b)
    {
        var rA = a.Bounds;
        var rB = b.Bounds;

        var interX = Math.Max(rA.X, rB.X);
        var interY = Math.Max(rA.Y, rB.Y);
        var interR = Math.Min(rA.Right, rB.Right);
        var interB = Math.Min(rA.Bottom, rB.Bottom);
        var interW = Math.Max(0, interR - interX);
        var interH = Math.Max(0, interB - interY);
        var interArea = interW * interH;

        if (interArea > 0)
        {
            var unionArea = (rA.Width * rA.Height) + (rB.Width * rB.Height) - interArea;
            var iou = unionArea > 0 ? interArea / (double)unionArea : 0;
            if (iou >= 0.35) return true;

            var minArea = Math.Min(rA.Width * rA.Height, rB.Width * rB.Height);
            if (minArea > 0 && interArea / (double)minArea >= 0.70) return true;
        }

        return false;
    }

    private static void EvaluateTemplateConsistency(
        List<CardinalityInstanceCandidate> restoredInstances,
        List<CardinalityInstanceCandidate> origInstances,
        int roiWidth,
        int roiHeight)
    {
        if (origInstances.Count == 0)
        {
            foreach (var inst in restoredInstances)
            {
                // Rely on generic elongated hardware prior
                inst.GeometryCompatibility = inst.AspectRatio >= 2.0 ? 0.85 : 0.65;
            }
            return;
        }

        foreach (var inst in restoredInstances)
        {
            double bestMatch = 0.0;
            foreach (var tmpl in origInstances)
            {
                // Compare aspect ratio
                double aspectSim = Math.Min(inst.AspectRatio, tmpl.AspectRatio) / Math.Max(inst.AspectRatio, tmpl.AspectRatio);

                // Compare relative size within ROI (reject tiny specks compared to physical template)
                double sizeRatio = Math.Min(inst.Area, tmpl.Area) / Math.Max(inst.Area, tmpl.Area);
                double sizeSim = sizeRatio < 0.12 ? 0.0 : Math.Clamp(sizeRatio * 1.6, 0.0, 1.0);

                // Compare orientation alignment (horizontal vs vertical vs angle)
                bool instHoriz = inst.Bounds.Width >= inst.Bounds.Height;
                bool tmplHoriz = tmpl.Bounds.Width >= tmpl.Bounds.Height;
                double orientSim = (instHoriz == tmplHoriz) ? 1.0 : 0.20;

                // Compare compactness
                double compactSim = Math.Min(inst.Compactness, tmpl.Compactness) / Math.Max(inst.Compactness, tmpl.Compactness);

                // Compare normalized position inside ROI
                double normDistX = Math.Abs(inst.Centroid.X - tmpl.Centroid.X) / Math.Max(1.0, roiWidth);
                double normDistY = Math.Abs(inst.Centroid.Y - tmpl.Centroid.Y) / Math.Max(1.0, roiHeight);
                double posSim = Math.Max(0.0, 1.0 - 1.5 * Math.Sqrt(normDistX * normDistX + normDistY * normDistY));

                double score = 0.30 * aspectSim + 0.25 * orientSim + 0.25 * sizeSim + 0.10 * compactSim + 0.10 * posSim;
                if (sizeSim == 0.0) score *= 0.45; // severely penalize candidates with mismatched scale
                if (score > bestMatch) bestMatch = score;
            }

            inst.GeometryCompatibility = Math.Clamp(bestMatch, 0.1, 1.0);
        }
    }

    private static void GenerateDiagnostics(
        string dir,
        string baseName,
        Mat restSub,
        Mat origSub,
        Point[][] rawContours,
        List<ContourInfo> filteredContours,
        List<CardinalityInstanceCandidate> instances,
        Rect roiRest,
        Dictionary<string, string> paths)
    {
        try
        {
            Directory.CreateDirectory(dir);

            // 1. Raw contours
            using var rawMat = new Mat();
            Cv2.CvtColor(restSub, rawMat, ColorConversionCodes.GRAY2BGR);
            for (int i = 0; i < rawContours.Length; i++)
            {
                Cv2.DrawContours(rawMat, rawContours, i, new Scalar(0, 0, 255), 1);
            }
            var rawPath = Path.Combine(dir, $"{baseName}_cardinality_raw_contours.png");
            Cv2.ImWrite(rawPath, rawMat);
            Cv2.ImWrite(Path.Combine(dir, "cardinality_raw_contours.png"), rawMat);
            paths["cardinality_raw_contours"] = rawPath;

            // 2. Filtered candidate contours
            using var filterMat = new Mat();
            Cv2.CvtColor(restSub, filterMat, ColorConversionCodes.GRAY2BGR);
            foreach (var fc in filteredContours)
            {
                Cv2.DrawContours(filterMat, new[] { fc.Points }, 0, new Scalar(255, 255, 0), 2);
                Cv2.Rectangle(filterMat, fc.Bounds, new Scalar(0, 255, 255), 1);
            }
            var filterPath = Path.Combine(dir, $"{baseName}_cardinality_filtered_candidates.png");
            Cv2.ImWrite(filterPath, filterMat);
            Cv2.ImWrite(Path.Combine(dir, "cardinality_filtered_candidates.png"), filterMat);
            paths["cardinality_filtered_candidates"] = filterPath;

            // 3. Grouped final instances with ID, bounding box, confidence
            using var groupMat = new Mat();
            Cv2.CvtColor(restSub, groupMat, ColorConversionCodes.GRAY2BGR);
            foreach (var inst in instances)
            {
                var color = inst.Confidence >= 0.58 ? new Scalar(0, 255, 0) : new Scalar(0, 165, 255);
                Cv2.Rectangle(groupMat, inst.Bounds, color, 2);
                var label = $"#{inst.Id} ({inst.Confidence:P0})";
                var textPt = new Point(Math.Max(0, inst.Bounds.X), Math.Max(15, inst.Bounds.Y - 5));
                Cv2.PutText(groupMat, label, textPt, HersheyFonts.HersheySimplex, 0.45, new Scalar(0, 255, 255), 1);
            }
            var groupPath = Path.Combine(dir, $"{baseName}_cardinality_grouped_instances.png");
            Cv2.ImWrite(groupPath, groupMat);
            Cv2.ImWrite(Path.Combine(dir, "cardinality_grouped_instances.png"), groupMat);
            paths["cardinality_grouped_instances"] = groupPath;

            // 4. Constraint ROI visualization
            using var roiMat = new Mat();
            Cv2.CvtColor(restSub, roiMat, ColorConversionCodes.GRAY2BGR);
            Cv2.Rectangle(roiMat, new Rect(0, 0, restSub.Width, restSub.Height), new Scalar(255, 0, 0), 2);
            var roiPath = Path.Combine(dir, $"{baseName}_cardinality_constraint_roi.png");
            Cv2.ImWrite(roiPath, roiMat);
            Cv2.ImWrite(Path.Combine(dir, "cardinality_constraint_roi.png"), roiMat);
            paths["cardinality_constraint_roi"] = roiPath;
        }
        catch
        {
            // Diagnostic visualization failure must not disrupt validation
        }
    }

    private sealed class ContourInfo
    {
        public int Id { get; set; }
        public Point[] Points { get; set; } = Array.Empty<Point>();
        public Rect Bounds { get; set; }
        public double Area { get; set; }
        public double Perimeter { get; set; }
        public double Aspect { get; set; }
    }
}
