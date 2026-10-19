using System.IO;
using OpenCvSharp;
using OpenIMG.Core.Enums;
using OpenIMG.Core.Models;

namespace OpenIMG.Infrastructure.ImageProcessing;

public class OpenCvNegativeSpaceValidator
{
    public static List<ObservedNegativeSpaceRegion> DeriveNegativeSpaceRegions(
        Mat origMat,
        FurnitureAnalysis analysis)
    {
        var regions = new List<ObservedNegativeSpaceRegion>();
        if (origMat.Empty()) return regions;

        var w = origMat.Width;
        var h = origMat.Height;

        using var origGray = new Mat();
        Cv2.CvtColor(origMat, origGray, ColorConversionCodes.BGR2GRAY);

        using var edges = new Mat();
        Cv2.Canny(origGray, edges, 50, 150);

        // If analysis already has pre-defined negative space regions from local contract, use them
        if (analysis.NegativeSpaceRegions.Count > 0)
        {
            foreach (var r in analysis.NegativeSpaceRegions)
            {
                var rect = ToPixelRect(r.Region, w, h);
                using var roi = new Mat(edges, rect);
                var edgeCount = Cv2.CountNonZero(roi);
                var density = (double)edgeCount / (rect.Width * rect.Height);

                regions.Add(r with
                {
                    StructuralEdgeDensity = density
                });
            }
            return regions;
        }

        // Estimate overall furniture boundary from components or bounds
        var fBounds = EstimateFurnitureBounds(analysis);

        // 1. Top Margin (Wall/Air above furniture if furniture top > 0.12)
        if (fBounds.Y > 0.12)
        {
            var topNorm = new NormalizedRectangle(0.04, 0.02, 0.92, Math.Max(0.06, fBounds.Y - 0.04)).Clamp();
            AddCandidateRegion(regions, topNorm, "neg_space_top_wall", NegativeSpaceType.WallSurface, origGray, edges, analysis);
        }

        // 2. Bottom Margin (Floor/Wall below furniture if furniture bottom < 0.88)
        var fBottom = fBounds.Y + fBounds.Height;
        if (fBottom < 0.88)
        {
            var botNorm = new NormalizedRectangle(0.04, fBottom + 0.04, 0.92, Math.Max(0.06, 0.98 - (fBottom + 0.04))).Clamp();
            var spaceType = (botNorm.Y + botNorm.Height / 2 > 0.70) ? NegativeSpaceType.FloorSurface : NegativeSpaceType.WallSurface;
            AddCandidateRegion(regions, botNorm, "neg_space_bottom_surface", spaceType, origGray, edges, analysis);
        }

        // 3. Left Margin (if furniture left > 0.15)
        if (fBounds.X > 0.15)
        {
            var leftNorm = new NormalizedRectangle(0.02, 0.05, Math.Max(0.06, fBounds.X - 0.04), 0.90).Clamp();
            AddCandidateRegion(regions, leftNorm, "neg_space_left_flank", NegativeSpaceType.WallSurface, origGray, edges, analysis);
        }

        // 4. Right Margin (if furniture right < 0.85)
        var fRight = fBounds.X + fBounds.Width;
        if (fRight < 0.85)
        {
            var rightNorm = new NormalizedRectangle(fRight + 0.04, 0.05, Math.Max(0.06, 0.98 - (fRight + 0.04)), 0.90).Clamp();
            AddCandidateRegion(regions, rightNorm, "neg_space_right_flank", NegativeSpaceType.WallSurface, origGray, edges, analysis);
        }

        // 5. Protected Absence Regions from contract
        foreach (var absence in analysis.ProtectedAbsenceRegions)
        {
            var norm = NormalizedRectangle.FromProtectedRegion(absence.Region).Clamp();
            if (norm.Area > 0.01)
            {
                AddCandidateRegion(regions, norm, $"neg_space_absence_{absence.ConstraintId}",
                    NegativeSpaceType.OpenFurnitureVoid, origGray, edges, analysis, forceStrict: absence.Strict);
            }
        }

        return regions;
    }

    private static void AddCandidateRegion(
        List<ObservedNegativeSpaceRegion> list,
        NormalizedRectangle norm,
        string id,
        NegativeSpaceType spaceType,
        Mat origGray,
        Mat origEdges,
        FurnitureAnalysis analysis,
        bool forceStrict = false)
    {
        if (norm.Width < 0.08 || norm.Height < 0.06 || norm.Area < 0.015)
            return;

        var w = origGray.Width;
        var h = origGray.Height;
        var rect = ToPixelRect(norm, w, h);

        using var edgeRoi = new Mat(origEdges, rect);
        var edgeCount = Cv2.CountNonZero(edgeRoi);
        var density = (double)edgeCount / (rect.Width * rect.Height);

        using var grayRoi = new Mat(origGray, rect);
        Cv2.MeanStdDev(grayRoi, out _, out var stdDev);
        var textureVar = stdDev.Val0;

        // Check overlap with permanent structural components
        bool overlapsComponent = analysis.Components.Any(c =>
            c.Region != null &&
            c.Region.Importance >= 0.8 &&
            norm.CalculateIoU(NormalizedRectangle.FromProtectedRegion(c.Region)) > 0.10);

        if (overlapsComponent && !forceStrict)
            return; // Not negative space; occupied by component

        // Check overlap with temporary scene clutter
        bool overlapsClutter = analysis.SceneObjects.Any(s =>
            s.Precedence == RegionPrecedence.RemovableScene &&
            s.Region != null &&
            norm.CalculateIoU(NormalizedRectangle.FromProtectedRegion(s.Region)) > 0.05);

        var authority = ConstraintAuthority.Strict;
        if (overlapsClutter)
        {
            authority = ConstraintAuthority.Advisory; // Hidden behind clutter -> cannot be Strict absence
        }
        else if (density > 0.06 || textureVar > 45.0)
        {
            authority = ConstraintAuthority.Uncertain;
        }

        list.Add(new ObservedNegativeSpaceRegion
        {
            RegionId = id,
            Region = norm,
            Confidence = forceStrict ? 0.95 : (authority == ConstraintAuthority.Strict ? 0.88 : (authority == ConstraintAuthority.Advisory ? 0.65 : 0.40)),
            SpaceType = spaceType,
            StructuralEdgeDensity = density,
            TextureConsistency = textureVar,
            BoundaryConfidence = 0.90,
            ProtectionLevel = forceStrict ? ConstraintAuthority.Strict : authority,
            SourceEvidence = $"Source edge density={density:F3}, texture stddev={textureVar:F1}, clutterOverlap={overlapsClutter}"
        });
    }

    public static EvaluationResult Evaluate(
        Mat origMat,
        Mat alignedMat,
        Mat origGray,
        Mat alignedGray,
        Mat origCoarseEdges,
        Mat restCoarseEdges,
        Mat unexpectedCoarseEdgeMask,
        Mat origFineEdges,
        Mat restFineEdges,
        Mat unexpectedFineEdgeMask,
        RegistrationModel regModel,
        double regConfidence,
        FurnitureAnalysis analysis,
        string diagnosticsDirectory,
        string assetBaseName,
        bool photographicEdgeEnhancementDetected = false)
    {
        var violations = new List<ProtectedNegativeSpaceViolationResult>();
        var additions = new List<UnsupportedPhysicalAdditionResult>();
        var cleanupTargets = new List<CleanupTargetRecord>();
        var evidences = new List<ValidationEvidence>();

        var w = origMat.Width;
        var h = origMat.Height;

        // 1. Derive Negative Space Regions
        var negativeSpaceRegions = DeriveNegativeSpaceRegions(origMat, analysis);

        // 2. Validate Protected Negative Space Regions (Part 4)
        foreach (var neg in negativeSpaceRegions.Where(r => r.ProtectionLevel == ConstraintAuthority.Strict))
        {
            var rect = ToPixelRect(neg.Region, w, h);
            using var unexpCoarseRoi = new Mat(unexpectedCoarseEdgeMask, rect);
            using var unexpFineRoi = new Mat(unexpectedFineEdgeMask, rect);
            using var restCoarseRoi = new Mat(restCoarseEdges, rect);

            var unexpCoarseCount = Cv2.CountNonZero(unexpCoarseRoi);
            var unexpFineCount = Cv2.CountNonZero(unexpFineRoi);
            var restCoarseCount = Cv2.CountNonZero(restCoarseRoi);
            var totalPixels = rect.Width * rect.Height;

            var unexpCoarseDensity = (double)unexpCoarseCount / totalPixels;
            var unexpFineDensity = (double)unexpFineCount / totalPixels;
            var restCoarseDensity = (double)restCoarseCount / totalPixels;

            // Find connected clusters of unexpected coarse edges inside negative space
            using var labels = new Mat();
            using var stats = new Mat();
            using var centroids = new Mat();
            int nLabels = Cv2.ConnectedComponentsWithStats(unexpCoarseRoi, labels, stats, centroids, PixelConnectivity.Connectivity8);

            int largestClusterInRegion = 0;
            for (int i = 1; i < nLabels; i++)
            {
                int area = stats.At<int>(i, (int)ConnectedComponentsTypes.Area);
                if (area > largestClusterInRegion) largestClusterInRegion = area;
            }

            // Multiple signals required for negative space violation:
            // 1. Significant unexpected coarse/fine edge presence (not noise)
            // 2. Coherent cluster >= 35 pixels (rigid structure)
            // 3. Candidate coarse density substantially exceeds original
            bool isViolated = (unexpCoarseDensity >= 0.02 || (unexpCoarseCount >= 60 && largestClusterInRegion >= 35)) &&
                              restCoarseDensity > (neg.StructuralEdgeDensity + 0.025);

            if (isViolated)
            {
                var violation = new ProtectedNegativeSpaceViolationResult
                {
                    ViolationId = $"viol_{neg.RegionId}",
                    NegativeSpaceRegionId = neg.RegionId,
                    Region = neg.Region,
                    Confidence = 0.95,
                    OccupancyRatio = unexpCoarseDensity,
                    UnexpectedEdgePixels = unexpCoarseCount,
                    IsViolation = true,
                    Description = $"Protected negative space '{neg.RegionId}' ({neg.SpaceType}) violated by coherent physical structure (Coarse edges: {unexpCoarseCount}, Largest cluster: {largestClusterInRegion}px, Density: {unexpCoarseDensity:P1})."
                };
                violations.Add(violation);

                // If registration is reliable, emit Critical
                var isRegReliable = regModel != RegistrationModel.None && regConfidence >= 0.50;
                evidences.Add(new ValidationEvidence
                {
                    Category = ValidationEvidenceCategory.ProtectedRegion,
                    Type = "ProtectedNegativeSpaceViolation",
                    Score = unexpCoarseDensity,
                    Threshold = 0.02,
                    Severity = isRegReliable ? ValidationSeverity.Critical : ValidationSeverity.Warning,
                    Passed = false,
                    Region = neg.RegionId,
                    Message = violation.Description
                });
            }
        }

        // 3. Multi-Scale Unsupported Physical Addition Detection (Parts 5, 6, 7)
        // Find all connected clusters of unexpected edges across the entire image
        using var allLabels = new Mat();
        using var allStats = new Mat();
        using var allCentroids = new Mat();
        int totalClusters = Cv2.ConnectedComponentsWithStats(unexpectedCoarseEdgeMask, allLabels, allStats, allCentroids, PixelConnectivity.Connectivity8);

        for (int i = 1; i < totalClusters; i++)
        {
            int area = allStats.At<int>(i, (int)ConnectedComponentsTypes.Area);
            int left = allStats.At<int>(i, (int)ConnectedComponentsTypes.Left);
            int top = allStats.At<int>(i, (int)ConnectedComponentsTypes.Top);
            int width = allStats.At<int>(i, (int)ConnectedComponentsTypes.Width);
            int height = allStats.At<int>(i, (int)ConnectedComponentsTypes.Height);

            // Bounding box in normalized coords
            var normRect = new NormalizedRectangle((double)left / w, (double)top / h, (double)width / w, (double)height / h).Clamp();

            // Filter registration boundary slivers (often 1-4px along the very edge of the image canvas)
            bool isBorderSliver = (left <= 2 || top <= 2 || (left + width) >= w - 2 || (top + height) >= h - 2) &&
                                  (width <= 6 || height <= 6 || area < 60);
            if (isBorderSliver)
            {
                continue;
            }

            // Sparse edge web spanning the entire image caused by global sharpening / edge continuity across the entire room
            bool isGlobalSceneEdgeWeb = (normRect.Width >= 0.70 && normRect.Height >= 0.50) &&
                                        ((double)area / (width * height) < 0.05);
            if (isGlobalSceneEdgeWeb)
            {
                continue;
            }

            // Canvas boundary sliver exclusion (registration edge artifacts along outer boundary)
            bool isVerticalBorderSliver = (normRect.X <= 0.015 || normRect.Right >= 0.985) &&
                                          (width <= 25 || normRect.Width <= 0.030);
            bool isHorizontalBorderSliver = (normRect.Y <= 0.015 || normRect.Bottom >= 0.985) &&
                                            (height <= 25 || normRect.Height <= 0.030);
            bool isCanvasBorderSliver = isVerticalBorderSliver || isHorizontalBorderSliver ||
                                        ((normRect.X <= 0.015 || normRect.Y <= 0.015 || normRect.Right >= 0.985 || normRect.Bottom >= 0.985) && (width <= 8 || height <= 8 || area < 50));
            if (isCanvasBorderSliver)
            {
                continue;
            }

            // Exclude scene objects and temporary clutter (Part 10, Part 13):
            // Clutter that was not removed or changed is handled by CleanupIncomplete, not UnsupportedPhysicalAddition!
            bool overlapsSceneObject = analysis.SceneObjects.Any(s =>
                s.Region != null &&
                (normRect.CalculateIoU(NormalizedRectangle.FromProtectedRegion(s.Region)) > 0.10 ||
                 normRect.IntersectionOverSelf(NormalizedRectangle.FromProtectedRegion(s.Region)) > 0.40));

            if (overlapsSceneObject)
            {
                continue;
            }

            double clusterAspect = Math.Max((double)width / Math.Max(1, height), (double)height / Math.Max(1, width));
            bool isElongated = clusterAspect >= 1.8;
            bool isPlanarOrBroad = (width >= 50 && height >= 30) || (normRect.Area >= 0.020 && Math.Min(width, height) >= 20);

            // Check source support in padded box
            var paddedRect = PadPixelRect(new Rect(left, top, width, height), 8, w, h);
            using var origEdgeRoi = new Mat(origCoarseEdges, paddedRect);
            using var origFineRoi = new Mat(origFineEdges, paddedRect);

            var origCoarseCount = Cv2.CountNonZero(origEdgeRoi);
            var origFineCount = Cv2.CountNonZero(origFineRoi);
            var paddedArea = paddedRect.Width * paddedRect.Height;
            var origCoarseDensity = (double)origCoarseCount / paddedArea;
            var origFineDensity = (double)origFineCount / paddedArea;

            // Also check unpadded cluster bounding box to avoid diluting thin structures through padding
            using var unpaddedFineRoi = new Mat(origFineEdges, new Rect(left, top, width, height));
            var unpaddedFineCount = Cv2.CountNonZero(unpaddedFineRoi);
            var unpaddedFineDensity = (double)unpaddedFineCount / (width * height);

            using var unpaddedCoarseRoi = new Mat(origCoarseEdges, new Rect(left, top, width, height));
            var unpaddedCoarseCount = Cv2.CountNonZero(unpaddedCoarseRoi);
            var unpaddedCoarseDensity = (double)unpaddedCoarseCount / (width * height);

            // Check if cluster is simply the edges of an already-known existing structural component in the source image.
            // A cluster is an existing component if:
            // 1. High-confidence component (Confidence >= 0.70) with meaningful edge support in source (coarse or fine count >= 200)
            // 2. Or visual edge density in source confirms the component structure
            bool hasVisualEvidence = (unpaddedCoarseCount >= 8 && unpaddedCoarseDensity >= 0.008) ||
                                     (origFineDensity >= 0.040 && origFineCount >= 20) ||
                                     (unpaddedFineDensity >= 0.040 && unpaddedFineCount >= 20);

            bool isExistingComponent = analysis.Components.Any(c =>
                c.Region != null &&
                (c.Region.Width * c.Region.Height) <= 0.15 &&
                ((c.Confidence >= 0.70 && (unpaddedCoarseCount >= 5 || origFineCount >= 200) && normRect.CalculateIoU(NormalizedRectangle.FromProtectedRegion(c.Region)) > 0.20) ||
                 (hasVisualEvidence && unpaddedCoarseCount >= 4 && (normRect.CalculateIoU(NormalizedRectangle.FromProtectedRegion(c.Region)) > 0.25 ||
                                        normRect.IntersectionOverSelf(NormalizedRectangle.FromProtectedRegion(c.Region)) > 0.70))));

            if (isExistingComponent)
            {
                continue; // An existing structural component present in source; not an invented addition
            }

            // Check if source image already had visual support (coarse edges or fine edges) in this region
            // Note: Visual support requires meaningful edge density or significant coarse edge count in the actual cluster box.
            // Under photographic enhancement, fog/haze removal and sharpening expose subtle fine/coarse edges that existed in the source.
            bool hasCoarseSupport = (unpaddedCoarseDensity >= 0.020 && unpaddedCoarseCount >= 6) ||
                                    (origCoarseCount >= 10 && origCoarseDensity >= 0.008 && unpaddedCoarseCount >= 4) ||
                                    (origCoarseCount >= 5 && unpaddedFineCount >= 50) ||
                                    (photographicEdgeEnhancementDetected && origCoarseCount >= 20 && unpaddedCoarseCount >= 4 &&
                                     (origCoarseDensity >= 0.005 || (double)origCoarseCount / (width * height) >= 0.010));

            // A. High-density fine edge support in source (fine structures, wires, thin hardware, or dense texture)
            bool hasFineSupport = origFineDensity >= 0.050 ||
                                  unpaddedFineDensity >= 0.050 ||
                                  origFineCount >= 180 ||
                                  unpaddedFineCount >= 140 ||
                                  (photographicEdgeEnhancementDetected &&
                                   ((unpaddedFineDensity >= 0.030 && unpaddedFineCount >= 15 && (unpaddedCoarseCount >= 1 || !isElongated)) ||
                                    (unpaddedFineCount >= 140)));

            bool hasSourceSupport = hasCoarseSupport || hasFineSupport;

            if (hasSourceSupport)
            {
                continue; // Supported by source image; not an invented structure
            }

            // Check if cluster is simply boundary sharpening/contrast along existing panel boundaries
            // (e.g. 1-pixel wide line segments with low fill ratio along seams/joints)
            bool isThinBoundaryArtifact = (width <= 14 || height <= 14) &&
                                          ((double)area / (width * height) < 0.18) &&
                                          area <= 100 &&
                                          analysis.Components.Any(c => c.Region != null &&
                                              normRect.Intersects(NormalizedRectangle.FromProtectedRegion(c.Region)));
            if (isThinBoundaryArtifact)
            {
                continue; // Edge boundary sharpening / contrast artifact; not a new coherent physical object
            }

            // Determine spatial context relative to furniture and scene (Part 3)
            bool overlapsFurniture = (analysis.Components.Count == 0 && analysis.ProtectedRegions.Count == 0) ||
                analysis.Components.Any(c => c.Region != null &&
                (normRect.CalculateIoU(NormalizedRectangle.FromProtectedRegion(c.Region)) > 0.05 ||
                 normRect.IntersectionOverSelf(NormalizedRectangle.FromProtectedRegion(c.Region)) > 0.20)) ||
                analysis.ProtectedRegions.Any(p => p.RegionType != "negative_space" &&
                 normRect.IntersectionOverSelf(NormalizedRectangle.FromProtectedRegion(p)) > 0.20);

            // Case 1: Outside furniture envelope (Scene / Wall additions)
            if (!overlapsFurniture)
            {
                // A. Minor scene detail (e.g. tiny screw mark near switch, hardware speck outside furniture)
                if (area < 70)
                {
                    var addition = new UnsupportedPhysicalAdditionResult
                    {
                        AdditionId = $"scene_detail_{i}",
                        Region = normRect,
                        Scale = PhysicalAdditionScale.Small,
                        Category = PhysicalAdditionCategory.MinorSceneDetailAddition,
                        Confidence = 0.70,
                        CoherentEdgePixels = area,
                        CoherentClusterRatio = (double)area / (width * height),
                        StructuralEdgeDensityGain = (double)area / paddedArea - origCoarseDensity,
                        SourceSupportStatus = SourceSupportStatus.Uncertain,
                        SourceSupportEvidence = $"Minor scene detail ({area}px) outside furniture at [{normRect.X:F2},{normRect.Y:F2},{normRect.Width:F2},{normRect.Height:F2}].",
                        Severity = ValidationSeverity.Warning,
                        IsViolation = false, // Warning only, not critical rejection per Part 3!
                        Description = $"Minor unsupported scene detail (hardware-like speck/mark) detected outside furniture at [{normRect.X:F2},{normRect.Y:F2}] ({area} edge pixels)."
                    };
                    additions.Add(addition);

                    evidences.Add(new ValidationEvidence
                    {
                        Category = ValidationEvidenceCategory.Diagnostic,
                        Type = "MinorUnsupportedSceneDetail",
                        Score = area,
                        Threshold = 70,
                        Severity = ValidationSeverity.Warning,
                        Passed = false,
                        Region = addition.AdditionId,
                        Message = addition.Description
                    });
                    continue;
                }

                // Check if scene addition is a localized fixture (e.g. wall outlet) vs broad room architecture (floor, ceiling)
                bool isLocalizedFixture = normRect.Width <= 0.40 && normRect.Height <= 0.40 && normRect.Area <= 0.10;
                if (!isLocalizedFixture)
                {
                    continue; // Broad floor / room architecture edge continuity; not a localized physical fixture
                }

                // B. Permanent scene physical addition (e.g. wall electrical outlet fixture invented on blank wall)
                if (area >= 100)
                {
                    var isRegReliable = regModel != RegistrationModel.None && regConfidence >= 0.50;
                    var addition = new UnsupportedPhysicalAdditionResult
                    {
                        AdditionId = $"scene_fixture_{i}",
                        Region = normRect,
                        Scale = isPlanarOrBroad ? PhysicalAdditionScale.Large : PhysicalAdditionScale.Small,
                        Category = PhysicalAdditionCategory.PermanentScenePhysicalAddition,
                        Confidence = 0.95,
                        CoherentEdgePixels = area,
                        CoherentClusterRatio = (double)area / (width * height),
                        StructuralEdgeDensityGain = (double)area / paddedArea - origCoarseDensity,
                        SourceSupportStatus = SourceSupportStatus.Unsupported,
                        SourceSupportEvidence = $"Coherent permanent scene fixture ({area}px) on wall/background at [{normRect.X:F2},{normRect.Y:F2},{normRect.Width:F2},{normRect.Height:F2}] with zero source support.",
                        Severity = isRegReliable ? ValidationSeverity.Critical : ValidationSeverity.Warning,
                        IsViolation = true,
                        Description = $"Unsupported permanent scene physical addition (wall/architectural fixture) detected at [{normRect.X:F2},{normRect.Y:F2},{normRect.Width:F2},{normRect.Height:F2}] ({area} edge pixels)."
                    };
                    additions.Add(addition);

                    evidences.Add(new ValidationEvidence
                    {
                        Category = ValidationEvidenceCategory.Structural,
                        Type = "PermanentScenePhysicalAddition",
                        Score = area,
                        Threshold = 100,
                        Severity = isRegReliable ? ValidationSeverity.Critical : ValidationSeverity.Warning,
                        Passed = false,
                        Region = addition.AdditionId,
                        Message = addition.Description
                    });
                    continue;
                }

                // C. Ambiguous intermediate scene cluster
                var intermediateAddition = new UnsupportedPhysicalAdditionResult
                {
                    AdditionId = $"scene_addition_{i}",
                    Region = normRect,
                    Scale = PhysicalAdditionScale.Small,
                    Category = PhysicalAdditionCategory.PermanentScenePhysicalAddition,
                    Confidence = 0.75,
                    CoherentEdgePixels = area,
                    CoherentClusterRatio = (double)area / (width * height),
                    StructuralEdgeDensityGain = (double)area / paddedArea - origCoarseDensity,
                    SourceSupportStatus = SourceSupportStatus.Uncertain,
                    SourceSupportEvidence = $"Ambiguous scene cluster ({area}px) outside furniture.",
                    Severity = ValidationSeverity.Warning,
                    IsViolation = false,
                    Description = $"Ambiguous scene cluster detected outside furniture at [{normRect.X:F2},{normRect.Y:F2}] ({area} edge pixels)."
                };
                additions.Add(intermediateAddition);
                continue;
            }

            // Case 2: Inside furniture or protected negative space
            // A. Large structural addition (e.g. countertop, sink, cabinet)
            if (isPlanarOrBroad && area >= 140 && normRect.Area <= 0.60)
            {
                var isRegReliable = regModel != RegistrationModel.None && regConfidence >= 0.50;
                var addition = new UnsupportedPhysicalAdditionResult
                {
                    AdditionId = $"addition_large_{i}",
                    Region = normRect,
                    Scale = PhysicalAdditionScale.Large,
                    Category = PhysicalAdditionCategory.FurniturePhysicalAddition,
                    Confidence = 0.95,
                    CoherentEdgePixels = area,
                    CoherentClusterRatio = (double)area / (width * height),
                    StructuralEdgeDensityGain = (double)area / paddedArea - origCoarseDensity,
                    SourceSupportStatus = SourceSupportStatus.Unsupported,
                    SourceSupportEvidence = $"Large coherent cluster of {area}px in [{normRect.X:F2},{normRect.Y:F2},{normRect.Width:F2},{normRect.Height:F2}] with zero source support (source coarse count={origCoarseCount}, fine count={origFineCount}).",
                    Severity = isRegReliable ? ValidationSeverity.Critical : ValidationSeverity.Warning,
                    IsViolation = true,
                    Description = $"Large unsupported physical structure detected at [{normRect.X:F2},{normRect.Y:F2},{normRect.Width:F2},{normRect.Height:F2}] ({area} connected edge pixels, source coarse density {origCoarseDensity:F3})."
                };
                additions.Add(addition);

                evidences.Add(new ValidationEvidence
                {
                    Category = ValidationEvidenceCategory.Structural,
                    Type = "UnsupportedPhysicalAddition",
                    Score = area,
                    Threshold = 140,
                    Severity = isRegReliable ? ValidationSeverity.Critical : ValidationSeverity.Warning,
                    Passed = false,
                    Region = addition.AdditionId,
                    Message = addition.Description
                });
            }
            // B. Small structural addition (e.g. second gas strut, hinge, handle, bracket, rod)
            else if (isElongated && area >= 80 && Math.Min(normRect.Width, normRect.Height) <= 0.08 && normRect.Area <= 0.035 && area <= 1200)
            {
                var isRegReliable = regModel != RegistrationModel.None && regConfidence >= 0.65;

                var addition = new UnsupportedPhysicalAdditionResult
                {
                    AdditionId = $"addition_small_{i}",
                    Region = normRect,
                    Scale = PhysicalAdditionScale.Small,
                    Category = PhysicalAdditionCategory.FurniturePhysicalAddition,
                    Confidence = 0.90,
                    CoherentEdgePixels = area,
                    CoherentClusterRatio = (double)area / (width * height),
                    StructuralEdgeDensityGain = (double)area / paddedArea - origCoarseDensity,
                    SourceSupportStatus = SourceSupportStatus.Unsupported,
                    SourceSupportEvidence = $"Rigid elongated candidate structure ({area}px, aspect={clusterAspect:F1}) at [{normRect.X:F2},{normRect.Y:F2},{normRect.Width:F2},{normRect.Height:F2}] with zero source support (source coarse count={origCoarseCount}, fine count={origFineCount}, paddedDens={origFineDensity:F4}, unpaddedDens={unpaddedFineDensity:F4}).",
                    Severity = isRegReliable ? ValidationSeverity.Critical : ValidationSeverity.Warning,
                    IsViolation = true,
                    Description = $"Unsupported physical structure (small/rigid component candidate) detected at [{normRect.X:F2},{normRect.Y:F2},{normRect.Width:F2},{normRect.Height:F2}] ({area} edge pixels, aspect {clusterAspect:F1})."
                };
                additions.Add(addition);

                evidences.Add(new ValidationEvidence
                {
                    Category = ValidationEvidenceCategory.Component,
                    Type = "UnsupportedPhysicalAddition",
                    Score = area,
                    Threshold = 80,
                    Severity = isRegReliable ? ValidationSeverity.Critical : ValidationSeverity.Warning,
                    Passed = false,
                    Region = addition.AdditionId,
                    Message = addition.Description
                });
            }
            else if (area >= 30 && area < 80)
            {
                // Sub-80px ambiguous furniture cluster - not corroborated as a confirmed physical component
                var addition = new UnsupportedPhysicalAdditionResult
                {
                    AdditionId = $"addition_small_{i}",
                    Region = normRect,
                    Scale = PhysicalAdditionScale.Small,
                    Category = PhysicalAdditionCategory.FurniturePhysicalAddition,
                    Confidence = 0.60,
                    CoherentEdgePixels = area,
                    CoherentClusterRatio = (double)area / (width * height),
                    StructuralEdgeDensityGain = (double)area / paddedArea - origCoarseDensity,
                    SourceSupportStatus = SourceSupportStatus.Uncertain,
                    SourceSupportEvidence = $"Uncertain fine cluster ({area}px) inside furniture envelope.",
                    Severity = ValidationSeverity.Warning,
                    IsViolation = false,
                    Description = $"Uncertain fine structure detected at [{normRect.X:F2},{normRect.Y:F2}] ({area} edge pixels)."
                };
                additions.Add(addition);
            }
        }


        // 4. Cleanup Separation (Parts 10, 11, 12)
        var removableObjects = analysis.SceneObjects
            .Where(s => s.Precedence == RegionPrecedence.RemovableScene)
            .ToList();

        var cleanupStatus = CleanupCompletionStatus.NotApplicable;
        if (removableObjects.Count > 0)
        {
            cleanupStatus = CleanupCompletionStatus.Complete; // Assume complete until proven incomplete
            foreach (var rem in removableObjects)
            {
                var remNorm = rem.Region != null ? NormalizedRectangle.FromProtectedRegion(rem.Region) : new NormalizedRectangle(0, 0, 0, 0);
                var rRect = ToPixelRect(remNorm, w, h);
                using var origObjRoi = new Mat(origCoarseEdges, rRect);
                using var restObjRoi = new Mat(restCoarseEdges, rRect);

                var origEdgesInObj = Cv2.CountNonZero(origObjRoi);
                var restEdgesInObj = Cv2.CountNonZero(restObjRoi);

                // If candidate still has similar or substantial edge structure in the clutter box, it was NOT removed
                bool remains = restEdgesInObj >= Math.Max(15, (int)(origEdgesInObj * 0.40));
                var targetStatus = remains ? CleanupCompletionStatus.Incomplete : CleanupCompletionStatus.Complete;

                if (remains)
                {
                    cleanupStatus = CleanupCompletionStatus.Incomplete;
                }

                cleanupTargets.Add(new CleanupTargetRecord
                {
                    CleanupTargetId = $"clutter_{rem.Id}",
                    Region = remNorm,
                    Authority = ConstraintAuthority.StrictCleanupAllowed,
                    ExpectedRemoval = true,
                    ObservedAfterRestoration = remains,
                    CleanupStatus = targetStatus,
                    RemainingPixelsRatio = origEdgesInObj > 0 ? (double)restEdgesInObj / origEdgesInObj : 0,
                    Details = remains
                        ? $"Temporary object '{rem.Id}' ({rem.Description}) remains in candidate ({restEdgesInObj} coarse edges)."
                        : $"Temporary object '{rem.Id}' was safely removed."
                });
            }

            if (cleanupStatus == CleanupCompletionStatus.Incomplete)
            {
                evidences.Add(new ValidationEvidence
                {
                    Category = ValidationEvidenceCategory.Diagnostic,
                    Type = "CleanupIncomplete",
                    Score = 0.5,
                    Threshold = 1.0,
                    Severity = ValidationSeverity.Warning, // NEVER Critical per Part 10!
                    Passed = false,
                    Message = "Scene cleanup incomplete: temporary non-structural background clutter was not removed, but furniture structural fidelity is preserved."
                });
            }
            else if (cleanupStatus == CleanupCompletionStatus.Complete)
            {
                evidences.Add(new ValidationEvidence
                {
                    Category = ValidationEvidenceCategory.Diagnostic,
                    Type = "CleanupComplete",
                    Score = 1.0,
                    Threshold = 1.0,
                    Severity = ValidationSeverity.Info,
                    Passed = true,
                    Message = "Scene cleanup complete: temporary non-structural objects were cleanly removed without structural distortion."
                });
            }
        }

        // 5. Generate Diagnostic Visualizations (Part 17)
        var diagnosticPaths = GenerateDiagnosticVisualizations(
            origMat,
            alignedMat,
            negativeSpaceRegions,
            violations,
            additions,
            cleanupTargets,
            unexpectedCoarseEdgeMask,
            diagnosticsDirectory,
            assetBaseName);

        return new EvaluationResult
        {
            NegativeSpaceRegions = negativeSpaceRegions,
            Violations = violations,
            Additions = additions,
            CleanupTargets = cleanupTargets,
            CleanupStatus = cleanupStatus,
            Evidences = evidences,
            DiagnosticPaths = diagnosticPaths
        };
    }

    private static Dictionary<string, string> GenerateDiagnosticVisualizations(
        Mat origMat,
        Mat alignedMat,
        List<ObservedNegativeSpaceRegion> negRegions,
        List<ProtectedNegativeSpaceViolationResult> violations,
        List<UnsupportedPhysicalAdditionResult> additions,
        List<CleanupTargetRecord> cleanupTargets,
        Mat unexpectedCoarseEdgeMask,
        string diagnosticsDirectory,
        string assetBaseName)
    {
        var paths = new Dictionary<string, string>();
        if (string.IsNullOrWhiteSpace(diagnosticsDirectory))
            return paths;

        Directory.CreateDirectory(diagnosticsDirectory);
        var w = origMat.Width;
        var h = origMat.Height;

        // 1. negative_space_regions.png
        using (var canvas = origMat.Clone())
        {
            foreach (var r in negRegions)
            {
                var rect = ToPixelRect(r.Region, w, h);
                var color = r.ProtectionLevel == ConstraintAuthority.Strict
                    ? new Scalar(0, 200, 0)
                    : (r.ProtectionLevel == ConstraintAuthority.Advisory ? new Scalar(0, 200, 255) : new Scalar(100, 100, 100));
                Cv2.Rectangle(canvas, rect, color, 2);
                Cv2.PutText(canvas, $"{r.RegionId} ({r.SpaceType}) [{r.ProtectionLevel}]", new Point(rect.X + 4, rect.Y + 16),
                    HersheyFonts.HersheySimplex, 0.45, color, 1);
            }
            var p = Path.Combine(diagnosticsDirectory, $"{assetBaseName}__negative_space_regions.png");
            canvas.SaveImage(p);
            paths["negative_space_regions"] = p;
        }

        // 2. negative_space_violations.png
        using (var canvas = alignedMat.Clone())
        {
            foreach (var v in violations)
            {
                var rect = ToPixelRect(v.Region, w, h);
                Cv2.Rectangle(canvas, rect, new Scalar(0, 0, 255), 3);
                Cv2.PutText(canvas, $"VIOLATION: {v.ViolationId} ({v.UnexpectedEdgePixels}px)", new Point(rect.X + 4, rect.Y + 20),
                    HersheyFonts.HersheySimplex, 0.5, new Scalar(0, 0, 255), 2);
            }
            var p = Path.Combine(diagnosticsDirectory, $"{assetBaseName}__negative_space_violations.png");
            canvas.SaveImage(p);
            paths["negative_space_violations"] = p;
        }

        // 3. unsupported_addition_candidates.png
        using (var canvas = alignedMat.Clone())
        {
            // Highlight unexpected edge mask in cyan
            using var edgeColor = new Mat();
            Cv2.CvtColor(unexpectedCoarseEdgeMask, edgeColor, ColorConversionCodes.GRAY2BGR);
            Cv2.AddWeighted(canvas, 0.75, edgeColor, 0.5, 0, canvas);

            var p = Path.Combine(diagnosticsDirectory, $"{assetBaseName}__unsupported_addition_candidates.png");
            canvas.SaveImage(p);
            paths["unsupported_addition_candidates"] = p;
        }

        // 4. unsupported_addition_confirmed.png
        using (var canvas = alignedMat.Clone())
        {
            foreach (var a in additions)
            {
                var rect = ToPixelRect(a.Region, w, h);
                var col = a.Scale == PhysicalAdditionScale.Large ? new Scalar(0, 0, 255) : new Scalar(0, 140, 255);
                Cv2.Rectangle(canvas, rect, col, 2);
                Cv2.PutText(canvas, $"{a.AdditionId} [{a.Scale}] ({a.CoherentEdgePixels}px)", new Point(rect.X + 4, Math.Max(14, rect.Y - 4)),
                    HersheyFonts.HersheySimplex, 0.45, col, 1);
            }
            var p = Path.Combine(diagnosticsDirectory, $"{assetBaseName}__unsupported_addition_confirmed.png");
            canvas.SaveImage(p);
            paths["unsupported_addition_confirmed"] = p;
        }

        // 5. cleanup_targets.png
        using (var canvas = origMat.Clone())
        {
            foreach (var c in cleanupTargets)
            {
                var rect = ToPixelRect(c.Region, w, h);
                Cv2.Rectangle(canvas, rect, new Scalar(255, 255, 0), 2);
                Cv2.PutText(canvas, $"{c.CleanupTargetId} [{c.Authority}]", new Point(rect.X + 4, rect.Y + 16),
                    HersheyFonts.HersheySimplex, 0.45, new Scalar(255, 255, 0), 1);
            }
            var p = Path.Combine(diagnosticsDirectory, $"{assetBaseName}__cleanup_targets.png");
            canvas.SaveImage(p);
            paths["cleanup_targets"] = p;
        }

        // 6. cleanup_remaining.png
        using (var canvas = alignedMat.Clone())
        {
            foreach (var c in cleanupTargets)
            {
                var rect = ToPixelRect(c.Region, w, h);
                var col = c.ObservedAfterRestoration ? new Scalar(0, 165, 255) : new Scalar(0, 255, 0);
                Cv2.Rectangle(canvas, rect, col, 2);
                var label = c.ObservedAfterRestoration ? "REMAINS (INCOMPLETE)" : "REMOVED (COMPLETE)";
                Cv2.PutText(canvas, $"{c.CleanupTargetId}: {label}", new Point(rect.X + 4, rect.Y + 16),
                    HersheyFonts.HersheySimplex, 0.45, col, 1);
            }
            var p = Path.Combine(diagnosticsDirectory, $"{assetBaseName}__cleanup_remaining.png");
            canvas.SaveImage(p);
            paths["cleanup_remaining"] = p;
        }

        return paths;
    }

    private static NormalizedRectangle EstimateFurnitureBounds(FurnitureAnalysis analysis)
    {
        if (analysis.Components.Count > 0)
        {
            var validRegions = analysis.Components.Where(c => c.Region != null).Select(c => c.Region!).ToList();
            if (validRegions.Count > 0)
            {
                var minX = validRegions.Min(r => r.X);
                var minY = validRegions.Min(r => r.Y);
                var maxX = validRegions.Max(r => r.X + r.Width);
                var maxY = validRegions.Max(r => r.Y + r.Height);
                return new NormalizedRectangle(minX, minY, maxX - minX, maxY - minY).Clamp();
            }
        }
        return new NormalizedRectangle(0.08, 0.08, 0.84, 0.84);
    }

    private static Rect ToPixelRect(NormalizedRectangle norm, int w, int h)
    {
        var x = Math.Clamp((int)Math.Round(norm.X * w), 0, w - 1);
        var y = Math.Clamp((int)Math.Round(norm.Y * h), 0, h - 1);
        var width = Math.Clamp((int)Math.Round(norm.Width * w), 1, w - x);
        var height = Math.Clamp((int)Math.Round(norm.Height * h), 1, h - y);
        return new Rect(x, y, width, height);
    }

    private static Rect PadPixelRect(Rect r, int pad, int w, int h)
    {
        var x = Math.Max(0, r.X - pad);
        var y = Math.Max(0, r.Y - pad);
        var right = Math.Min(w, r.Right + pad);
        var bottom = Math.Min(h, r.Bottom + pad);
        return new Rect(x, y, right - x, bottom - y);
    }

    public class EvaluationResult
    {
        public List<ObservedNegativeSpaceRegion> NegativeSpaceRegions { get; set; } = new();
        public List<ProtectedNegativeSpaceViolationResult> Violations { get; set; } = new();
        public List<UnsupportedPhysicalAdditionResult> Additions { get; set; } = new();
        public List<CleanupTargetRecord> CleanupTargets { get; set; } = new();
        public CleanupCompletionStatus CleanupStatus { get; set; } = CleanupCompletionStatus.NotApplicable;
        public List<ValidationEvidence> Evidences { get; set; } = new();
        public Dictionary<string, string> DiagnosticPaths { get; set; } = new();
    }
}
