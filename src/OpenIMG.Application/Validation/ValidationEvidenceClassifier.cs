using OpenIMG.Application.Configuration;
using OpenIMG.Core.Enums;
using OpenIMG.Core.Models;

namespace OpenIMG.Application.Validation;

/// <summary>
/// Classifies already-measured validation evidence into the policy tiers used
/// by Barrier 3. It does not calculate metrics and it does not choose the
/// final decision.
/// </summary>
public sealed class ValidationEvidenceClassifier
{
    private readonly OpenImgOptions _options;

    public ValidationEvidenceClassifier(OpenImgOptions options)
    {
        _options = options;
    }

    public ValidationEvidenceClassification Classify(
        LocalValidationReport report,
        IEnumerable<ValidationEvidence> evidences)
    {
        var tierA = new List<ValidationEvidence>();
        var tierB = new List<ValidationEvidence>();
        var tierC = new List<ValidationEvidence>();

        foreach (var evidence in evidences.Where(e => !e.Passed))
        {
            if (IsTierAEvidence(evidence))
            {
                tierA.Add(evidence);
            }
            else if (IsTierBEvidence(evidence, report))
            {
                tierB.Add(evidence);
            }
            else
            {
                tierC.Add(evidence);
            }
        }

        return new ValidationEvidenceClassification(tierA, tierB, tierC);
    }

    public bool IsNonBlockingPhotographicTierC(ValidationEvidence evidence, LocalValidationReport report)
    {
        var isPhotoGain = report.PhotographicEdgeEnhancementDetected ||
                          report.PhotographicImprovement != PhotographicImprovementAssessment.Insufficient ||
                          report.SharpnessGain >= 0.50 ||
                          report.HazeReduction >= 10.0;

        var recallPreserved = (report.FurnitureEdgeRecall > 0 ? report.FurnitureEdgeRecall : report.EdgeRecall) >= 0.95;
        var silhouettePreserved = report.SilhouetteSimilarity >= _options.MinSilhouetteSimilarity;
        var hardwarePreserved = report.HardwarePreserved;
        var colorPreserved = report.FurnitureStructuralRegionColorDifference <= _options.MaxAllowedFurnitureStructuralColorDifference;

        if (!isPhotoGain || !recallPreserved || !silhouettePreserved || !hardwarePreserved || !colorPreserved)
            return false;

        return evidence.Type switch
        {
            "EdgeSimilarity" => true,
            "EdgePrecision" => true,
            "EdgeRecall" when recallPreserved => true,
            "UnexpectedEdgeRatio" => true,
            "UnexpectedEdgeCluster" => true,
            "MinorUnsupportedSceneDetail" => true,
            "ComponentStructureAdded" => true,
            "LargestChangedRegion" => true,
            "ProtectedAreaChangedPercentage" => true,
            "ChangedAreaPercentage" => true,
            "PhotometricLuminanceChange" => true,
            "NormalizedColorDistribution" when colorPreserved => true,
            _ => false
        };
    }

    public string GetTierBConcept(ValidationEvidence evidence)
    {
        return evidence.Type switch
        {
            "SilhouetteSimilarity" => "Silhouette",
            "BoundaryContinuity" or "SceneCleanupBoundaryIntrusion" => "BoundaryDisplacement",
            "RegistrationModel" => "Registration",
            "SpeculativeReframing" or "ReframingPerspectiveDistortion" => "Reframing",
            "ComponentStructureAdded" or "ComponentStructureRemoved" => "ComponentStructure",
            "ComponentCardinalityMismatch" => "Cardinality",
            "CoarseEdgeStructureDisagreement" => "CoarseGeometry",
            "ProtectedNegativeSpaceViolation" => "NegativeSpace",
            "UnsupportedPhysicalAddition" => "PhysicalAddition",
            "PermanentScenePhysicalAddition" => "PhysicalAddition",
            _ => evidence.Type
        };
    }

    private static bool IsTierAEvidence(ValidationEvidence evidence)
    {
        return evidence.Type switch
        {
            "HardwareAbsenceViolated" => true,
            "UnexpectedStructureInProtectedAbsence" => true,
            "PersistentSceneDetailRemoved" => true,
            "SevereStructuralDeviation" => true,
            "CriticalHardwareRemoved" => true,
            "ComponentColorIdentityChanged" => true,
            "PermanentSceneColorIdentityChanged" => true,
            "MaterialSensitiveColor" => true,
            "MaterialColorIdentityViolation" => true,
            "AccentColorPreservation" => true,
            "ReframingAspectDistortion" => true,
            "GlassFrameAltered" => true,
            "ComponentCardinalityMismatch" when evidence.Severity == ValidationSeverity.Critical => true,
            "ComponentStructureAdded" when evidence.Severity == ValidationSeverity.Critical => true,
            "ComponentStructureRemoved" when evidence.Severity == ValidationSeverity.Critical => true,
            "ProtectedNegativeSpaceViolation" when evidence.Severity == ValidationSeverity.Critical => true,
            "UnsupportedPhysicalAddition" when evidence.Severity == ValidationSeverity.Critical => true,
            "PermanentScenePhysicalAddition" when evidence.Severity == ValidationSeverity.Critical => true,
            _ => evidence.Severity == ValidationSeverity.Critical &&
                 evidence.Category is ValidationEvidenceCategory.ProtectedRegion or ValidationEvidenceCategory.Structural or ValidationEvidenceCategory.Component
        };
    }

    private bool IsTierBEvidence(ValidationEvidence evidence, LocalValidationReport report)
    {
        var isPhotoGain = report.PhotographicEdgeEnhancementDetected ||
                          report.PhotographicImprovement != PhotographicImprovementAssessment.Insufficient ||
                          report.SharpnessGain >= 0.50 ||
                          report.HazeReduction >= 10.0;
        var recallPreserved = (report.FurnitureEdgeRecall > 0 ? report.FurnitureEdgeRecall : report.EdgeRecall) >= 0.95;
        var furniturePreserved = report.FurnitureSsim >= 0.70 || report.SilhouetteSimilarity >= _options.MinSilhouetteSimilarity;

        if (isPhotoGain && recallPreserved && furniturePreserved &&
            evidence.Type is "SilhouetteSimilarity" or "BoundaryContinuity" or "SceneCleanupBoundaryIntrusion")
        {
            return false;
        }

        if (isPhotoGain && recallPreserved && evidence.Type is "ComponentStructureAdded" && evidence.Severity == ValidationSeverity.Warning)
        {
            return false;
        }

        return evidence.Type switch
        {
            "SilhouetteSimilarity" => true,
            "BoundaryContinuity" => true,
            "SceneCleanupBoundaryIntrusion" => true,
            "RegistrationModel" => true,
            "SpeculativeReframing" => true,
            "ReframingPerspectiveDistortion" => true,
            "ComponentStructureAdded" => true,
            "ComponentStructureRemoved" => true,
            "ComponentCardinalityMismatch" when !evidence.Message.Contains("Status: Ambiguous") => true,
            "CoarseEdgeStructureDisagreement" => true,
            "ProtectedNegativeSpaceViolation" when evidence.Severity == ValidationSeverity.Warning => true,
            "UnsupportedPhysicalAddition" when evidence.Severity == ValidationSeverity.Warning => true,
            "PermanentScenePhysicalAddition" when evidence.Severity == ValidationSeverity.Warning => true,
            _ => false
        };
    }
}

public sealed record ValidationEvidenceClassification(
    IReadOnlyList<ValidationEvidence> TierAFailures,
    IReadOnlyList<ValidationEvidence> TierBFailures,
    IReadOnlyList<ValidationEvidence> TierCFailures);
