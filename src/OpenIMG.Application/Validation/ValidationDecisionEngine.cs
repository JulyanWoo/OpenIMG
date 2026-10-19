using OpenIMG.Application.Configuration;
using OpenIMG.Core.Enums;
using OpenIMG.Core.Models;

namespace OpenIMG.Application.Validation;

public class ValidationDecisionEngine
{
    private readonly OpenImgOptions _options;
    private readonly LocalDecisionPolicy _localDecisionPolicy;
    private readonly RemoteDecisionCombiner _remoteDecisionCombiner;

    public ValidationDecisionEngine(
        OpenImgOptions options,
        LocalDecisionPolicy localDecisionPolicy,
        RemoteDecisionCombiner remoteDecisionCombiner)
    {
        _options = options;
        _localDecisionPolicy = localDecisionPolicy;
        _remoteDecisionCombiner = remoteDecisionCombiner;
    }

    /// <summary>
    /// Compatibility constructor retained for deterministic tests and legacy
    /// calibration tools. Production composition uses injected collaborators.
    /// </summary>
    public ValidationDecisionEngine(OpenImgOptions options)
        : this(
            options,
            new LocalDecisionPolicy(new ValidationEvidenceClassifier(options)),
            new RemoteDecisionCombiner())
    {
    }

    public ValidationDecision EvaluateLocalReport(LocalValidationReport report, out List<ValidationEvidence> evaluatedEvidences)
    {
        return EvaluateLocalReport(report, out evaluatedEvidences, out _);
    }

    public ValidationDecision EvaluateLocalReport(
        LocalValidationReport report,
        out List<ValidationEvidence> evaluatedEvidences,
        out DecisionExplanation explanation)
    {
        evaluatedEvidences = new List<ValidationEvidence>();

        // 1. Registration Model & Confidence
        if (report.RegistrationModel == RegistrationModel.None ||
            report.RegistrationConfidence < _options.MinImageRegistrationConfidence)
        {
            // Registration uncertainty must not hide a physical violation that
            // was already measured by the local validator.  Preserve critical
            // report evidence before the conservative early return so a remote
            // PASS cannot turn an unsafe candidate into Accepted.
            foreach (var critical in report.Evidences.Where(e => !e.Passed && e.Severity == ValidationSeverity.Critical))
            {
                evaluatedEvidences.Add(critical);
            }

            if (evaluatedEvidences.Count > 0)
            {
                explanation = new DecisionExplanation(
                    evaluatedEvidences.Select(e => e.Message).Distinct().ToList(),
                    new List<string> { $"Registration failed or confidence too low ({report.RegistrationConfidence:P1} < {_options.MinImageRegistrationConfidence:P1})." },
                    new List<string>());
                // Keep the Barrier 3 route reviewable so the normal policy can
                // still invoke comparative validation, but the preserved
                // critical evidence will block any remote PASS in CombineDecisions.
                return ValidationDecision.NeedsReview;
            }

            evaluatedEvidences.Add(new ValidationEvidence
            {
                Category = ValidationEvidenceCategory.Registration,
                Type = "ImageRegistration",
                Score = report.RegistrationConfidence,
                Threshold = _options.MinImageRegistrationConfidence,
                Severity = ValidationSeverity.Warning,
                Passed = false,
                Message = $"Registration failed or confidence too low ({report.RegistrationConfidence:P1} < {_options.MinImageRegistrationConfidence:P1})."
            });

            explanation = new DecisionExplanation(
                new List<string> { "None (Registration uncertainty requires review)" },
                new List<string> { $"Registration failed or confidence too low ({report.RegistrationConfidence:P1} < {_options.MinImageRegistrationConfidence:P1})." },
                new List<string>());

            // Registration uncertainty routes strictly to NeedsReview
            return ValidationDecision.NeedsReview;
        }

        if (report.RegistrationModel == RegistrationModel.Homography)
        {
            evaluatedEvidences.Add(new ValidationEvidence
            {
                Category = ValidationEvidenceCategory.Registration,
                Type = "RegistrationModel",
                Score = (int)report.RegistrationModel,
                Threshold = (int)RegistrationModel.Affine,
                Severity = ValidationSeverity.Warning,
                Passed = false,
                Message = "Registration required aggressive Homography fallback. Potential geometric warping detected; automatic acceptance forbidden."
            });
        }
        else
        {
            evaluatedEvidences.Add(new ValidationEvidence
            {
                Category = ValidationEvidenceCategory.Registration,
                Type = "ImageRegistration",
                Score = report.RegistrationConfidence,
                Threshold = _options.MinImageRegistrationConfidence,
                Severity = ValidationSeverity.Info,
                Passed = true,
                Message = $"Registration successful using {report.RegistrationModel} transform ({report.RegistrationConfidence:P1})."
            });
        }

        // 2. Aspect Ratio Consistency
        // If legitimate boundary expansion (zoom-out) occurred and is accepted, canvas aspect ratio is expected to change;
        // furniture aspect consistency is evaluated under Overlap-Aware Reframing (Section 7e).
        var isReframed = report.BoundaryExpansionOccurred && report.IsZoomOutAccepted;
        var aspectDiff = report.AspectRatioDifference;
        var aspectPassed = isReframed || report.BoundaryExpansionOccurred || aspectDiff <= _options.MaxAspectRatioDifference;
        var aspectSeverity = (isReframed || aspectPassed)
            ? ValidationSeverity.Info
            : (report.BoundaryExpansionOccurred ? ValidationSeverity.Warning : ValidationSeverity.Critical);

        evaluatedEvidences.Add(new ValidationEvidence
        {
            Category = ValidationEvidenceCategory.Geometry,
            Type = "AspectRatioConsistency",
            Score = aspectDiff,
            Threshold = _options.MaxAspectRatioDifference,
            Severity = aspectSeverity,
            Passed = aspectPassed,
            Message = isReframed
                ? $"Canvas aspect ratio changed due to accepted reframing ({aspectDiff:P1}); evaluated via furniture overlap aspect consistency."
                : (aspectPassed
                    ? $"Aspect ratio difference {aspectDiff:P1} is within threshold or accounted for by reframing."
                    : $"Aspect ratio changed significantly ({aspectDiff:P1} > {_options.MaxAspectRatioDifference:P1}).")
        });

        // 3. Structural Similarity (SSIM) - Prefer Masked Furniture SSIM when available
        var effectiveSsim = report.FurnitureSsim > 0 ? report.FurnitureSsim : report.StructuralSimilarity;
        var ssimPassed = effectiveSsim >= _options.MinStructuralSimilarity;
        evaluatedEvidences.Add(new ValidationEvidence
        {
            Category = ValidationEvidenceCategory.Structural,
            Type = "StructuralSimilarity",
            Score = effectiveSsim,
            Threshold = _options.MinStructuralSimilarity,
            Severity = ssimPassed ? ValidationSeverity.Info : ValidationSeverity.Warning,
            Passed = ssimPassed,
            Message = ssimPassed
                ? $"Furniture SSIM {effectiveSsim:F3} meets threshold ({_options.MinStructuralSimilarity:F3})."
                : $"Furniture SSIM {effectiveSsim:F3} fell below threshold ({_options.MinStructuralSimilarity:F3})."
        });

        // 4. Edge Similarity - Prefer Masked Furniture Edge Agreement when available (Tier C - Part 5 & 6)
        var effectiveEdgeSim = report.FurnitureEdgeSimilarity > 0 ? report.FurnitureEdgeSimilarity : report.EdgeSimilarity;
        var edgePassed = effectiveEdgeSim >= _options.MinEdgeSimilarity;
        evaluatedEvidences.Add(new ValidationEvidence
        {
            Category = ValidationEvidenceCategory.Structural,
            Type = "EdgeSimilarity",
            Score = effectiveEdgeSim,
            Threshold = _options.MinEdgeSimilarity,
            Severity = edgePassed ? ValidationSeverity.Info : ValidationSeverity.Warning,
            Passed = edgePassed,
            Message = edgePassed
                ? $"Furniture edge similarity {effectiveEdgeSim:P1} meets threshold ({_options.MinEdgeSimilarity:P1})."
                : $"Furniture edge similarity {effectiveEdgeSim:P1} fell below threshold ({_options.MinEdgeSimilarity:P1}). Low-level edge overlap difference."
        });

        // 4b. Edge Recall (Detects missing original structure - Tier B/C unless severe loss)
        var effectiveEdgeRecall = report.FurnitureEdgeRecall > 0 ? report.FurnitureEdgeRecall : report.EdgeRecall;
        if (effectiveEdgeRecall > 0 && effectiveEdgeRecall < _options.MinEdgeSimilarity)
        {
            var isSevereMissing = effectiveEdgeRecall < 0.60 && report.MissingEdgePixelCount > 300;
            evaluatedEvidences.Add(new ValidationEvidence
            {
                Category = ValidationEvidenceCategory.Structural,
                Type = "EdgeRecall",
                Score = effectiveEdgeRecall,
                Threshold = _options.MinEdgeSimilarity,
                Severity = isSevereMissing ? ValidationSeverity.Critical : ValidationSeverity.Warning,
                Passed = false,
                Message = isSevereMissing
                    ? $"Original structural edges missing from restored image ({effectiveEdgeRecall:P1} < {_options.MinEdgeSimilarity:P1}, {report.MissingEdgePixelCount} missing px). Critical physical boundary loss."
                    : $"Original structural edges partially displaced or softened ({effectiveEdgeRecall:P1} < {_options.MinEdgeSimilarity:P1})."
            });
        }

        // 4c. Edge Precision (Detects unexpected added structure - Tier C: photographic micro-edges)
        var effectiveEdgePrec = report.FurnitureEdgePrecision > 0 ? report.FurnitureEdgePrecision : report.EdgePrecision;
        if (effectiveEdgePrec > 0 && effectiveEdgePrec < _options.MinEdgeSimilarity)
        {
            evaluatedEvidences.Add(new ValidationEvidence
            {
                Category = ValidationEvidenceCategory.Structural,
                Type = "EdgePrecision",
                Score = effectiveEdgePrec,
                Threshold = _options.MinEdgeSimilarity,
                Severity = ValidationSeverity.Warning,
                Passed = false,
                Message = $"Unexpected fine edges detected in restored image ({effectiveEdgePrec:P1} < {_options.MinEdgeSimilarity:P1}). Possible added structure or micro-edge sharpening."
            });
        }

        // 4c2. Coarse Edge Structure (Priority over Medium Edge F1 for physical furniture geometry - Part 7)
        if (report.CoarseEdgeF1 > 0 && report.CoarseEdgeF1 < 0.35 && effectiveEdgeRecall < 0.85)
        {
            evaluatedEvidences.Add(new ValidationEvidence
            {
                Category = ValidationEvidenceCategory.Structural,
                Type = "CoarseEdgeStructureDisagreement",
                Score = report.CoarseEdgeF1,
                Threshold = 0.35,
                Severity = ValidationSeverity.Warning,
                Passed = false,
                Message = $"Major structural coarse edges exhibit substantial deviation ({report.CoarseEdgeF1:P1} < 35.0%) with missing original boundaries."
            });
        }

        // 4d. Unexpected Structural Edges Ratio
        if (report.UnexpectedEdgeRatio > _options.MaxAllowedUnexpectedEdgeRatio)
        {
            evaluatedEvidences.Add(new ValidationEvidence
            {
                Category = ValidationEvidenceCategory.Structural,
                Type = "UnexpectedEdgeRatio",
                Score = report.UnexpectedEdgeRatio,
                Threshold = _options.MaxAllowedUnexpectedEdgeRatio,
                Severity = ValidationSeverity.Warning,
                Passed = false,
                Message = $"Unexpected structural edges detected in restored image ({report.UnexpectedEdgeRatio:P1} > {_options.MaxAllowedUnexpectedEdgeRatio:P1}). Possible added structure."
            });
        }

        // 4e. Connected Unexpected Edge Clusters (Detects coherent localized structures)
        if (report.UnexpectedEdgeClusterCount > 0 && report.LargestUnexpectedEdgeClusterRatio > _options.MaxAllowedUnexpectedClusterRatio)
        {
            evaluatedEvidences.Add(new ValidationEvidence
            {
                Category = ValidationEvidenceCategory.Structural,
                Type = "UnexpectedEdgeCluster",
                Score = report.LargestUnexpectedEdgeClusterRatio,
                Threshold = _options.MaxAllowedUnexpectedClusterRatio,
                Severity = ValidationSeverity.Warning,
                Passed = false,
                Message = $"Unexpected localized structural cluster detected ({report.LargestUnexpectedEdgeClusterPixels} px, {report.LargestUnexpectedEdgeClusterRatio:P1} of edges). Structural inconsistency."
            });
        }

        // 5. Silhouette Similarity
        var silPassed = report.SilhouetteSimilarity >= _options.MinSilhouetteSimilarity;
        evaluatedEvidences.Add(new ValidationEvidence
        {
            Category = ValidationEvidenceCategory.Silhouette,
            Type = "SilhouetteSimilarity",
            Score = report.SilhouetteSimilarity,
            Threshold = _options.MinSilhouetteSimilarity,
            Severity = silPassed ? ValidationSeverity.Info : ValidationSeverity.Critical,
            Passed = silPassed,
            Message = silPassed
                ? $"Silhouette similarity {report.SilhouetteSimilarity:P1} meets threshold ({_options.MinSilhouetteSimilarity:P1})."
                : $"Furniture exterior silhouette altered ({report.SilhouetteSimilarity:P1} < {_options.MinSilhouetteSimilarity:P1})."
        });

        // 6. Normalized Color Distribution
        var colorDistPassed = report.NormalizedColorDistributionDifference <= _options.MaxAllowedNormalizedColorDistributionDifference;
        var furnitureColorPreserved = report.FurnitureStructuralRegionColorDifference <= _options.MaxAllowedFurnitureStructuralColorDifference &&
                                      report.MaterialSensitiveColorDifference <= _options.MaxAllowedMaterialSensitiveColorDifference;
        var colorPassed = colorDistPassed || furnitureColorPreserved;
        var colorSeverity = colorPassed ? ValidationSeverity.Info : ValidationSeverity.Critical;

        evaluatedEvidences.Add(new ValidationEvidence
        {
            Category = ValidationEvidenceCategory.Color,
            Type = "NormalizedColorDistribution",
            Score = report.NormalizedColorDistributionDifference,
            Threshold = _options.MaxAllowedNormalizedColorDistributionDifference,
            Severity = colorSeverity,
            Passed = colorPassed,
            Message = colorPassed
                ? $"Normalized color distribution difference {report.NormalizedColorDistributionDifference:P1} acceptable (furniture structural color preserved)."
                : $"Color distribution altered significantly ({report.NormalizedColorDistributionDifference:P1} > {_options.MaxAllowedNormalizedColorDistributionDifference:P1})."
        });

        // 6b. Phase 3.6: Furniture Structural Region Color Difference
        if (report.FurnitureStructuralRegionColorDifference > _options.MaxAllowedFurnitureStructuralColorDifference)
        {
            evaluatedEvidences.Add(new ValidationEvidence
            {
                Category = ValidationEvidenceCategory.Color,
                Type = "FurnitureStructuralColor",
                Score = report.FurnitureStructuralRegionColorDifference,
                Threshold = _options.MaxAllowedFurnitureStructuralColorDifference,
                Severity = ValidationSeverity.Critical,
                Passed = false,
                Message = $"Furniture structural color difference exceeded ({report.FurnitureStructuralRegionColorDifference:P1} > {_options.MaxAllowedFurnitureStructuralColorDifference:P1}). Potential furniture recoloring."
            });
        }

        // 6c. Phase 3.6 & Phase 4F.4: Material-Sensitive Color Difference (Wood/Melamine/Stone preservation)
        if (report.MaterialSensitiveColorDifference > _options.MaxAllowedMaterialSensitiveColorDifference ||
            report.HasMaterialColorIdentityViolation)
        {
            evaluatedEvidences.Add(new ValidationEvidence
            {
                Category = ValidationEvidenceCategory.Color,
                Type = report.HasMaterialColorIdentityViolation ? "MaterialColorIdentityViolation" : "MaterialSensitiveColor",
                Score = report.MaterialSensitiveColorDifference,
                Threshold = _options.MaxAllowedMaterialSensitiveColorDifference,
                Severity = ValidationSeverity.Critical,
                Passed = false,
                Message = $"Material-sensitive color shift detected ({report.MaterialSensitiveColorDifference:P1} > {_options.MaxAllowedMaterialSensitiveColorDifference:P1}). Material identity altered (e.g. wood or marble changed)."
            });
        }

        // 6d. Phase 3.6: Accent Color Region Preservation
        if (report.AccentColorRegionPreservation < _options.MinAccentColorRegionPreservation)
        {
            evaluatedEvidences.Add(new ValidationEvidence
            {
                Category = ValidationEvidenceCategory.Color,
                Type = "AccentColorPreservation",
                Score = report.AccentColorRegionPreservation,
                Threshold = _options.MinAccentColorRegionPreservation,
                Severity = ValidationSeverity.Critical,
                Passed = false,
                Message = $"Accent color expansion detected ({report.AccentColorRegionPreservation:P1} < {_options.MinAccentColorRegionPreservation:P1}). Accent colors spread into structural panels."
            });
        }

        // 7. Difference Map: Changed Area Percentage
        var effectiveChangedArea = (report.SceneCleanupAreaPercentage > 0 && report.FurnitureChangedAreaPercentage >= 0)
            ? report.FurnitureChangedAreaPercentage
            : report.ChangedAreaPercentage;
        var changedAreaPassed = effectiveChangedArea <= _options.MaxAllowedChangedAreaPercentage;
        evaluatedEvidences.Add(new ValidationEvidence
        {
            Category = ValidationEvidenceCategory.Geometry,
            Type = "ChangedAreaPercentage",
            Score = effectiveChangedArea,
            Threshold = _options.MaxAllowedChangedAreaPercentage,
            Severity = changedAreaPassed ? ValidationSeverity.Info : ValidationSeverity.Warning,
            Passed = changedAreaPassed,
            Message = changedAreaPassed
                ? $"Difference map changed area {effectiveChangedArea:P1} within bounds."
                : $"Large changed area detected in difference map ({effectiveChangedArea:P1} > {_options.MaxAllowedChangedAreaPercentage:P1})."
        });

        // 7b. Phase 3.6: Scene Cleanup Area Reporting (Scene Fidelity concept separated from Furniture Fidelity)
        if (report.SceneCleanupAreaPercentage > 0)
        {
            var isSceneCleanupAuthorized = _options.SceneCleanupMode != SceneCleanupMode.Disabled ||
                                           _options.PersonRemovalMode != PersonRemovalMode.Disabled ||
                                           _options.AllowReflectionCleanup;
            evaluatedEvidences.Add(new ValidationEvidence
            {
                Category = ValidationEvidenceCategory.Diagnostic,
                Type = isSceneCleanupAuthorized ? "SceneCleanupArea" : "UnauthorizedSceneModification",
                Score = report.SceneCleanupAreaPercentage,
                Threshold = 1.0,
                Severity = ValidationSeverity.Info,
                Passed = isSceneCleanupAuthorized,
                Message = isSceneCleanupAuthorized
                    ? $"Scene cleanup verified: non-furniture clutter removed ({report.SceneCleanupAreaPercentage:P1} of non-furniture area). Authorized by active profile."
                    : $"Scene modification detected ({report.SceneCleanupAreaPercentage:P1} of non-furniture area), but scene cleanup is disabled by the active cleanup policy. Identified as deviation from requested strict restoration; reported separately from furniture fidelity."
            });
        }

        // 7c. Phase 3.6: Scene Cleanup Boundary Intrusion
        if (report.SceneCleanupIntrusionPercentage > _options.MaxAllowedSceneCleanupIntrusionPercentage)
        {
            evaluatedEvidences.Add(new ValidationEvidence
            {
                Category = ValidationEvidenceCategory.Structural,
                Type = "SceneCleanupBoundaryIntrusion",
                Score = report.SceneCleanupIntrusionPercentage,
                Threshold = _options.MaxAllowedSceneCleanupIntrusionPercentage,
                Severity = ValidationSeverity.Warning,
                Passed = false,
                Message = $"Scene cleanup intruded into furniture boundary zone ({report.SceneCleanupIntrusionPercentage:P1} > {_options.MaxAllowedSceneCleanupIntrusionPercentage:P1}). Potential boundary erosion."
            });
        }

        // 7d. Phase 3.6: Boundary Continuity Score
        if (report.BoundaryContinuityScore < _options.MinBoundaryContinuityScore)
        {
            evaluatedEvidences.Add(new ValidationEvidence
            {
                Category = ValidationEvidenceCategory.Structural,
                Type = "BoundaryContinuity",
                Score = report.BoundaryContinuityScore,
                Threshold = _options.MinBoundaryContinuityScore,
                Severity = ValidationSeverity.Warning,
                Passed = false,
                Message = $"Furniture boundary continuity compromised ({report.BoundaryContinuityScore:P1} < {_options.MinBoundaryContinuityScore:P1})."
            });
        }

        // Independent structural signals corroborate a physical addition (Part 8 & 17); high SSIM/recall cannot average it away.
        var isPhotographicSharpeningEnhancement = (report.PhotographicEdgeEnhancementDetected ||
                                                   report.PhotographicImprovement != PhotographicImprovementAssessment.Insufficient ||
                                                   report.SharpnessGain >= 0.50 ||
                                                   report.HazeReduction >= 10.0) &&
                                                  effectiveEdgeRecall >= 0.90 &&
                                                  (report.SilhouetteSimilarity >= _options.MinSilhouetteSimilarity || report.FurnitureSsim >= 0.70) &&
                                                  report.HardwarePreserved;

        var severeStructuralConflict = !isPhotographicSharpeningEnhancement &&
                                      report.UnexpectedEdgeRatio > _options.MaxAllowedUnexpectedEdgeRatio &&
                                      report.LargestUnexpectedEdgeClusterRatio > _options.MaxAllowedUnexpectedClusterRatio &&
                                      report.FurnitureEdgePrecision > 0 && report.FurnitureEdgePrecision < _options.MinEdgeSimilarity;
        if (severeStructuralConflict)
        {
            evaluatedEvidences.Add(new ValidationEvidence
            {
                Category = ValidationEvidenceCategory.Structural, Type = "SevereStructuralDeviation",
                Score = Math.Max(report.UnexpectedEdgeRatio, report.LargestUnexpectedEdgeClusterRatio), Threshold = 0,
                Severity = ValidationSeverity.Critical, Passed = false,
                Message = "Corroborating unexpected-edge, coherent-cluster, and poor precision signals indicate a physical structural addition; automatic acceptance is forbidden."
            });
        }

        // 7e. Phase 3.6: Overlap-Aware Reframing & Zoom-Out
        if (report.BoundaryExpansionOccurred)
        {
            if (!_options.AllowZoomOut && !_options.AllowOutpainting)
            {
                evaluatedEvidences.Add(new ValidationEvidence
                {
                    Category = ValidationEvidenceCategory.Diagnostic,
                    Type = "UnauthorizedReframingModification",
                    Score = report.ExpandedCanvasPercentage,
                    Threshold = 0.0,
                    Severity = ValidationSeverity.Info,
                    Passed = false,
                    Message = $"Reframing / zoom-out detected ({report.ExpandedCanvasPercentage:P1}) but zoom-out was NOT authorized by active profile '{_options.RestorationProfile}'. Identified as deviation from requested strict restoration; reported separately from furniture fidelity."
                });
            }

            if (!report.IsZoomOutAccepted)
            {
                evaluatedEvidences.Add(new ValidationEvidence
                {
                    Category = ValidationEvidenceCategory.Geometry,
                    Type = "SpeculativeReframing",
                    Score = 0.0,
                    Threshold = 1.0,
                    Severity = ValidationSeverity.Warning,
                    Passed = false,
                    Message = "Automatic acceptance forbidden: furniture structure was cut off and reconstructed speculatively. Reframing requires human review."
                });
            }

            if (report.FurnitureAspectConsistency < _options.MinFurnitureAspectConsistency)
            {
                evaluatedEvidences.Add(new ValidationEvidence
                {
                    Category = ValidationEvidenceCategory.Geometry,
                    Type = "ReframingAspectDistortion",
                    Score = report.FurnitureAspectConsistency,
                    Threshold = _options.MinFurnitureAspectConsistency,
                    Severity = ValidationSeverity.Critical,
                    Passed = false,
                    Message = $"Furniture aspect ratio distorted during reframing ({report.FurnitureAspectConsistency:P1} < {_options.MinFurnitureAspectConsistency:P1})."
                });
            }

            if (report.FurniturePerspectiveConsistency < _options.MinFurniturePerspectiveConsistency)
            {
                evaluatedEvidences.Add(new ValidationEvidence
                {
                    Category = ValidationEvidenceCategory.Geometry,
                    Type = "ReframingPerspectiveDistortion",
                    Score = report.FurniturePerspectiveConsistency,
                    Threshold = _options.MinFurniturePerspectiveConsistency,
                    Severity = ValidationSeverity.Warning,
                    Passed = false,
                    Message = $"Furniture perspective consistency low during reframing ({report.FurniturePerspectiveConsistency:P1} < {_options.MinFurniturePerspectiveConsistency:P1})."
                });
            }

            if (report.IsZoomOutAccepted && report.FurnitureAspectConsistency >= _options.MinFurnitureAspectConsistency)
            {
                evaluatedEvidences.Add(new ValidationEvidence
                {
                    Category = ValidationEvidenceCategory.Geometry,
                    Type = "SafeZoomOut",
                    Score = report.FurnitureAspectConsistency,
                    Threshold = _options.MinFurnitureAspectConsistency,
                    Severity = ValidationSeverity.Info,
                    Passed = true,
                    Message = $"Safe conservative zoom-out validated with preserved furniture proportions ({report.FurnitureAspectConsistency:P1} aspect consistency)."
                });
            }
        }

        // 8. Difference Map: Largest Connected Changed Region (Tier B/C unless accompanied by geometry disruption)
        var isPhotographicTonalShift = (report.PhotographicEdgeEnhancementDetected || report.SharpnessGain >= 1.5 || report.HazeReduction >= 10.0 || Math.Abs(report.ExposureShift) > 5.0) &&
                                       report.SilhouetteSimilarity >= _options.MinSilhouetteSimilarity &&
                                       report.FurnitureStructuralRegionColorDifference <= _options.MaxAllowedFurnitureStructuralColorDifference;

        var largestRegionPassed = report.LargestChangedRegionPercentage <= _options.MaxAllowedLargestChangedRegionPercentage || isPhotographicTonalShift;
        var largestRegionSeverity = (largestRegionPassed || isPhotographicTonalShift)
            ? (largestRegionPassed ? ValidationSeverity.Info : ValidationSeverity.Warning)
            : ValidationSeverity.Critical;

        evaluatedEvidences.Add(new ValidationEvidence
        {
            Category = ValidationEvidenceCategory.Structural,
            Type = "LargestChangedRegion",
            Score = report.LargestChangedRegionPercentage,
            Threshold = _options.MaxAllowedLargestChangedRegionPercentage,
            Severity = largestRegionSeverity,
            Passed = largestRegionPassed,
            Message = largestRegionPassed
                ? (isPhotographicTonalShift
                    ? $"Contiguous changed region {report.LargestChangedRegionPercentage:P1} corresponds to photographic tonal/contrast recovery across panels."
                    : $"Largest contiguous changed region {report.LargestChangedRegionPercentage:P1} within bounds.")
                : $"Large localized alteration block detected ({report.LargestChangedRegionPercentage:P1} > {_options.MaxAllowedLargestChangedRegionPercentage:P1}). Likely panel/door alteration."
        });

        // 9. Protected Components / Regions Interior Change
        var isPhotometricProtectedShift = (report.PhotographicEdgeEnhancementDetected || report.ProtectedEdgeAlignedAreaChangedPercentage > 0) &&
                                          report.FurnitureStructuralRegionColorDifference <= _options.MaxAllowedFurnitureStructuralColorDifference;

        var protectedAreaPassed = report.ProtectedAreaChangedPercentage <= _options.MaxAllowedProtectedAreaChangedPercentage || isPhotometricProtectedShift;
        var protectedSeverity = (protectedAreaPassed || isPhotometricProtectedShift)
            ? (protectedAreaPassed ? ValidationSeverity.Info : ValidationSeverity.Warning)
            : ValidationSeverity.Critical;

        evaluatedEvidences.Add(new ValidationEvidence
        {
            Category = ValidationEvidenceCategory.ProtectedRegion,
            Type = "ProtectedAreaChangedPercentage",
            Score = report.ProtectedAreaChangedPercentage,
            Threshold = _options.MaxAllowedProtectedAreaChangedPercentage,
            Severity = protectedSeverity,
            Passed = protectedAreaPassed,
            Message = protectedAreaPassed
                ? (isPhotometricProtectedShift
                    ? $"Protected regions exhibited photometric enhancement ({report.ProtectedAreaChangedPercentage:P1} interior, {report.ProtectedEdgeAlignedAreaChangedPercentage:P1} edge-aligned); color identity preserved."
                    : $"Protected regions interior change {report.ProtectedAreaChangedPercentage:P1} within bounds.")
                : $"Protected furniture component interior modification detected ({report.ProtectedAreaChangedPercentage:P1} > {_options.MaxAllowedProtectedAreaChangedPercentage:P1})."
        });

        if (report.ProtectedEdgeAlignedAreaChangedPercentage > _options.MaxAllowedProtectedAreaChangedPercentage)
        {
            evaluatedEvidences.Add(new ValidationEvidence
            {
                Category = ValidationEvidenceCategory.ProtectedRegion,
                Type = "EdgeAlignedPhotometricChange",
                Score = report.ProtectedEdgeAlignedAreaChangedPercentage,
                Threshold = _options.MaxAllowedProtectedAreaChangedPercentage,
                Severity = ValidationSeverity.Info,
                Passed = true,
                Message = $"Protected regions exhibited edge-aligned photographic enhancement ({report.ProtectedEdgeAlignedAreaChangedPercentage:P1}); interior area preserved ({report.ProtectedAreaChangedPercentage:P1} <= {_options.MaxAllowedProtectedAreaChangedPercentage:P1})."
            });
        }

        // 10. Hardware & Component Level Invariants
        foreach (var comp in report.ComponentResults)
        {
            var isHardware = (comp.ComponentType ?? "").Contains("handle", StringComparison.OrdinalIgnoreCase) ||
                             (comp.ComponentType ?? "").Contains("hinge", StringComparison.OrdinalIgnoreCase) ||
                             (comp.ComponentType ?? "").Contains("strut", StringComparison.OrdinalIgnoreCase) ||
                             (comp.ComponentType ?? "").Contains("rail", StringComparison.OrdinalIgnoreCase) ||
                             comp.ComponentId.Contains("handle", StringComparison.OrdinalIgnoreCase) ||
                             comp.ComponentId.Contains("hinge", StringComparison.OrdinalIgnoreCase);

            var hardwareInteriorChanged = comp.InteriorChangedAreaRatio > 0 ? comp.InteriorChangedAreaRatio : comp.ChangedAreaPercentage;
            if (isHardware && (comp.MissingEdgeRatio > 0.40 || (hardwareInteriorChanged > 0.15 && comp.InteriorChangedPixels >= 20)))
            {
                evaluatedEvidences.Add(new ValidationEvidence
                {
                    Category = ValidationEvidenceCategory.Component,
                    Type = "CriticalHardwareRemoved",
                    Score = comp.MissingEdgeRatio > 0.40 ? comp.MissingEdgeRatio : hardwareInteriorChanged,
                    Threshold = 0.40,
                    Severity = ValidationSeverity.Critical,
                    Passed = false,
                    Region = comp.ComponentId,
                    Message = $"Critical furniture hardware element '{comp.ComponentId}' was removed or altered ({comp.MissingEdgeRatio:P1} missing edges, {hardwareInteriorChanged:P1} interior change)."
                });
            }

            // Glass reflection rule: glass frame must be preserved, but reflection content may vary
            var isGlass = (comp.ComponentType ?? "").Contains("glass", StringComparison.OrdinalIgnoreCase) ||
                          comp.ComponentId.Contains("glass", StringComparison.OrdinalIgnoreCase);
            if (isGlass)
            {
                if (comp.MissingEdgeRatio > 0.45)
                {
                    evaluatedEvidences.Add(new ValidationEvidence
                    {
                        Category = ValidationEvidenceCategory.Component,
                        Type = "GlassFrameAltered",
                        Score = comp.MissingEdgeRatio,
                        Threshold = 0.45,
                        Severity = ValidationSeverity.Critical,
                        Passed = false,
                        Region = comp.ComponentId,
                        Message = $"Glass frame or boundary geometry altered in component '{comp.ComponentId}'."
                    });
                }
                else if (comp.NormalizedColorDifference > 0.10)
                {
                    evaluatedEvidences.Add(new ValidationEvidence
                    {
                        Category = ValidationEvidenceCategory.Component,
                        Type = "GlassReflectionRemoved",
                        Score = comp.NormalizedColorDifference,
                        Threshold = 0.10,
                        Severity = ValidationSeverity.Info,
                        Passed = true,
                        Region = comp.ComponentId,
                        Message = $"Incidental reflection removed from glass component '{comp.ComponentId}' safely."
                    });
                }
            }
        }

        // Include any sub-evidences already generated by local validator
        foreach (var ev in report.Evidences)
        {
            evaluatedEvidences.Add(ev);
        }

        if (report.CleanupCompletionStatus == CleanupCompletionStatus.Incomplete &&
            !evaluatedEvidences.Any(e => e.Type == "CleanupIncomplete"))
        {
            evaluatedEvidences.Add(new ValidationEvidence
            {
                Category = ValidationEvidenceCategory.Diagnostic,
                Type = "CleanupIncomplete",
                Score = 0.5,
                Threshold = 1.0,
                Severity = ValidationSeverity.Warning,
                Passed = false,
                Message = "Scene cleanup incomplete: temporary non-structural background clutter was not removed, but furniture structural fidelity is preserved."
            });
        }

        // Collect non-blocking photographic improvement notes (Part 13)
        var photographicNotes = new List<string>();
        if (report.SharpnessGain >= 1.5)
            photographicNotes.Add($"Sharpening enhancement (gain: +{report.SharpnessGain:P1})");
        if (report.ContrastGain != 0)
            photographicNotes.Add($"Contrast recovery (gain: {report.ContrastGain:P1})");
        if (report.HazeReduction >= 5.0)
            photographicNotes.Add($"Dehazing / optical recovery ({report.HazeReduction:F1} haze metric drop)");
        if (report.PhotographicEdgeEnhancementDetected)
            photographicNotes.Add("Edge-aligned photometric enhancement along existing boundaries");
        if (effectiveEdgeRecall >= 0.95 && effectiveEdgeSim < _options.MinEdgeSimilarity)
            photographicNotes.Add("Low global edge similarity consistent with strong photographic gradient recovery while original edge recall is preserved");
        if (report.PermanentSceneColorIdentityPreserved && report.MaterialSensitiveColorDifference == 0)
            photographicNotes.Add("Color identity and material textures preserved (zero recoloring)");

        return _localDecisionPolicy.Evaluate(report, evaluatedEvidences, photographicNotes, out explanation);
    }

    public ValidationDecision CombineDecisions(
        ValidationDecision localDecision,
        GeminiValidationReport? remoteReport,
        out List<ValidationEvidence> finalEvidences,
        IEnumerable<ValidationEvidence> localEvidences,
        bool allowSemanticResolution = false)
    {
        return _remoteDecisionCombiner.Combine(
            localDecision,
            remoteReport,
            out finalEvidences,
            localEvidences,
            allowSemanticResolution);
    }

}
