using OpenIMG.Application.Abstractions.Validation;
using OpenIMG.Application.Configuration;
using OpenIMG.Core.Enums;
using OpenIMG.Core.Models;

namespace OpenIMG.Application.Validation;

public class RemoteValidationPolicy : IRemoteValidationPolicy
{
    private readonly OpenImgOptions _options;

    public RemoteValidationPolicy(OpenImgOptions options)
    {
        _options = options;
    }

    public RemoteValidationResult Evaluate(
        LocalValidationReport localReport,
        ValidationDecision localDecision,
        LocalFurnitureContract? localContract = null,
        LocalAnalysisRisk? analysisRisk = null)
    {
        // Rule A: Local Rejection is deterministic and blocking.
        // Gemini comparative validation must NEVER be invoked to override a physical local rejection.
        if (localDecision == ValidationDecision.Rejected)
        {
            return new RemoteValidationResult
            {
                Requirement = RemoteValidationRequirement.BlockedByLocalRejection,
                Reasons = new List<RemoteValidationReason> { RemoteValidationReason.LocalRejected },
                Explanation = "Local deterministic evidence found a blocking physical violation (Barrier 3 Rejected). Gemini comparative validation is blocked."
            };
        }

        // Rule B: Barrier 3 NeedsReview requires Barrier 4 escalation by default.
        if (localDecision == ValidationDecision.NeedsReview)
        {
            var reviewReasons = new List<RemoteValidationReason> { RemoteValidationReason.Barrier3NeedsReview };

            // Registration uncertainty
            if (localReport.RegistrationModel == RegistrationModel.Homography ||
                localReport.RegistrationConfidence < _options.MinImageRegistrationConfidence)
            {
                reviewReasons.Add(RemoteValidationReason.RegistrationUncertain);
            }
            else if (localReport.RegistrationModel == RegistrationModel.Affine)
            {
                reviewReasons.Add(RemoteValidationReason.AffineRegistrationUsed);
            }
            else if (localReport.RegistrationConfidence < 0.70)
            {
                reviewReasons.Add(RemoteValidationReason.RegistrationNearUncertaintyBoundary);
            }

            // Hardware ambiguity or cardinality
            if (localContract?.HardwareAbsenceConstraints.Count > 0 ||
                localReport.ComponentResults.Any(c => IsHardwareComponent(c) && c.MissingEdgeRatio > 0.20))
            {
                reviewReasons.Add(RemoteValidationReason.HardwareCardinalityAmbiguous);
            }
            else if (localContract?.HardwareCandidates.Count > 0)
            {
                reviewReasons.Add(RemoteValidationReason.HardwareAmbiguous);
            }

            // Protected absence
            if (localContract?.HardwareAbsenceConstraints.Count > 0 ||
                localContract?.ProtectedAbsenceRegions.Count > 0 ||
                !localReport.HardwareAbsencePreserved)
            {
                reviewReasons.Add(RemoteValidationReason.ProtectedAbsenceNearBoundary);
            }

            // Material / Glass
            if (localReport.ComponentResults.Any(IsGlassComponent) ||
                localContract?.MaterialHints.Any(m => m.Material == MaterialHintType.GlassLikeReflective) == true)
            {
                reviewReasons.Add(RemoteValidationReason.GlassOrReflectionComplexity);
            }

            if (localReport.MaterialSensitiveColorDifference > 0.10)
            {
                reviewReasons.Add(RemoteValidationReason.MaterialSensitiveSurfaceHigh);
            }

            // Color identity
            if (!localReport.PermanentSceneColorIdentityPreserved ||
                localReport.PermanentSceneColorDeltaE > 7.0 ||
                localReport.NormalizedColorDistributionDifference > 0.20)
            {
                reviewReasons.Add(RemoteValidationReason.ColorIdentityNearBoundary);
            }

            // Persistent details
            if (localContract?.PersistentSceneDetails.Count > 0)
            {
                reviewReasons.Add(RemoteValidationReason.PersistentSceneDetailRisk);
            }

            // Metric conflict / Unexpected edges
            if (localReport.UnexpectedEdgeRatio > _options.MaxAllowedUnexpectedEdgeRatio)
            {
                reviewReasons.Add(RemoteValidationReason.MetricConflict);
            }

            // Uncertain region intrusion
            if (localReport.SceneCleanupIntrusionPercentage > 0.01 || localReport.LargestUnexpectedEdgeClusterPixels > 50)
            {
                reviewReasons.Add(RemoteValidationReason.UncertainRegionIntrusion);
            }

            // Phase 4F.3 Scene Cleanup Incomplete
            if (localReport.IsCleanupIncomplete)
            {
                reviewReasons.Add(RemoteValidationReason.SceneCleanupIncomplete);
            }

            // Phase 4F.3 Uncertain Physical Additions or Negative Space Violations
            if (localReport.UnsupportedAdditions.Any(a => a.Severity == ValidationSeverity.Warning))
            {
                reviewReasons.Add(RemoteValidationReason.UnsupportedPhysicalAdditionUncertain);
            }

            if (localReport.NegativeSpaceViolations.Any(v => v.ProtectionLevel != ConstraintAuthority.Strict))
            {
                reviewReasons.Add(RemoteValidationReason.ProtectedNegativeSpaceUncertain);
            }

            return new RemoteValidationResult
            {
                Requirement = RemoteValidationRequirement.Required,
                Reasons = reviewReasons,
                Explanation = $"Barrier 3 produced NeedsReview; Gemini comparative validation required for semantic escalation ({string.Join(", ", reviewReasons)})."
            };
        }

        // Rule C: Barrier 3 Accepted candidate - may skip Barrier 4 ONLY if LOW RISK.
        var highRiskReasons = new List<RemoteValidationReason>();

        // 1. Registration
        if (localReport.RegistrationModel == RegistrationModel.Affine)
        {
            highRiskReasons.Add(RemoteValidationReason.AffineRegistrationUsed);
        }
        else if (localReport.RegistrationConfidence < 0.70)
        {
            highRiskReasons.Add(RemoteValidationReason.RegistrationNearUncertaintyBoundary);
        }

        // 2. Hardware cardinality and complexity
        if (localContract?.HardwareAbsenceConstraints.Count > 0)
        {
            highRiskReasons.Add(RemoteValidationReason.HardwareCardinalityAmbiguous);
        }

        if (localContract?.HardwareCandidates.Count > 0 ||
            localReport.ComponentResults.Any(IsHardwareComponent))
        {
            highRiskReasons.Add(RemoteValidationReason.ProtectedHardwarePresent);
        }

        if (localContract?.HardwareCandidates.Count > 2)
        {
            highRiskReasons.Add(RemoteValidationReason.HardwareComplexityHigh);
        }
        else if (localContract?.HardwareCandidates.Any(h => h.Confidence < 0.80) == true)
        {
            highRiskReasons.Add(RemoteValidationReason.HardwareAmbiguous);
        }

        // 3. Protected absence
        if (localContract?.HardwareAbsenceConstraints.Count > 0 || localContract?.ProtectedAbsenceRegions.Count > 0)
        {
            highRiskReasons.Add(RemoteValidationReason.ProtectedAbsenceNearBoundary);
        }

        // 4. Glass / specularity
        if (localReport.ComponentResults.Any(IsGlassComponent) ||
            localContract?.MaterialHints.Any(m => m.Material == MaterialHintType.GlassLikeReflective) == true)
        {
            highRiskReasons.Add(RemoteValidationReason.GlassOrReflectionComplexity);
        }

        // 5. Material sensitive
        if (localReport.MaterialSensitiveColorDifference > 0.10)
        {
            highRiskReasons.Add(RemoteValidationReason.MaterialSensitiveSurfaceHigh);
        }
        else if (localContract?.MaterialHints.Any(m => m.Material is MaterialHintType.WoodLikeTexture or MaterialHintType.MetalLikeHighlight) == true)
        {
            highRiskReasons.Add(RemoteValidationReason.MaterialSensitive);
        }

        // 6. Color identity
        if (localReport.NormalizedColorDistributionDifference > 0.20 ||
            localReport.PermanentSceneColorDeltaE > 7.0 ||
            localReport.PermanentSceneChromaRatio < 0.80)
        {
            highRiskReasons.Add(RemoteValidationReason.ColorIdentityNearBoundary);
        }

        // 7. Persistent scene details
        if (localContract?.PersistentSceneDetails.Count > 0)
        {
            highRiskReasons.Add(RemoteValidationReason.PersistentSceneDetailRisk);
        }

        // 8. Scene complexity with furniture overlap (generic clutter alone does NOT trigger high risk)
        if (localContract != null && localContract.SceneComplexity > 0.60)
        {
            var hasFurnitureOverlap = (localContract.FurnitureBounds != null &&
                                       localContract.SceneObjects.Any(s => s.Region.CalculateIoU(localContract.FurnitureBounds) > 0.10)) ||
                                      localReport.SceneCleanupAreaPercentage > 0.05;
            if (hasFurnitureOverlap)
            {
                highRiskReasons.Add(RemoteValidationReason.HighSceneComplexity);
            }
        }

        // 9. Component coverage
        if (localContract != null && (localContract.StructuralRegions.Count < 2 || localContract.ConfidenceBreakdown.StructuralCoverageConfidence < 0.60))
        {
            highRiskReasons.Add(RemoteValidationReason.ComponentCoverageInsufficient);
        }

        // 10. Low semantic confidence
        if (analysisRisk == LocalAnalysisRisk.High || (localContract != null && localContract.LocalConfidence < 0.60))
        {
            highRiskReasons.Add(RemoteValidationReason.LowSemanticConfidence);
        }

        // 11. Uncertain region intrusion
        if (localReport.SceneCleanupIntrusionPercentage > 0.01 || localReport.LargestUnexpectedEdgeClusterPixels > 50)
        {
            highRiskReasons.Add(RemoteValidationReason.UncertainRegionIntrusion);
        }

        // 12. Metric conflict
        if ((localReport.StructuralSimilarity > 0.90 && localReport.FurnitureEdgePrecision > 0 && localReport.FurnitureEdgePrecision < 0.70) ||
            (localReport.SilhouetteSimilarity > 0.95 && localReport.FurnitureChangedAreaPercentage > 0.08))
        {
            highRiskReasons.Add(RemoteValidationReason.MetricConflict);
        }

        // Deduplicate reasons
        highRiskReasons = highRiskReasons.Distinct().ToList();

        // Evaluate High-Risk vs Low-Risk Accepted
        if (highRiskReasons.Count > 0 && _options.RequireRemoteValidationForHighRiskAccepted)
        {
            var reasons = new List<RemoteValidationReason> { RemoteValidationReason.HighRiskAccepted };
            reasons.AddRange(highRiskReasons);

            return new RemoteValidationResult
            {
                Requirement = RemoteValidationRequirement.Required,
                Reasons = reasons,
                Explanation = $"Accepted candidate contains high-risk physical or structural conditions; Gemini comparative validation required ({string.Join(", ", highRiskReasons)})."
            };
        }

        // Low-Risk Accepted Path: skip Barrier 4 safely!
        return new RemoteValidationResult
        {
            Requirement = RemoteValidationRequirement.NotRequired,
            Reasons = new List<RemoteValidationReason> { RemoteValidationReason.LowRiskAccepted },
            Explanation = "Skipped because local validation produced a low-risk acceptance."
        };
    }

    private static bool IsHardwareComponent(ComponentValidationResult c)
    {
        return (c.ComponentType ?? "").Contains("handle", StringComparison.OrdinalIgnoreCase) ||
               (c.ComponentType ?? "").Contains("hinge", StringComparison.OrdinalIgnoreCase) ||
               (c.ComponentType ?? "").Contains("strut", StringComparison.OrdinalIgnoreCase) ||
               (c.ComponentType ?? "").Contains("rail", StringComparison.OrdinalIgnoreCase) ||
               (c.ComponentType ?? "").Contains("hardware", StringComparison.OrdinalIgnoreCase) ||
               c.ComponentId.Contains("handle", StringComparison.OrdinalIgnoreCase) ||
               c.ComponentId.Contains("hinge", StringComparison.OrdinalIgnoreCase) ||
               c.ComponentId.Contains("hw_", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsGlassComponent(ComponentValidationResult c)
    {
        return (c.ComponentType ?? "").Contains("glass", StringComparison.OrdinalIgnoreCase) ||
               c.ComponentId.Contains("glass", StringComparison.OrdinalIgnoreCase);
    }
}
