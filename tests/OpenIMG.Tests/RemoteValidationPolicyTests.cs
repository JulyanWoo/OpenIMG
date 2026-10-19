using OpenIMG.Application.Configuration;
using OpenIMG.Application.Validation;
using OpenIMG.Core.Enums;
using OpenIMG.Core.Models;
using Xunit;

namespace OpenIMG.Tests;

[Trait("Category", TestCategories.Unit)]
public class RemoteValidationPolicyTests
{
    private readonly OpenImgOptions _options = new()
    {
        MinStructuralSimilarity = 0.70,
        MinEdgeSimilarity = 0.65,
        MinSilhouetteSimilarity = 0.75,
        MinImageRegistrationConfidence = 0.50,
        RequireRemoteValidationForHighRiskAccepted = true
    };

    private static LocalValidationReport CreateLowRiskAcceptedReport()
    {
        return new LocalValidationReport
        {
            Passed = true,
            RegistrationModel = RegistrationModel.Similarity,
            RegistrationConfidence = 0.90,
            StructuralSimilarity = 0.92,
            EdgeSimilarity = 0.85,
            FurnitureEdgePrecision = 0.88,
            FurnitureEdgeRecall = 0.95,
            SilhouetteSimilarity = 0.94,
            NormalizedColorDistributionDifference = 0.08,
            ChangedAreaPercentage = 0.03,
            LargestChangedRegionPercentage = 0.01,
            ProtectedAreaChangedPercentage = 0.0,
            UnexpectedEdgeRatio = 0.10,
            MissingEdgeRatio = 0.02,
            MaterialSensitiveColorDifference = 0.02,
            PermanentSceneColorDeltaE = 2.0,
            PermanentSceneChromaRatio = 1.0,
            PermanentSceneColorIdentityPreserved = true,
            HardwareAbsencePreserved = true
        };
    }

    [Fact]
    public void Evaluate_LocalRejected_ReturnsBlockedByLocalRejection()
    {
        var policy = new RemoteValidationPolicy(_options);
        var report = CreateLowRiskAcceptedReport();

        var result = policy.Evaluate(report, ValidationDecision.Rejected);

        Assert.Equal(RemoteValidationRequirement.BlockedByLocalRejection, result.Requirement);
        Assert.Contains(RemoteValidationReason.LocalRejected, result.Reasons);
        Assert.False(result.WouldCallGemini);
    }

    [Fact]
    public void Evaluate_LocalNeedsReview_ReturnsRequiredWithBarrier3NeedsReview()
    {
        var policy = new RemoteValidationPolicy(_options);
        var report = CreateLowRiskAcceptedReport();

        var result = policy.Evaluate(report, ValidationDecision.NeedsReview);

        Assert.Equal(RemoteValidationRequirement.Required, result.Requirement);
        Assert.Contains(RemoteValidationReason.Barrier3NeedsReview, result.Reasons);
        Assert.True(result.WouldCallGemini);
    }

    [Fact]
    public void Evaluate_LocalNeedsReview_WithHardwareAmbiguity_IncludesHardwareReason()
    {
        var policy = new RemoteValidationPolicy(_options);
        var report = CreateLowRiskAcceptedReport();
        var contract = new LocalFurnitureContract
        {
            HardwareAbsenceConstraints = new List<HardwareAbsenceConstraint>
            {
                new() { ConstraintId = "absence_left", Side = "Left", Strict = true }
            }
        };

        var result = policy.Evaluate(report, ValidationDecision.NeedsReview, contract);

        Assert.Equal(RemoteValidationRequirement.Required, result.Requirement);
        Assert.Contains(RemoteValidationReason.Barrier3NeedsReview, result.Reasons);
        Assert.Contains(RemoteValidationReason.HardwareCardinalityAmbiguous, result.Reasons);
        Assert.True(result.WouldCallGemini);
    }

    [Fact]
    public void Evaluate_LocalAccepted_LowRisk_ReturnsNotRequiredAndLowRiskAccepted()
    {
        var policy = new RemoteValidationPolicy(_options);
        var report = CreateLowRiskAcceptedReport();

        var result = policy.Evaluate(report, ValidationDecision.Accepted);

        Assert.Equal(RemoteValidationRequirement.NotRequired, result.Requirement);
        Assert.Contains(RemoteValidationReason.LowRiskAccepted, result.Reasons);
        Assert.False(result.WouldCallGemini);
    }

    [Fact]
    public void Evaluate_LocalAccepted_HardwareAmbiguity_ReturnsRequired()
    {
        var policy = new RemoteValidationPolicy(_options);
        var report = CreateLowRiskAcceptedReport();
        var contract = new LocalFurnitureContract
        {
            HardwareCandidates = new List<HardwareCandidate>
            {
                new() { Id = "hw_1", Confidence = 0.65 } // Low confidence (< 0.80) -> Ambiguous
            }
        };

        var result = policy.Evaluate(report, ValidationDecision.Accepted, contract);

        Assert.Equal(RemoteValidationRequirement.Required, result.Requirement);
        Assert.Contains(RemoteValidationReason.HighRiskAccepted, result.Reasons);
        Assert.Contains(RemoteValidationReason.HardwareAmbiguous, result.Reasons);
        Assert.True(result.WouldCallGemini);
    }

    [Fact]
    public void Evaluate_LocalAccepted_AffineRegistration_ReturnsRequired()
    {
        var policy = new RemoteValidationPolicy(_options);
        var report = CreateLowRiskAcceptedReport() with
        {
            RegistrationModel = RegistrationModel.Affine
        };

        var result = policy.Evaluate(report, ValidationDecision.Accepted);

        Assert.Equal(RemoteValidationRequirement.Required, result.Requirement);
        Assert.Contains(RemoteValidationReason.AffineRegistrationUsed, result.Reasons);
        Assert.True(result.WouldCallGemini);
    }

    [Fact]
    public void Evaluate_LocalAccepted_RegistrationNearBoundary_ReturnsRequired()
    {
        var policy = new RemoteValidationPolicy(_options);
        var report = CreateLowRiskAcceptedReport() with
        {
            RegistrationConfidence = 0.65 // < 0.70 threshold for low risk
        };

        var result = policy.Evaluate(report, ValidationDecision.Accepted);

        Assert.Equal(RemoteValidationRequirement.Required, result.Requirement);
        Assert.Contains(RemoteValidationReason.RegistrationNearUncertaintyBoundary, result.Reasons);
        Assert.True(result.WouldCallGemini);
    }

    [Fact]
    public void Evaluate_LocalAccepted_ComplexGlass_ReturnsRequired()
    {
        var policy = new RemoteValidationPolicy(_options);
        var report = CreateLowRiskAcceptedReport();
        report.ComponentResults.Add(new ComponentValidationResult
        {
            ComponentId = "comp_glass_1",
            ComponentType = "glass_door"
        });

        var result = policy.Evaluate(report, ValidationDecision.Accepted);

        Assert.Equal(RemoteValidationRequirement.Required, result.Requirement);
        Assert.Contains(RemoteValidationReason.GlassOrReflectionComplexity, result.Reasons);
        Assert.True(result.WouldCallGemini);
    }

    [Fact]
    public void Evaluate_LocalAccepted_MaterialSensitiveSurfaceHigh_ReturnsRequired()
    {
        var policy = new RemoteValidationPolicy(_options);
        var report = CreateLowRiskAcceptedReport() with
        {
            MaterialSensitiveColorDifference = 0.15 // > 0.10
        };

        var result = policy.Evaluate(report, ValidationDecision.Accepted);

        Assert.Equal(RemoteValidationRequirement.Required, result.Requirement);
        Assert.Contains(RemoteValidationReason.MaterialSensitiveSurfaceHigh, result.Reasons);
        Assert.True(result.WouldCallGemini);
    }

    [Fact]
    public void Evaluate_LocalAccepted_ColorIdentityNearBoundary_ReturnsRequired()
    {
        var policy = new RemoteValidationPolicy(_options);
        var report = CreateLowRiskAcceptedReport() with
        {
            PermanentSceneColorDeltaE = 8.5 // > 7.0
        };

        var result = policy.Evaluate(report, ValidationDecision.Accepted);

        Assert.Equal(RemoteValidationRequirement.Required, result.Requirement);
        Assert.Contains(RemoteValidationReason.ColorIdentityNearBoundary, result.Reasons);
        Assert.True(result.WouldCallGemini);
    }

    [Fact]
    public void Evaluate_LocalAccepted_ProtectedAbsenceUncertain_ReturnsRequired()
    {
        var policy = new RemoteValidationPolicy(_options);
        var report = CreateLowRiskAcceptedReport();
        var contract = new LocalFurnitureContract
        {
            HardwareAbsenceConstraints = new List<HardwareAbsenceConstraint>
            {
                new() { ConstraintId = "absence_left", Side = "Left", Strict = true }
            }
        };

        var result = policy.Evaluate(report, ValidationDecision.Accepted, contract);

        Assert.Equal(RemoteValidationRequirement.Required, result.Requirement);
        Assert.Contains(RemoteValidationReason.ProtectedAbsenceNearBoundary, result.Reasons);
        Assert.True(result.WouldCallGemini);
    }

    [Fact]
    public void Evaluate_LocalAccepted_GenericClutterOnly_DoesNotRequireGemini()
    {
        var policy = new RemoteValidationPolicy(_options);
        var report = CreateLowRiskAcceptedReport();

        // Furniture in center
        var furnitureBounds = new NormalizedRectangle(0.2, 0.2, 0.6, 0.6);

        // Generic clutter objects in background/sides that do NOT overlap furniture
        var contract = new LocalFurnitureContract
        {
            FurnitureBounds = furnitureBounds,
            SceneComplexity = 0.40, // Low scene complexity
            LocalConfidence = 0.85,
            ConfidenceBreakdown = new LocalConfidenceBreakdown
            {
                StructuralCoverageConfidence = 0.85
            },
            StructuralRegions = new List<LocalStructuralRegion>
            {
                new() { Id = "shelf_1", Region = new NormalizedRectangle(0.2, 0.3, 0.6, 0.05) },
                new() { Id = "shelf_2", Region = new NormalizedRectangle(0.2, 0.5, 0.6, 0.05) }
            },
            SceneObjects = new List<LocalDetectedObject>
            {
                new() { Id = "obj_person", ClassName = "person", Region = new NormalizedRectangle(0.01, 0.01, 0.10, 0.15) },
                new() { Id = "obj_bottle", ClassName = "bottle", Region = new NormalizedRectangle(0.85, 0.85, 0.05, 0.10) },
                new() { Id = "obj_sink", ClassName = "sink", Region = new NormalizedRectangle(0.85, 0.01, 0.10, 0.10) },
                new() { Id = "obj_tv", ClassName = "tv", Region = new NormalizedRectangle(0.01, 0.80, 0.15, 0.15) }
            }
        };

        var result = policy.Evaluate(report, ValidationDecision.Accepted, contract);

        Assert.Equal(RemoteValidationRequirement.NotRequired, result.Requirement);
        Assert.Contains(RemoteValidationReason.LowRiskAccepted, result.Reasons);
        Assert.False(result.WouldCallGemini);
    }

    [Fact]
    public void CombineDecisions_LocalCriticalEvidence_GeminiPassCannotOverride()
    {
        var engine = new ValidationDecisionEngine(_options);
        var localDecision = ValidationDecision.Rejected;
        var localEvidences = new List<ValidationEvidence>
        {
            new()
            {
                Category = ValidationEvidenceCategory.Component,
                Type = "HardwareAbsenceViolated",
                Severity = ValidationSeverity.Critical,
                Passed = false,
                Message = "Unexpected hardware added to left flank."
            }
        };

        var remoteReport = new GeminiValidationReport
        {
            Decision = "PASS",
            FurnitureDecision = "PASS",
            Notes = "Looks good according to Gemini."
        };

        var finalDecision = engine.CombineDecisions(
            localDecision,
            remoteReport,
            out var finalEvidences,
            localEvidences,
            allowSemanticResolution: true);

        Assert.Equal(ValidationDecision.Rejected, finalDecision);
    }

    [Fact]
    public void CombineDecisions_RemotePass_NeedsReview_ResolvesToAcceptedWhenAllowed()
    {
        var engine = new ValidationDecisionEngine(_options);
        var localDecision = ValidationDecision.NeedsReview;
        var localEvidences = new List<ValidationEvidence>
        {
            new()
            {
                Category = ValidationEvidenceCategory.Structural,
                Type = "UnexpectedEdgeRatio",
                Severity = ValidationSeverity.Warning,
                Passed = false,
                Message = "De-blurred wood grain edges exceeded ratio."
            }
        };

        var remoteReport = new GeminiValidationReport
        {
            Decision = "PASS",
            FurnitureDecision = "PASS",
            Notes = "High fidelity restoration confirmed."
        };

        var finalDecision = engine.CombineDecisions(
            localDecision,
            remoteReport,
            out var finalEvidences,
            localEvidences,
            allowSemanticResolution: true);

        Assert.Equal(ValidationDecision.Accepted, finalDecision);
        Assert.Contains(finalEvidences, e => e.Type == "GeminiValidation" && e.Passed);
    }

    [Fact]
    public void CombineDecisions_RemotePass_NeedsReview_RemainsNeedsReviewWhenNotAllowed()
    {
        var engine = new ValidationDecisionEngine(_options);
        var localDecision = ValidationDecision.NeedsReview;
        var localEvidences = new List<ValidationEvidence>
        {
            new()
            {
                Category = ValidationEvidenceCategory.Structural,
                Type = "UnexpectedEdgeRatio",
                Severity = ValidationSeverity.Warning,
                Passed = false,
                Message = "De-blurred wood grain edges exceeded ratio."
            }
        };

        var remoteReport = new GeminiValidationReport
        {
            Decision = "PASS",
            FurnitureDecision = "PASS",
            Notes = "High fidelity restoration confirmed."
        };

        var finalDecision = engine.CombineDecisions(
            localDecision,
            remoteReport,
            out var finalEvidences,
            localEvidences,
            allowSemanticResolution: false); // Not allowed (legacy/shadow mode)

        Assert.Equal(ValidationDecision.NeedsReview, finalDecision);
    }

    [Fact]
    public void CombineDecisions_RemoteFail_NeedsReview_ForcesRejected()
    {
        var engine = new ValidationDecisionEngine(_options);
        var localDecision = ValidationDecision.NeedsReview;
        var localEvidences = new List<ValidationEvidence>
        {
            new()
            {
                Category = ValidationEvidenceCategory.Structural,
                Type = "UnexpectedEdgeRatio",
                Severity = ValidationSeverity.Warning,
                Passed = false
            }
        };

        var remoteReport = new GeminiValidationReport
        {
            Decision = "FAIL",
            FurnitureDecision = "FAIL",
            Notes = "Altered cabinet layout."
        };

        var finalDecision = engine.CombineDecisions(
            localDecision,
            remoteReport,
            out var finalEvidences,
            localEvidences,
            allowSemanticResolution: true);

        Assert.Equal(ValidationDecision.Rejected, finalDecision);
        Assert.Contains(finalEvidences, e => e.Type == "GeminiValidation" && e.Severity == ValidationSeverity.Critical && !e.Passed);
    }

    [Fact]
    public void CombineDecisions_RemoteUncertain_NeedsReview_RemainsNeedsReview()
    {
        var engine = new ValidationDecisionEngine(_options);
        var localDecision = ValidationDecision.NeedsReview;
        var remoteReport = new GeminiValidationReport
        {
            Decision = "UNCERTAIN",
            FurnitureDecision = "UNCERTAIN",
            Notes = "Uncertain about wood grain."
        };

        var finalDecision = engine.CombineDecisions(
            localDecision,
            remoteReport,
            out var finalEvidences,
            new List<ValidationEvidence>(),
            allowSemanticResolution: true);

        Assert.Equal(ValidationDecision.NeedsReview, finalDecision);
    }

    [Fact]
    public void CombineDecisions_RemoteFail_AcceptedHighRisk_ForcesRejected()
    {
        var engine = new ValidationDecisionEngine(_options);
        var localDecision = ValidationDecision.Accepted;
        var remoteReport = new GeminiValidationReport
        {
            Decision = "FAIL",
            FurnitureDecision = "FAIL",
            Notes = "Hallucinated handle."
        };

        var finalDecision = engine.CombineDecisions(
            localDecision,
            remoteReport,
            out var finalEvidences,
            new List<ValidationEvidence>(),
            allowSemanticResolution: true);

        Assert.Equal(ValidationDecision.Rejected, finalDecision);
        Assert.Contains(finalEvidences, e => e.Type == "GeminiValidation" && e.Severity == ValidationSeverity.Critical && !e.Passed);
    }

    [Fact]
    public void CombineDecisions_RemoteUncertain_AcceptedHighRisk_ForcesNeedsReview()
    {
        var engine = new ValidationDecisionEngine(_options);
        var localDecision = ValidationDecision.Accepted;
        var remoteReport = new GeminiValidationReport
        {
            Decision = "UNCERTAIN",
            FurnitureDecision = "UNCERTAIN",
            Notes = "Could not confirm details."
        };

        var finalDecision = engine.CombineDecisions(
            localDecision,
            remoteReport,
            out var finalEvidences,
            new List<ValidationEvidence>(),
            allowSemanticResolution: true);

        Assert.Equal(ValidationDecision.NeedsReview, finalDecision);
    }

    [Fact]
    public void CombineDecisions_RemotePass_AcceptedHighRisk_ReturnsAccepted()
    {
        var engine = new ValidationDecisionEngine(_options);
        var localDecision = ValidationDecision.Accepted;
        var remoteReport = new GeminiValidationReport
        {
            Decision = "PASS",
            FurnitureDecision = "PASS",
            Notes = "Confirmed physical fidelity."
        };

        var finalDecision = engine.CombineDecisions(
            localDecision,
            remoteReport,
            out var finalEvidences,
            new List<ValidationEvidence>(),
            allowSemanticResolution: true);

        Assert.Equal(ValidationDecision.Accepted, finalDecision);
    }
}
