using System.IO;
using OpenCvSharp;
using OpenIMG.Application.Abstractions.Validation;
using OpenIMG.Application.Configuration;
using OpenIMG.Core.Enums;
using OpenIMG.Core.Models;
using QualityAnalyzer = OpenIMG.Application.Analysis.OpenCvQualityAnalyzer;

namespace OpenIMG.Infrastructure.ImageProcessing;

public class OpenCvImageValidator : ILocalImageValidator
{
    private const int AnalysisMaxDimension = 1024;
    private readonly OpenImgOptions _options;
    private readonly IComponentCardinalityValidator _cardinalityValidator;

    public OpenCvImageValidator(
        OpenImgOptions? options = null,
        IComponentCardinalityValidator? cardinalityValidator = null)
    {
        _options = options ?? new OpenImgOptions();
        // The optional fallback preserves direct construction in offline
        // calibration tests; the desktop host injects the registered validator.
        _cardinalityValidator = cardinalityValidator ?? new OpenCvComponentCardinalityValidator();
    }

    // Compatibility facade for calibration tests and older callers. The mask
    // construction implementation lives in OpenCvValidationMaskBuilder.
    public static (Mat FurnitureMask, Mat SceneMask, Mat RemovableMask, Mat UncertainMask, Mat BoundaryMask)
        BuildExplicitValidationMasks(int width, int height, Mat origGray, FurnitureAnalysis analysis) =>
        OpenCvValidationMaskBuilder.Build(width, height, origGray, analysis);

    public Task<LocalValidationReport> ValidateAsync(
        string originalImagePath,
        string restoredImagePath,
        FurnitureAnalysis analysis,
        string diagnosticsDirectory,
        string assetBaseName,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            var diagnosticPaths = new Dictionary<string, string>();
            var failureReasons = new List<string>();
            var evidences = new List<ValidationEvidence>();

            using var origMatRaw = Cv2.ImRead(originalImagePath, ImreadModes.Color);
            using var restMatRaw = Cv2.ImRead(restoredImagePath, ImreadModes.Color);

            if (origMatRaw.Empty() || restMatRaw.Empty())
            {
                return new LocalValidationReport
                {
                    Passed = false,
                    Notes = "Failed to load image files into OpenCV.",
                    FailureReasons = new List<string> { "One or both image files could not be decoded." }
                };
            }

            // Aspect ratio check on raw dimensions
            var origAspect = (double)origMatRaw.Width / origMatRaw.Height;
            var restAspect = (double)restMatRaw.Width / restMatRaw.Height;
            var aspectDiff = Math.Abs(origAspect - restAspect) / origAspect;

            // Scale down to analysis resolution for CPU efficiency
            bool hasDimensionChange = origMatRaw.Width != restMatRaw.Width || origMatRaw.Height != restMatRaw.Height;
            using var origMat = OpenCvImageRegistration.ResizeToMaxDimension(origMatRaw, AnalysisMaxDimension);
            using var restMat = hasDimensionChange
                ? OpenCvImageRegistration.ResizeToMaxDimension(restMatRaw, AnalysisMaxDimension)
                : OpenCvImageRegistration.ResizeToMatch(restMatRaw, origMat.Width, origMat.Height);

            // 1. Escalated Image Registration
            var (alignedMat, regModel, regConfidence, regScale, regAspectConsistency) = OpenCvImageRegistration.RegisterImages(origMat, restMat);

            // 2. Structural Similarity (SSIM)
            using var origGray = new Mat();
            using var alignedGray = new Mat();
            Cv2.CvtColor(origMat, origGray, ColorConversionCodes.BGR2GRAY);
            Cv2.CvtColor(alignedMat, alignedGray, ColorConversionCodes.BGR2GRAY);

            // Phase 3.6: Explicit Validation Masks & Region Precedence (Computed early for mask-scoped metrics)
            var (furnitureValidationMask, sceneValidationMask, removableSceneMask, uncertainMask, furnitureBoundaryMask) =
                OpenCvValidationMaskBuilder.Build(origMat.Width, origMat.Height, origGray, analysis);

            var furnitureMaskPixels = Cv2.CountNonZero(furnitureValidationMask);
            var sceneMaskPixels = Cv2.CountNonZero(sceneValidationMask);
            var boundaryMaskPixels = Cv2.CountNonZero(furnitureBoundaryMask);
            var uncertainMaskPixels = Cv2.CountNonZero(uncertainMask);

            var ssim = OpenCvPhotometricMetrics.CalculateSsim(origGray, alignedGray);
            var furnitureSsim = CalculateSsimMasked(origGray, alignedGray, furnitureValidationMask);

            // 3. Multi-Scale Spatially Tolerant Edge Validation
            var maxDim = Math.Max(origMat.Width, origMat.Height);
            var effectiveTolerance = Math.Max(1.0, _options.EdgeMatchTolerancePixels * (maxDim / 1024.0));

            // Scale 1: Coarse Structure (Major furniture boundaries, cabinet frame, shelf lines)
            using var origCoarseBlur = new Mat();
            using var restCoarseBlur = new Mat();
            Cv2.GaussianBlur(origGray, origCoarseBlur, new Size(7, 7), 2.0);
            Cv2.GaussianBlur(alignedGray, restCoarseBlur, new Size(7, 7), 2.0);

            using var origCoarseEdges = new Mat();
            using var restCoarseEdges = new Mat();
            Cv2.Canny(origCoarseBlur, origCoarseEdges, 70, 160);
            Cv2.Canny(restCoarseBlur, restCoarseEdges, 70, 160);

            using var coarseResult = OpenCvStructuralEvidenceAnalyzer.CalculateTolerantEdgeAgreement(origCoarseEdges, restCoarseEdges, effectiveTolerance);

            // Scale 2: Medium Structure (Panel seams, door borders)
            using var origMedBlur = new Mat();
            using var restMedBlur = new Mat();
            Cv2.GaussianBlur(origGray, origMedBlur, new Size(3, 3), 1.0);
            Cv2.GaussianBlur(alignedGray, restMedBlur, new Size(3, 3), 1.0);

            using var origMedEdges = new Mat();
            using var restMedEdges = new Mat();
            Cv2.Canny(origMedBlur, origMedEdges, 40, 120);
            Cv2.Canny(restMedBlur, restMedEdges, 40, 120);

            using var medResult = OpenCvStructuralEvidenceAnalyzer.CalculateTolerantEdgeAgreement(origMedEdges, restMedEdges, effectiveTolerance);

            var coarsePrec = coarseResult.Precision;
            var coarseRec = coarseResult.Recall;
            var coarseF1 = coarseResult.F1;
            var coarseDist = coarseResult.MeanDist;
            var coarseUnexpRatio = coarseResult.UnexpectedEdgeRatio;
            var coarseMissRatio = coarseResult.MissingEdgeRatio;

            var medPrec = medResult.Precision;
            var medRec = medResult.Recall;
            var medF1 = medResult.F1;
            var medDist = medResult.MeanDist;
            var medUnexpRatio = medResult.UnexpectedEdgeRatio;
            var medMissRatio = medResult.MissingEdgeRatio;

            // Weighted multi-scale structural edge agreement (60% coarse major structure, 40% medium panel structure)
            var edgePrecision = (coarsePrec * 0.6) + (medPrec * 0.4);
            var edgeRecall = (coarseRec * 0.6) + (medRec * 0.4);
            var edgeF1 = (coarseF1 * 0.6) + (medF1 * 0.4);
            var meanEdgeDistance = (coarseDist * 0.6) + (medDist * 0.4);
            var edgeSimilarity = edgeF1;

            var unexpectedEdgeRatio = (coarseUnexpRatio * 0.6) + (medUnexpRatio * 0.4);
            var missingEdgeRatio = (coarseMissRatio * 0.6) + (medMissRatio * 0.4);
            var unexpectedPixelCount = coarseResult.UnexpectedEdgePixelCount;
            var missingPixelCount = coarseResult.MissingEdgePixelCount;

            // Masked Furniture Edge Agreement
            using var furnOrigEdges = new Mat();
            using var furnRestEdges = new Mat();
            Cv2.BitwiseAnd(origCoarseEdges, furnitureValidationMask, furnOrigEdges);
            Cv2.BitwiseAnd(restCoarseEdges, furnitureValidationMask, furnRestEdges);
            using var furnEdgeRes = OpenCvStructuralEvidenceAnalyzer.CalculateTolerantEdgeAgreement(furnOrigEdges, furnRestEdges, effectiveTolerance);

            var furnitureEdgePrecision = furnEdgeRes.Precision;
            var furnitureEdgeRecall = furnEdgeRes.Recall;
            var furnitureEdgeSimilarity = furnEdgeRes.F1;

            // Connected component cluster analysis: exclude removable scene clutter from unexpected edge clusters
            using var nonSceneUnexpectedEdges = new Mat();
            var removableCount = Cv2.CountNonZero(removableSceneMask);
            if (removableCount > 0)
            {
                using var notRemovable = new Mat();
                Cv2.BitwiseNot(removableSceneMask, notRemovable);
                Cv2.BitwiseAnd(coarseResult.UnexpectedEdgeMap, notRemovable, nonSceneUnexpectedEdges);
            }
            else
            {
                coarseResult.UnexpectedEdgeMap.CopyTo(nonSceneUnexpectedEdges);
            }

            var (clusterCount, largestClusterPixels, largestClusterRatio, clusterMap) =
                OpenCvStructuralEvidenceAnalyzer.AnalyzeUnexpectedEdgeClusters(nonSceneUnexpectedEdges, Cv2.CountNonZero(restCoarseEdges), _options.MinimumUnexpectedEdgeClusterPixels);

            // 4. Silhouette Similarity scoped inside furniture validation mask
            var silhouetteSimilarity = CalculateSilhouetteSimilarity(origGray, alignedGray, furnitureValidationMask);

            // 5. Color Validation (CIELAB)
            using var origLab = new Mat();
            using var alignedLab = new Mat();
            Cv2.CvtColor(origMat, origLab, ColorConversionCodes.BGR2Lab);
            Cv2.CvtColor(alignedMat, alignedLab, ColorConversionCodes.BGR2Lab);

            var (rawColorDiff, normColorDistDiff) = OpenCvPhotometricMetrics.CalculateColorMetrics(origLab, alignedLab);

            // 6. Difference Map & Region Alterations
            using var diffMap = new Mat();
            Cv2.Absdiff(origMat, alignedMat, diffMap);
            using var diffGray = new Mat();
            Cv2.CvtColor(diffMap, diffGray, ColorConversionCodes.BGR2GRAY);
            using var diffBinary = new Mat();
            Cv2.Threshold(diffGray, diffBinary, 35, 255, ThresholdTypes.Binary);

            // Phase 4C.4 Edge-Aligned vs Interior Change Decomposition (Parts 2-4)
            using var origAllEdges = new Mat();
            Cv2.BitwiseOr(origCoarseEdges, origMedEdges, origAllEdges);

            var edgeRadius = Math.Max(2, (int)Math.Round(effectiveTolerance));
            using var edgeKernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(edgeRadius * 2 + 1, edgeRadius * 2 + 1));
            using var edgeDilatedMask = new Mat();
            Cv2.Dilate(origAllEdges, edgeDilatedMask, edgeKernel);

            using var edgeAlignedChanges = new Mat();
            Cv2.BitwiseAnd(diffBinary, edgeDilatedMask, edgeAlignedChanges);

            using var notEdgeDilated = new Mat();
            Cv2.BitwiseNot(edgeDilatedMask, notEdgeDilated);

            using var interiorChanges = new Mat();
            Cv2.BitwiseAnd(diffBinary, notEdgeDilated, interiorChanges);

            // Phase 4C.1 & 4E.2 Photographic Quality & Perspective Metrics
            using var restGray = new Mat();
            Cv2.CvtColor(restMat, restGray, ColorConversionCodes.BGR2GRAY);

            var sourceQualityAssessment = QualityAnalyzer.Analyze(origMat);
            var restQualityAssessment = QualityAnalyzer.Analyze(restMat);

            double sharpnessBefore = sourceQualityAssessment.SharpnessScore;
            double sharpnessAfter = restQualityAssessment.SharpnessScore;
            double sharpnessGain = sharpnessBefore > 0
                ? Math.Round((sharpnessAfter - sharpnessBefore) / sharpnessBefore, 4)
                : 0.0;

            double noiseBefore = sourceQualityAssessment.NoiseScore;
            double noiseAfter = restQualityAssessment.NoiseScore;

            double localContrastBefore = sourceQualityAssessment.ContrastScore;
            double localContrastAfter = restQualityAssessment.ContrastScore;
            double contrastGain = localContrastBefore > 0
                ? Math.Round((localContrastAfter - localContrastBefore) / localContrastBefore, 4)
                : 0.0;

            double globalContrastBefore = sourceQualityAssessment.ContrastScore;
            double globalContrastAfter = restQualityAssessment.ContrastScore;

            double dynamicRangeBefore = sourceQualityAssessment.DynamicRangeSpan;
            double dynamicRangeAfter = restQualityAssessment.DynamicRangeSpan;
            double dynamicRangeGain = Math.Round(dynamicRangeAfter - dynamicRangeBefore, 2);

            double exposureBefore = sourceQualityAssessment.ExposureMean;
            double exposureAfter = restQualityAssessment.ExposureMean;
            double exposureShift = Math.Round(exposureAfter - exposureBefore, 2);

            double hazeBefore = sourceQualityAssessment.HazeScore;
            double hazeAfter = restQualityAssessment.HazeScore;
            double hazeReduction = Math.Round(hazeBefore - hazeAfter, 2);

            double colorCastBefore = sourceQualityAssessment.ColorCastDelta;
            double colorCastAfter = restQualityAssessment.ColorCastDelta;
            double colorCastReduction = Math.Round(colorCastBefore - colorCastAfter, 2);

            bool photographicEdgeEnhancementDetected = evidences.Any(e => e.Type == "EdgeAlignedPhotometricChange" && e.Passed) ||
                                                       sharpnessGain >= 1.5 ||
                                                       hazeReduction >= 10.0;

            using var sceneChanges = new Mat();
            Cv2.BitwiseAnd(diffBinary, sceneValidationMask, sceneChanges);

            using var furnitureChanges = new Mat();
            Cv2.BitwiseAnd(diffBinary, furnitureValidationMask, furnitureChanges);

            using var boundaryChanges = new Mat();
            if (photographicEdgeEnhancementDetected)
            {
                Cv2.BitwiseAnd(interiorChanges, furnitureBoundaryMask, boundaryChanges);
            }
            else
            {
                Cv2.BitwiseAnd(diffBinary, furnitureBoundaryMask, boundaryChanges);
            }

            var totalPixels = diffBinary.Width * diffBinary.Height;
            var nonZeroPixels = Cv2.CountNonZero(diffBinary);
            var changedAreaPercentage = (double)nonZeroPixels / totalPixels;

            var furnitureChangedAreaPercentage = furnitureMaskPixels > 0
                ? (double)Cv2.CountNonZero(furnitureChanges) / furnitureMaskPixels
                : changedAreaPercentage;

            var sceneCleanupAreaPercentage = sceneMaskPixels > 0
                ? (double)Cv2.CountNonZero(sceneChanges) / sceneMaskPixels
                : 0.0;

            var sceneCleanupIntrusionPercentage = boundaryMaskPixels > 0
                ? (double)Cv2.CountNonZero(boundaryChanges) / boundaryMaskPixels
                : 0.0;

            var boundaryContinuityScore = 1.0 - Math.Clamp(sceneCleanupIntrusionPercentage * 2.5, 0.0, 1.0);

            var largestRegionPercentage = furnitureMaskPixels > 0
                ? OpenCvPhotometricMetrics.CalculateLargestConnectedRegionPercentage(furnitureChanges, furnitureMaskPixels)
                : OpenCvPhotometricMetrics.CalculateLargestConnectedRegionPercentage(diffBinary, totalPixels);

            var (protectedInteriorChangedPercentage, protectedEdgeAlignedChangedPercentage) =
                EvaluateProtectedAreas(diffBinary, edgeAlignedChanges, interiorChanges, origLab, alignedLab, analysis, evidences, _options.MaxAllowedProtectedAreaChangedPercentage);

            // Phase 3.6: Check Uncertain Region Intrusions (Specification 2 & 3)
            if (uncertainMaskPixels > 0)
            {
                using var uncertainChanges = new Mat();
                Cv2.BitwiseAnd(diffBinary, uncertainMask, uncertainChanges);
                var uncertainChangedPixels = Cv2.CountNonZero(uncertainChanges);
                var uncertainRatio = (double)uncertainChangedPixels / uncertainMaskPixels;
                if (uncertainRatio > 0.05)
                {
                    evidences.Add(new ValidationEvidence
                    {
                        Category = ValidationEvidenceCategory.ProtectedRegion,
                        Type = "UncertainRegionIntrusion",
                        Score = uncertainRatio,
                        Threshold = 0.05,
                        Severity = ValidationSeverity.Warning,
                        Passed = false,
                        Message = $"Modifications detected inside uncertain or overlapping region ({uncertainRatio:P1}). Requires human review."
                    });
                }
            }

            // Component-Level Validation (Phase 4C.4 Decomposed Component Evaluation)
            var componentResults = OpenCvStructuralEvidenceAnalyzer.EvaluateComponentLevel(
                origMat, alignedMat, origGray, alignedGray, origCoarseEdges, restCoarseEdges,
                origLab, alignedLab, diffBinary, edgeAlignedChanges, interiorChanges, notEdgeDilated, analysis, effectiveTolerance,
                _options.MinimumUnexpectedEdgeClusterPixels, photographicEdgeEnhancementDetected, evidences, ref protectedInteriorChangedPercentage);

            // Region-Aware & Material-Sensitive Color Metrics
            var furnitureColorDiff = CalculateFurnitureStructuralColorDifference(origLab, alignedLab, furnitureValidationMask, analysis);
            var materialSensitiveColorDiff = CalculateMaterialSensitiveColorDifference(
                origLab, alignedLab, analysis, origMat.Width, origMat.Height,
                out bool hasMaterialColorViolation, out string materialViolationReason);
            var accentColorPreservation = CalculateAccentColorPreservation(origLab, alignedLab, analysis, origMat.Width, origMat.Height);

            if (hasMaterialColorViolation)
            {
                evidences.Add(new ValidationEvidence
                {
                    Category = ValidationEvidenceCategory.Component,
                    Type = "MaterialColorIdentityViolation",
                    Score = materialSensitiveColorDiff,
                    Threshold = _options.MaxAllowedMaterialSensitiveColorDifference,
                    Severity = ValidationSeverity.Critical,
                    Passed = false,
                    Message = materialViolationReason
                });
            }

            // Overlap-Aware Reframing & Zoom-Out
            var reframing = EvaluateOverlapReframing(origMatRaw, restMatRaw, regModel, regConfidence, regScale, regAspectConsistency, analysis);

            // 7. Generate Diagnostic Images if output directory specified
            if (Directory.Exists(diagnosticsDirectory))
            {
                var basePrefix = string.IsNullOrEmpty(assetBaseName) ? "" : $"{assetBaseName}__";

                var alignedPath = Path.Combine(diagnosticsDirectory, $"{basePrefix}aligned.png");
                Cv2.ImWrite(alignedPath, alignedMat);
                diagnosticPaths["aligned"] = alignedPath;

                var origEdgesPath = Path.Combine(diagnosticsDirectory, $"{basePrefix}edges_orig.png");
                Cv2.ImWrite(origEdgesPath, origCoarseEdges);
                diagnosticPaths["edges_orig"] = origEdgesPath;

                var restEdgesPath = Path.Combine(diagnosticsDirectory, $"{basePrefix}edges_rest.png");
                Cv2.ImWrite(restEdgesPath, restCoarseEdges);
                diagnosticPaths["edges_rest"] = restEdgesPath;

                var diffPath = Path.Combine(diagnosticsDirectory, $"{basePrefix}diff.png");
                Cv2.ImWrite(diffPath, diffBinary);
                diagnosticPaths["difference"] = diffPath;

                var unexpEdgesPath = Path.Combine(diagnosticsDirectory, $"{basePrefix}unexpected_edges.png");
                Cv2.ImWrite(unexpEdgesPath, coarseResult.UnexpectedEdgeMap);
                diagnosticPaths["unexpected_edges"] = unexpEdgesPath;

                var missEdgesPath = Path.Combine(diagnosticsDirectory, $"{basePrefix}missing_edges.png");
                Cv2.ImWrite(missEdgesPath, coarseResult.MissingEdgeMap);
                diagnosticPaths["missing_edges"] = missEdgesPath;

                using var clusterOverlay = alignedMat.Clone();
                using var dilatedClusters = new Mat();
                Cv2.Dilate(clusterMap, dilatedClusters, new Mat());
                clusterOverlay.SetTo(new Scalar(0, 0, 255), dilatedClusters);
                var unexpClustersPath = Path.Combine(diagnosticsDirectory, $"{basePrefix}unexpected_clusters.png");
                Cv2.ImWrite(unexpClustersPath, clusterOverlay);
                diagnosticPaths["unexpected_clusters"] = unexpClustersPath;

                using var compRegionsPreview = origMat.Clone();
                OpenCvStructuralEvidenceAnalyzer.DrawComponentRegions(compRegionsPreview, componentResults);
                var compPath = Path.Combine(diagnosticsDirectory, $"{basePrefix}component_regions.png");
                Cv2.ImWrite(compPath, compRegionsPreview);
                diagnosticPaths["component_regions"] = compPath;

                using var regionsPreview = origMat.Clone();
                DrawProtectedRegions(regionsPreview, analysis);
                var regPath = Path.Combine(diagnosticsDirectory, $"{basePrefix}regions.png");
                Cv2.ImWrite(regPath, regionsPreview);
                diagnosticPaths["protected_regions"] = regPath;

                // Phase 3.6 Diagnostic Masks (Specification 9)
                var furnMaskPath = Path.Combine(diagnosticsDirectory, $"{basePrefix}furniture_mask.png");
                Cv2.ImWrite(furnMaskPath, furnitureValidationMask);
                diagnosticPaths["furniture_mask"] = furnMaskPath;
                Cv2.ImWrite(Path.Combine(diagnosticsDirectory, "furniture_mask.png"), furnitureValidationMask);

                var sceneMaskPath = Path.Combine(diagnosticsDirectory, $"{basePrefix}scene_mask.png");
                Cv2.ImWrite(sceneMaskPath, sceneValidationMask);
                diagnosticPaths["scene_mask"] = sceneMaskPath;
                Cv2.ImWrite(Path.Combine(diagnosticsDirectory, "scene_mask.png"), sceneValidationMask);

                var remMaskPath = Path.Combine(diagnosticsDirectory, $"{basePrefix}removable_scene_mask.png");
                Cv2.ImWrite(remMaskPath, removableSceneMask);
                diagnosticPaths["removable_scene_mask"] = remMaskPath;
                Cv2.ImWrite(Path.Combine(diagnosticsDirectory, "removable_scene_mask.png"), removableSceneMask);

                var uncMaskPath = Path.Combine(diagnosticsDirectory, $"{basePrefix}uncertain_mask.png");
                Cv2.ImWrite(uncMaskPath, uncertainMask);
                diagnosticPaths["uncertain_mask"] = uncMaskPath;
                Cv2.ImWrite(Path.Combine(diagnosticsDirectory, "uncertain_mask.png"), uncertainMask);

                var boundMaskPath = Path.Combine(diagnosticsDirectory, $"{basePrefix}furniture_boundary_mask.png");
                Cv2.ImWrite(boundMaskPath, furnitureBoundaryMask);
                diagnosticPaths["furniture_boundary_mask"] = boundMaskPath;
                Cv2.ImWrite(Path.Combine(diagnosticsDirectory, "furniture_boundary_mask.png"), furnitureBoundaryMask);

                var sceneChangesPath = Path.Combine(diagnosticsDirectory, $"{basePrefix}scene_changes.png");
                Cv2.ImWrite(sceneChangesPath, sceneChanges);
                diagnosticPaths["scene_changes"] = sceneChangesPath;
                Cv2.ImWrite(Path.Combine(diagnosticsDirectory, "scene_changes.png"), sceneChanges);

                var furnChangesPath = Path.Combine(diagnosticsDirectory, $"{basePrefix}furniture_changes.png");
                Cv2.ImWrite(furnChangesPath, furnitureChanges);
                diagnosticPaths["furniture_changes"] = furnChangesPath;
                Cv2.ImWrite(Path.Combine(diagnosticsDirectory, "furniture_changes.png"), furnitureChanges);

                // Phase 4C.4 Decomposed Component & Region Change Masks (Part 14)
                var compChangedTotalPath = Path.Combine(diagnosticsDirectory, $"{basePrefix}component_changed_total.png");
                Cv2.ImWrite(compChangedTotalPath, diffBinary);
                diagnosticPaths["component_changed_total"] = compChangedTotalPath;
                Cv2.ImWrite(Path.Combine(diagnosticsDirectory, "component_changed_total.png"), diffBinary);

                var compChangedEdgePath = Path.Combine(diagnosticsDirectory, $"{basePrefix}component_changed_edge_aligned.png");
                Cv2.ImWrite(compChangedEdgePath, edgeAlignedChanges);
                diagnosticPaths["component_changed_edge_aligned"] = compChangedEdgePath;
                Cv2.ImWrite(Path.Combine(diagnosticsDirectory, "component_changed_edge_aligned.png"), edgeAlignedChanges);

                var compChangedInteriorPath = Path.Combine(diagnosticsDirectory, $"{basePrefix}component_changed_interior.png");
                Cv2.ImWrite(compChangedInteriorPath, interiorChanges);
                diagnosticPaths["component_changed_interior"] = compChangedInteriorPath;
                Cv2.ImWrite(Path.Combine(diagnosticsDirectory, "component_changed_interior.png"), interiorChanges);

                using var colorDeltaMap = OpenCvPhotometricMetrics.CreateColorDeltaMap(origLab, alignedLab);
                var compColorDeltaPath = Path.Combine(diagnosticsDirectory, $"{basePrefix}component_color_delta.png");
                Cv2.ImWrite(compColorDeltaPath, colorDeltaMap);
                diagnosticPaths["component_color_delta"] = compColorDeltaPath;
                Cv2.ImWrite(Path.Combine(diagnosticsDirectory, "component_color_delta.png"), colorDeltaMap);
            }

            // Phase 4C.1 & 4E.2 Photographic Quality & Perspective Metrics

            using var origLabFull = new Mat();
            using var restLabFull = new Mat();
            Cv2.CvtColor(origMat, origLabFull, ColorConversionCodes.BGR2Lab);
            Cv2.CvtColor(restMat, restLabFull, ColorConversionCodes.BGR2Lab);
            Cv2.MeanStdDev(origLabFull, out var origLabMean, out _);
            Cv2.MeanStdDev(restLabFull, out var restLabMean, out _);
            double dLabA = restLabMean.Val1 - origLabMean.Val1;
            double dLabB = restLabMean.Val2 - origLabMean.Val2;
            double whiteBalanceShift = Math.Round(Math.Sqrt(dLabA * dLabA + dLabB * dLabB), 2);

            // 6. Perspective and Straightening Detection
            bool perspectiveDetected = regModel == RegistrationModel.Affine || regModel == RegistrationModel.Homography;
            double rotationCorrectionDeg = perspectiveDetected
                ? Math.Round(Math.Abs(1.0 - regAspectConsistency) * 10.0, 2)
                : 0.0;
            double perspectiveTransformMag = perspectiveDetected
                ? Math.Round(Math.Abs(1.0 - regScale), 4)
                : 0.0;
            double cropPctAfterCorrection = Math.Round((1.0 - Math.Min(1.0, reframing.Coverage)) * 100.0, 2);
            bool geometryPreserved = reframing.AspectConsistency > 0.85 && furnitureSsim > 0.40;

            evidences.AddRange(OpenCvVisualArtifactValidator.Validate(origMat, alignedMat, diagnosticsDirectory, assetBaseName));

            // Phase 4F.3 Negative Space, Unsupported Physical Additions & Cleanup Separation
            var negSpaceEval = OpenCvNegativeSpaceValidator.Evaluate(
                origMat,
                alignedMat,
                origGray,
                alignedGray,
                origCoarseEdges,
                restCoarseEdges,
                coarseResult.UnexpectedEdgeMap,
                origMedEdges,
                restMedEdges,
                medResult.UnexpectedEdgeMap,
                regModel,
                regConfidence,
                analysis,
                diagnosticsDirectory,
                assetBaseName,
                photographicEdgeEnhancementDetected);

            evidences.AddRange(negSpaceEval.Evidences);
            foreach (var kvp in negSpaceEval.DiagnosticPaths)
            {
                diagnosticPaths[kvp.Key] = kvp.Value;
            }

            furnitureValidationMask.Dispose();
            sceneValidationMask.Dispose();
            removableSceneMask.Dispose();
            uncertainMask.Dispose();
            furnitureBoundaryMask.Dispose();
            clusterMap.Dispose();
            alignedMat.Dispose();

            evidences.AddRange(OpenCvStructuralSafetyValidator.Validate(origGray, alignedGray, analysis));
            evidences.AddRange(OpenCvHardwareAbsenceValidator.Validate(origGray, alignedGray, analysis));
            evidences.AddRange(_cardinalityValidator.Validate(
                originalImagePath,
                restoredImagePath,
                analysis.ComponentCardinalityConstraints,
                diagnosticsDirectory,
                assetBaseName));
            var sceneColorMetrics = OpenCvSceneColorValidator.Validate(origMat, restMat, analysis.SceneColorReferences);
            evidences.AddRange(sceneColorMetrics.Evidences);

            // Phase 4E.2 Photographic Improvement Assessment
            var prelimReport = new LocalValidationReport
            {
                StructuralSimilarity = ssim,
                SharpnessGain = sharpnessGain,
                NoiseEstimateBefore = noiseBefore,
                NoiseEstimateAfter = noiseAfter,
                ContrastGain = contrastGain,
                DynamicRangeGain = dynamicRangeGain,
                HazeReduction = hazeReduction,
                ColorCastReduction = colorCastReduction
            };
            var improvementAssessment = QualityAnalyzer.AssessImprovement(sourceQualityAssessment, prelimReport);

            bool enableQualityGainEvaluation = _options != null &&
                (_options.RestorationProfile.Equals("HighQualityConservative", StringComparison.OrdinalIgnoreCase) ||
                 (_options.PhotographicEnhancementProfile == PhotographicEnhancementProfile.Strong &&
                  !string.Equals(_options.RestorationProfile, "Conservative", StringComparison.OrdinalIgnoreCase) &&
                  !string.Equals(_options.RestorationProfile, "Permissive", StringComparison.OrdinalIgnoreCase)));

            if (enableQualityGainEvaluation && improvementAssessment == PhotographicImprovementAssessment.Insufficient)
            {
                evidences.Add(new ValidationEvidence
                {
                    Category = ValidationEvidenceCategory.ImageQuality,
                    Type = "InsufficientPhotographicImprovement",
                    Score = ssim,
                    Threshold = 0.995,
                    Severity = ValidationSeverity.Warning,
                    Passed = false,
                    Message = "Source photograph exhibits clear optical/photographic defects, but candidate output remains nearly unchanged across quality dimensions. Restoration is photographically insufficient; automatic acceptance forbidden."
                });
            }

            return new LocalValidationReport
            {
                Passed = true, // Raw pass flag; ValidationDecisionEngine makes final evaluation
                RegistrationModel = regModel,
                RegistrationConfidence = regConfidence,
                StructuralSimilarity = ssim,
                EdgeSimilarity = edgeSimilarity,
                EdgePrecision = edgePrecision,
                EdgeRecall = edgeRecall,
                EdgeF1 = edgeF1,
                CoarseEdgeF1 = coarseF1,
                MediumEdgeF1 = medF1,
                MeanEdgeDistance = meanEdgeDistance,
                SilhouetteSimilarity = silhouetteSimilarity,
                RawColorDifference = rawColorDiff,
                NormalizedColorDistributionDifference = normColorDistDiff,
                ChangedAreaPercentage = changedAreaPercentage,
                LargestChangedRegionPercentage = largestRegionPercentage,
                ProtectedAreaChangedPercentage = protectedInteriorChangedPercentage,
                ProtectedInteriorAreaChangedPercentage = protectedInteriorChangedPercentage,
                ProtectedEdgeAlignedAreaChangedPercentage = protectedEdgeAlignedChangedPercentage,
                PhotographicEdgeEnhancementDetected = photographicEdgeEnhancementDetected,
                UnexpectedEdgeRatio = unexpectedEdgeRatio,
                MissingEdgeRatio = missingEdgeRatio,
                UnexpectedEdgePixelCount = unexpectedPixelCount,
                MissingEdgePixelCount = missingPixelCount,
                UnexpectedEdgeClusterCount = clusterCount,
                LargestUnexpectedEdgeClusterPixels = largestClusterPixels,
                LargestUnexpectedEdgeClusterRatio = largestClusterRatio,
                CoarseUnexpectedEdgeRatio = coarseUnexpRatio,
                MediumUnexpectedEdgeRatio = medUnexpRatio,

                // Phase 3.6 Explicit Mask Metrics
                FurnitureSsim = furnitureSsim,
                FurnitureEdgeSimilarity = furnitureEdgeSimilarity,
                FurnitureEdgePrecision = furnitureEdgePrecision,
                FurnitureEdgeRecall = furnitureEdgeRecall,
                FurnitureColorDifference = furnitureColorDiff,
                FurnitureChangedAreaPercentage = furnitureChangedAreaPercentage,
                SceneCleanupAreaPercentage = sceneCleanupAreaPercentage,
                SceneCleanupIntrusionPercentage = sceneCleanupIntrusionPercentage,
                BoundaryContinuityScore = boundaryContinuityScore,

                // Phase 3.6 Region-Aware & Material-Sensitive Color Metrics
                GlobalLightingNormalizedColorDifference = normColorDistDiff,
                FurnitureStructuralRegionColorDifference = furnitureColorDiff,
                MaterialSensitiveColorDifference = materialSensitiveColorDiff,
                HasMaterialColorIdentityViolation = hasMaterialColorViolation,
                AccentColorRegionPreservation = accentColorPreservation,

                // Phase 3.6 Overlap-Aware Reframing Metrics
                OriginalFrameCoverage = reframing.Coverage,
                ExpandedCanvasPercentage = reframing.CanvasPercentage,
                FurnitureScaleWithinOverlap = reframing.Scale,
                FurnitureAspectConsistency = reframing.AspectConsistency,
                FurnitureRelativeDimensionConsistency = reframing.RelativeDimConsistency,
                FurniturePerspectiveConsistency = reframing.PerspectiveConsistency,
                OutpaintAreaPercentage = reframing.OutpaintPercentage,
                BoundaryExpansionOccurred = reframing.ExpansionOccurred,
                ReframingSeverity = reframing.Severity,
                IsZoomOutAccepted = reframing.ZoomOutAccepted,

                // Phase 4C.1 & 4E.2 Photo Quality & Gain Metrics
                SharpnessBefore = sharpnessBefore,
                SharpnessAfter = sharpnessAfter,
                NoiseEstimateBefore = noiseBefore,
                NoiseEstimateAfter = noiseAfter,
                LocalContrastBefore = localContrastBefore,
                LocalContrastAfter = localContrastAfter,
                GlobalContrastBefore = globalContrastBefore,
                GlobalContrastAfter = globalContrastAfter,
                DynamicRangeBefore = dynamicRangeBefore,
                DynamicRangeAfter = dynamicRangeAfter,
                ExposureBefore = exposureBefore,
                ExposureAfter = exposureAfter,
                ExposureShift = exposureShift,
                HazeMetricBefore = hazeBefore,
                HazeMetricAfter = hazeAfter,
                ColorCastMetricBefore = colorCastBefore,
                ColorCastMetricAfter = colorCastAfter,
                WhiteBalanceShift = whiteBalanceShift,

                // Phase 4E.2 Gains
                SharpnessGain = sharpnessGain,
                ContrastGain = contrastGain,
                DynamicRangeGain = dynamicRangeGain,
                HazeReduction = hazeReduction,
                ColorCastReduction = colorCastReduction,
                PhotographicImprovement = improvementAssessment,

                // Phase 4C.1 Perspective & Straightening Metrics
                PerspectiveCorrectionDetected = perspectiveDetected,
                RotationCorrectionDegrees = rotationCorrectionDeg,
                PerspectiveTransformMagnitude = perspectiveTransformMag,
                CropPercentageAfterCorrection = cropPctAfterCorrection,
                FurnitureGeometryPreservedAfterRectification = geometryPreserved,

                // Phase 4C.3 Hardware Absence & Scene Color Identity Metrics
                HardwareAbsencePreserved = !evidences.Any(e => e.Type == "HardwareAbsenceViolated" && !e.Passed),
                PermanentSceneColorDeltaE = sceneColorMetrics.DeltaE,
                PermanentSceneHueShift = sceneColorMetrics.HueShift,
                PermanentSceneChromaRatio = sceneColorMetrics.ChromaRatio,
                PermanentSceneWarmCoolBiasDelta = sceneColorMetrics.WarmCoolBiasDelta,
                PermanentSceneLightnessChange = sceneColorMetrics.LightnessChange,
                PermanentSceneColorIdentityPreserved = sceneColorMetrics.ColorIdentityPreserved,

                ComponentResults = componentResults,
                Evidences = evidences,
                FailureReasons = failureReasons,
                DiagnosticImagePaths = diagnosticPaths,

                // Phase 4F.3 Negative Space, Unsupported Physical Additions & Cleanup Separation
                NegativeSpaceRegions = negSpaceEval.NegativeSpaceRegions,
                NegativeSpaceViolations = negSpaceEval.Violations,
                UnsupportedAdditions = negSpaceEval.Additions,
                CleanupTargets = negSpaceEval.CleanupTargets,
                CleanupCompletionStatus = negSpaceEval.CleanupStatus
            };
        }, cancellationToken);
    }










    private static double CalculateSilhouetteSimilarity(Mat gray1, Mat gray2, Mat? mask = null)
    {
        using var sil1 = new Mat();
        using var sil2 = new Mat();

        Cv2.Threshold(gray1, sil1, 0, 255, ThresholdTypes.Otsu | ThresholdTypes.Binary);
        Cv2.Threshold(gray2, sil2, 0, 255, ThresholdTypes.Otsu | ThresholdTypes.Binary);

        if (mask != null && !mask.Empty())
        {
            Cv2.BitwiseAnd(sil1, mask, sil1);
            Cv2.BitwiseAnd(sil2, mask, sil2);
        }

        using var inter = new Mat();
        using var uni = new Mat();
        Cv2.BitwiseAnd(sil1, sil2, inter);
        Cv2.BitwiseOr(sil1, sil2, uni);

        var interCount = Cv2.CountNonZero(inter);
        var uniCount = Cv2.CountNonZero(uni);

        if (uniCount == 0) return 1.0;
        return (double)interCount / uniCount;
    }



    private static (double MaxInteriorChange, double MaxEdgeAlignedChange) EvaluateProtectedAreas(
        Mat diffBinary,
        Mat edgeAlignedChanges,
        Mat interiorChanges,
        Mat origLab,
        Mat alignedLab,
        FurnitureAnalysis analysis,
        List<ValidationEvidence> evidences,
        double threshold)
    {
        double maxInteriorChange = 0.0;
        double maxEdgeAlignedChange = 0.0;

        foreach (var reg in analysis.ProtectedRegions)
        {
            var x = Math.Clamp((int)(reg.X * diffBinary.Width), 0, diffBinary.Width - 1);
            var y = Math.Clamp((int)(reg.Y * diffBinary.Height), 0, diffBinary.Height - 1);
            var w = Math.Clamp((int)(reg.Width * diffBinary.Width), 1, diffBinary.Width - x);
            var h = Math.Clamp((int)(reg.Height * diffBinary.Height), 1, diffBinary.Height - y);

            var rect = new Rect(x, y, w, h);
            using var subDiff = new Mat(diffBinary, rect);
            using var subEdgeAligned = new Mat(edgeAlignedChanges, rect);
            using var subInterior = new Mat(interiorChanges, rect);
            using var subOrigLab = new Mat(origLab, rect);
            using var subAlignedLab = new Mat(alignedLab, rect);

            var subPixels = w * h;
            var subTotalChanged = Cv2.CountNonZero(subDiff);
            var subEdgeChanged = Cv2.CountNonZero(subEdgeAligned);
            var subInteriorChanged = Cv2.CountNonZero(subInterior);

            var totalRatio = (double)subTotalChanged / subPixels;
            var edgeRatio = (double)subEdgeChanged / subPixels;
            var interiorRatio = (double)subInteriorChanged / subPixels;

            var (lumDelta, chromaDelta, chromaticDist) = OpenCvPhotometricMetrics.CalculateLabColorShift(subOrigLab, subAlignedLab);

            var minInteriorPixels = Math.Min(20, Math.Max(5, (int)(subPixels * 0.15)));
            var isSignificantInterior = interiorRatio > threshold && subInteriorChanged >= minInteriorPixels;

            if (edgeRatio > maxEdgeAlignedChange)
                maxEdgeAlignedChange = edgeRatio;

            if (isSignificantInterior)
            {
                if (chromaticDist > 8.0)
                {
                    if (interiorRatio > maxInteriorChange)
                        maxInteriorChange = interiorRatio;

                    evidences.Add(new ValidationEvidence
                    {
                        Category = ValidationEvidenceCategory.ProtectedRegion,
                        Type = "ComponentColorIdentityChanged",
                        Score = chromaticDist,
                        Threshold = 8.0,
                        Severity = ValidationSeverity.Critical,
                        Passed = false,
                        Region = reg.RegionType,
                        Message = $"Protected region '{reg.RegionType}' exhibited chromatic recoloring ({interiorRatio:P1} > {threshold:P1}, chromatic Delta C = {chromaticDist:F1} > 8.0)."
                    });
                }
                else
                {
                    evidences.Add(new ValidationEvidence
                    {
                        Category = ValidationEvidenceCategory.ProtectedRegion,
                        Type = "PhotometricLuminanceChange",
                        Score = interiorRatio,
                        Threshold = threshold,
                        Severity = ValidationSeverity.Warning,
                        Passed = false,
                        Region = reg.RegionType,
                        Message = $"Protected region '{reg.RegionType}' exhibited interior luminance shift ({interiorRatio:P1} > {threshold:P1}, {subInteriorChanged} interior px, chromatic Delta C = {chromaticDist:F1} <= 8.0)."
                    });
                }
            }
            else if (totalRatio > threshold)
            {
                evidences.Add(new ValidationEvidence
                {
                    Category = ValidationEvidenceCategory.ProtectedRegion,
                    Type = "EdgeAlignedPhotometricChange",
                    Score = edgeRatio,
                    Threshold = threshold,
                    Severity = ValidationSeverity.Info,
                    Passed = true,
                    Region = reg.RegionType,
                    Message = $"Protected region '{reg.RegionType}' exhibited edge-aligned photographic enhancement ({edgeRatio:P1} edge-aligned, {interiorRatio:P1} interior). Physical geometry preserved."
                });
            }
        }

        return (maxInteriorChange, maxEdgeAlignedChange);
    }



    private static void DrawProtectedRegions(Mat mat, FurnitureAnalysis analysis)
    {
        foreach (var reg in analysis.ProtectedRegions)
        {
            var x = (int)(reg.X * mat.Width);
            var y = (int)(reg.Y * mat.Height);
            var w = (int)(reg.Width * mat.Width);
            var h = (int)(reg.Height * mat.Height);
            Cv2.Rectangle(mat, new Rect(x, y, w, h), new Scalar(0, 255, 0), 2);
            Cv2.PutText(mat, reg.RegionType, new Point(x + 4, y + 16), HersheyFonts.HersheySimplex, 0.5, new Scalar(0, 255, 0), 1);
        }

        foreach (var comp in analysis.Components)
        {
            if (comp.Region != null)
            {
                var x = (int)(comp.Region.X * mat.Width);
                var y = (int)(comp.Region.Y * mat.Height);
                var w = (int)(comp.Region.Width * mat.Width);
                var h = (int)(comp.Region.Height * mat.Height);
                Cv2.Rectangle(mat, new Rect(x, y, w, h), new Scalar(0, 140, 255), 2);
                Cv2.PutText(mat, comp.Id, new Point(x + 4, y + 16), HersheyFonts.HersheySimplex, 0.5, new Scalar(0, 140, 255), 1);
            }
        }
    }




    private static double CalculateSsimMasked(Mat img1, Mat img2, Mat mask)
    {
        if (mask == null || Cv2.CountNonZero(mask) == 0)
            return OpenCvPhotometricMetrics.CalculateSsim(img1, img2);

        const double c1 = 6.5025;
        const double c2 = 58.5225;

        using var f1 = new Mat();
        using var f2 = new Mat();
        img1.ConvertTo(f1, MatType.CV_32F);
        img2.ConvertTo(f2, MatType.CV_32F);

        using var mu1 = new Mat();
        using var mu2 = new Mat();
        Cv2.GaussianBlur(f1, mu1, new Size(11, 11), 1.5);
        Cv2.GaussianBlur(f2, mu2, new Size(11, 11), 1.5);

        using var mu1Sq = mu1.Mul(mu1);
        using var mu2Sq = mu2.Mul(mu2);
        using var mu1Mu2 = mu1.Mul(mu2);

        using var f1Sq = f1.Mul(f1);
        using var f2Sq = f2.Mul(f2);
        using var f1F2 = f1.Mul(f2);

        using var sigma1Sq = new Mat();
        using var sigma2Sq = new Mat();
        using var sigma12 = new Mat();
        Cv2.GaussianBlur(f1Sq, sigma1Sq, new Size(11, 11), 1.5);
        Cv2.Subtract(sigma1Sq, mu1Sq, sigma1Sq);

        Cv2.GaussianBlur(f2Sq, sigma2Sq, new Size(11, 11), 1.5);
        Cv2.Subtract(sigma2Sq, mu2Sq, sigma2Sq);

        Cv2.GaussianBlur(f1F2, sigma12, new Size(11, 11), 1.5);
        Cv2.Subtract(sigma12, mu1Mu2, sigma12);

        using var t1 = new Mat();
        using var t2 = new Mat();
        Cv2.Add(mu1Mu2 * 2.0, new Scalar(c1), t1);
        Cv2.Add(sigma12 * 2.0, new Scalar(c2), t2);
        using var num = t1.Mul(t2);

        using var sigSum = new Mat();
        using var den1 = new Mat();
        using var den2 = new Mat();
        Cv2.Add(mu1Sq, mu2Sq, sigSum);
        Cv2.Add(sigSum, new Scalar(c1), den1);

        Cv2.Add(sigma1Sq, sigma2Sq, sigSum);
        Cv2.Add(sigSum, new Scalar(c2), den2);

        using var den = den1.Mul(den2);
        using var ssimMap = new Mat();
        Cv2.Divide(num, den, ssimMap);

        var mean = Cv2.Mean(ssimMap, mask);
        return Math.Clamp(mean.Val0, 0.0, 1.0);
    }

    private static (double RawDiff, double NormDiff) CalculateColorMetricsMasked(Mat lab1, Mat lab2, Mat mask)
    {
        if (mask == null || Cv2.CountNonZero(mask) == 0)
            return OpenCvPhotometricMetrics.CalculateColorMetrics(lab1, lab2);

        var mean1 = Cv2.Mean(lab1, mask);
        var mean2 = Cv2.Mean(lab2, mask);

        var dL = (mean1.Val0 - mean2.Val0) / 255.0;
        var da = (mean1.Val1 - mean2.Val1) / 255.0;
        var db = (mean1.Val2 - mean2.Val2) / 255.0;
        var rawDiff = Math.Sqrt(dL * dL + da * da + db * db);

        using var shifted2 = new Mat();
        Cv2.Subtract(lab2, new Scalar(mean2.Val0 - mean1.Val0, mean2.Val1 - mean1.Val1, mean2.Val2 - mean1.Val2), shifted2);

        using var diff = new Mat();
        Cv2.Absdiff(lab1, shifted2, diff);
        var diffMean = Cv2.Mean(diff, mask);
        var normDiff = (diffMean.Val0 + diffMean.Val1 + diffMean.Val2) / (3.0 * 255.0);

        return (rawDiff, normDiff);
    }

    public static double CalculateFurnitureStructuralColorDifference(
        Mat origLab,
        Mat alignedLab,
        Mat furnitureMask,
        FurnitureAnalysis analysis)
    {
        var (rawDiff, _) = CalculateColorMetricsMasked(origLab, alignedLab, furnitureMask);
        double maxStructuralDiff = rawDiff;

        foreach (var comp in analysis.Components)
        {
            if (!comp.IsStructural || comp.Region == null) continue;

            var rx = Math.Clamp((int)(comp.Region.X * origLab.Width), 0, origLab.Width - 1);
            var ry = Math.Clamp((int)(comp.Region.Y * origLab.Height), 0, origLab.Height - 1);
            var rw = Math.Clamp((int)(comp.Region.Width * origLab.Width), 1, origLab.Width - rx);
            var rh = Math.Clamp((int)(comp.Region.Height * origLab.Height), 1, origLab.Height - ry);
            if (rw < 8 || rh < 8) continue;

            using var sub1 = new Mat(origLab, new Rect(rx, ry, rw, rh));
            using var sub2 = new Mat(alignedLab, new Rect(rx, ry, rw, rh));
            var (cRaw, _) = OpenCvPhotometricMetrics.CalculateColorMetrics(sub1, sub2);

            using var diffSub = new Mat();
            Cv2.Absdiff(sub1, sub2, diffSub);
            var meanDiff = Cv2.Mean(diffSub);
            var pixelDiff = (meanDiff.Val0 + meanDiff.Val1 + meanDiff.Val2) / (3.0 * 255.0);

            var cVal = Math.Max(cRaw, pixelDiff);
            if (cVal > maxStructuralDiff)
                maxStructuralDiff = cVal;
        }

        return maxStructuralDiff;
    }

    public static double CalculateMaterialSensitiveColorDifference(
        Mat origLab,
        Mat alignedLab,
        FurnitureAnalysis analysis,
        int width,
        int height)
    {
        return CalculateMaterialSensitiveColorDifference(origLab, alignedLab, analysis, width, height, out _, out _);
    }

    public static double CalculateMaterialSensitiveColorDifference(
        Mat origLab,
        Mat alignedLab,
        FurnitureAnalysis analysis,
        int width,
        int height,
        out bool hasViolation,
        out string violationReason)
    {
        double maxMaterialDiff = 0.0;
        hasViolation = false;
        violationReason = string.Empty;

        foreach (var comp in analysis.Components)
        {
            if (comp.Region == null) continue;
            var matStr = (comp.Material ?? "").ToLowerInvariant();
            var typeStr = (comp.Type ?? "").ToLowerInvariant();
            bool isWoodOrStone = matStr.Contains("wood") || matStr.Contains("walnut") || matStr.Contains("mahogany") ||
                                matStr.Contains("oak") || matStr.Contains("marble") || matStr.Contains("stone") ||
                                matStr.Contains("granite") || matStr.Contains("quartz");

            bool isStructuralWoodComponent = comp.IsStructural ||
                                             typeStr.Contains("shelf") ||
                                             typeStr.Contains("panel") ||
                                             typeStr.Contains("division") ||
                                             typeStr.Contains("door") ||
                                             typeStr.Contains("countertop");

            if (!isWoodOrStone && !isStructuralWoodComponent) continue;

            var rx = Math.Clamp((int)(comp.Region.X * width), 0, width - 1);
            var ry = Math.Clamp((int)(comp.Region.Y * height), 0, height - 1);
            var rw = Math.Clamp((int)(comp.Region.Width * width), 1, width - rx);
            var rh = Math.Clamp((int)(comp.Region.Height * height), 1, height - ry);
            if (rw < 6 || rh < 6) continue;

            var rect = new Rect(rx, ry, rw, rh);
            using var subOrig = new Mat(origLab, rect);
            using var subRest = new Mat(alignedLab, rect);

            var m1 = Cv2.Mean(subOrig);
            var m2 = Cv2.Mean(subRest);

            var a1 = m1.Val1 - 128.0;
            var b1 = m1.Val2 - 128.0;
            var a2 = m2.Val1 - 128.0;
            var b2 = m2.Val2 - 128.0;

            var chroma1 = Math.Sqrt(a1 * a1 + b1 * b1);
            var chroma2 = Math.Sqrt(a2 * a2 + b2 * b2);

            // Compute hue difference only when chroma is significant to avoid angle noise on near-neutral grays
            double normHueDiff = 0.0;
            if (chroma1 >= 8.0 && chroma2 >= 8.0)
            {
                var hue1 = Math.Atan2(b1, a1);
                var hue2 = Math.Atan2(b2, a2);
                var hueDiff = Math.Abs(hue1 - hue2);
                if (hueDiff > Math.PI) hueDiff = 2 * Math.PI - hueDiff;
                normHueDiff = hueDiff / Math.PI;
            }

            // Material distance measures chromatic alteration (Delta a, Delta b), NOT luminance illumination gain (dL)
            var da = Math.Abs(a1 - a2) / 128.0;
            var db = Math.Abs(b1 - b2) / 128.0;
            var chromaDist = Math.Sqrt(da * da + db * db);

            var compDiff = Math.Max(normHueDiff, chromaDist);

            // Phase 4F.4 Material / Color Identity Shift Detection (Part 9):
            // Original furniture: genuine cool grey / neutral grey appearance (a1 <= 1.0, b1 <= 0.8, Chroma1 <= 2.2).
            // Candidate changed it toward warm amber/brown (a2 >= 2.5, b2 >= 2.2, Chroma2 >= 3.5, delta-a >= 2.5, delta-b >= 2.0).
            // This is a physical material/color identity violation (as demonstrated by CAL06).
            var deltaA = a2 - a1;
            var deltaB = b2 - b1;
            var deltaL = (m2.Val0 - m1.Val0) * (100.0 / 255.0);
            var deltaE = Math.Sqrt(deltaL * deltaL + deltaA * deltaA + deltaB * deltaB);

            bool isCoolGreyToBrownShift = Math.Abs(a1) <= 1.0 && b1 <= 0.8 && chroma1 <= 2.2 &&
                                          a2 >= 2.5 && b2 >= 2.2 && chroma2 >= 3.5 &&
                                          deltaA >= 2.5 && deltaB >= 2.0 && deltaE >= 10.0;

            if (isCoolGreyToBrownShift)
            {
                compDiff = Math.Max(compDiff, 0.35 + (deltaE / 100.0));
                hasViolation = true;
                violationReason = $"Furniture material color identity altered: original cool/neutral grey finish (a={a1:F1}, b={b1:F1}, Chroma={chroma1:F1}) was altered to warm brown (a={a2:F1}, b={b2:F1}, Chroma={chroma2:F1}, DeltaE={deltaE:F1}).";
            }

            if (compDiff > maxMaterialDiff)
                maxMaterialDiff = compDiff;
        }
        return maxMaterialDiff;
    }

    public static double CalculateAccentColorPreservation(
        Mat origLab,
        Mat alignedLab,
        FurnitureAnalysis analysis,
        int width,
        int height)
    {
        if (analysis.AccentColors.Count == 0 || analysis.ColorLocations.Count == 0)
            return 1.0;

        double preservation = 1.0;
        foreach (var comp in analysis.Components)
        {
            if (comp.IsStructural && comp.Region != null)
            {
                var rx = Math.Clamp((int)(comp.Region.X * width), 0, width - 1);
                var ry = Math.Clamp((int)(comp.Region.Y * height), 0, height - 1);
                var rw = Math.Clamp((int)(comp.Region.Width * width), 1, width - rx);
                var rh = Math.Clamp((int)(comp.Region.Height * height), 1, height - ry);
                if (rw < 6 || rh < 6) continue;

                var rect = new Rect(rx, ry, rw, rh);
                using var subOrig = new Mat(origLab, rect);
                using var subRest = new Mat(alignedLab, rect);

                var mean1 = Cv2.Mean(subOrig);
                var mean2 = Cv2.Mean(subRest);
                // Chromatic shift in a* and b* (independent of luminance L changes from exposure/dehazing)
                var da = (mean1.Val1 - mean2.Val1) / 255.0;
                var db = (mean1.Val2 - mean2.Val2) / 255.0;
                var chromaShift = Math.Sqrt(da * da + db * db);

                if (chromaShift > 0.020)
                {
                    preservation = Math.Min(preservation, Math.Max(0.0, 1.0 - (chromaShift - 0.020) * 8.0));
                }
            }
        }
        return Math.Clamp(preservation, 0.0, 1.0);
    }

    public static (
        double Coverage,
        double CanvasPercentage,
        double Scale,
        double AspectConsistency,
        double RelativeDimConsistency,
        double PerspectiveConsistency,
        double OutpaintPercentage,
        bool ExpansionOccurred,
        string Severity,
        bool ZoomOutAccepted)
    EvaluateOverlapReframing(
        Mat origRaw,
        Mat restRaw,
        RegistrationModel regModel,
        double regConfidence,
        double regScale,
        double regAspectConsistency,
        FurnitureAnalysis analysis)
    {
        double origW = origRaw.Width;
        double origH = origRaw.Height;
        double restW = restRaw.Width;
        double restH = restRaw.Height;

        var origAspect = origW / origH;
        var restAspect = restW / restH;
        var aspectDiff = Math.Abs(origAspect - restAspect) / origAspect;

        var framing = analysis.FramingAnalysis;
        bool isCropped = framing?.IsFurnitureCropped ?? false;
        var missingContext = framing?.MissingContextType ?? MissingContextType.SceneOnly;

        bool expansionOccurred = (restW > origW * 1.01) || (restH > origH * 1.01) || aspectDiff > 0.01;

        double coverage = Math.Clamp((origW * origH) / (restW * restH), 0.1, 1.0);
        double canvasPercentage = 1.0 - coverage;
        double scale = regScale;
        double aspectConsistency = regAspectConsistency;
        double relativeDimConsistency = Math.Clamp(scale, 0.0, 1.0);
        double perspectiveConsistency = regConfidence > 0 ? regConfidence : 1.0;
        double outpaintPercentage = expansionOccurred ? canvasPercentage : 0.0;
        string severity = framing?.CropSeverity.ToString() ?? "None";

        bool zoomOutAccepted = true;
        if (expansionOccurred)
        {
            if (framing == null || !framing.SafeZoomOutRecommended || !framing.IsFurnitureCropped)
            {
                // Unprompted canvas expansion / outpainting is forbidden
                zoomOutAccepted = false;
            }
            else if (missingContext == MissingContextType.FurnitureStructureCut)
            {
                // Automatic acceptance is forbidden if invisible furniture structure had to be generated
                zoomOutAccepted = false;
            }
            else if (framing?.CropSeverity == CropSeverity.Severe || framing?.CropSeverity == CropSeverity.Uncertain)
            {
                zoomOutAccepted = false;
            }
        }

        return (
            coverage,
            canvasPercentage,
            scale,
            aspectConsistency,
            relativeDimConsistency,
            perspectiveConsistency,
            outpaintPercentage,
            expansionOccurred,
            severity,
            zoomOutAccepted
        );
    }

    private static double CalculateDynamicRange(Mat gray)
    {
        int total = (int)gray.Total();
        if (total == 0) return 0.0;
        byte[] buffer = new byte[total];
        System.Runtime.InteropServices.Marshal.Copy(gray.Data, buffer, 0, total);
        var hist = new int[256];
        for (int i = 0; i < total; i++)
        {
            hist[buffer[i]]++;
        }

        int p1 = 0, p99 = 255;
        int cumSum = 0;
        bool foundP1 = false;
        for (int i = 0; i < 256; i++)
        {
            cumSum += hist[i];
            if (!foundP1 && cumSum >= total * 0.01)
            {
                p1 = i;
                foundP1 = true;
            }
            if (cumSum >= total * 0.99)
            {
                p99 = i;
                break;
            }
        }
        return Math.Max(0, p99 - p1);
    }
}
