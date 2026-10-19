using OpenIMG.Application.Validation;
using OpenIMG.Application.Configuration;
using OpenIMG.Core.Enums;
using OpenIMG.Core.Models;

namespace OpenIMG.Tests;

[Trait("Category", TestCategories.Unit)]
public sealed class ValidationDecisionSafetyTests
{
    [Fact]
    public void GeminiPassCannotOverrideCriticalMaterialIdentityViolation()
    {
        var engine = new ValidationDecisionEngine(new OpenImgOptions());
        var report = new LocalValidationReport
        {
            RegistrationModel = RegistrationModel.Similarity,
            RegistrationConfidence = 0.99,
            FurnitureSsim = 0.95,
            FurnitureEdgeSimilarity = 0.95,
            FurnitureEdgeRecall = 0.99,
            SilhouetteSimilarity = 0.98,
            HardwarePreserved = true,
            PhotographicEdgeEnhancementDetected = true,
            Evidences =
            [
                new ValidationEvidence
                {
                    Category = ValidationEvidenceCategory.Color,
                    Type = "MaterialColorIdentityViolation",
                    Severity = ValidationSeverity.Critical,
                    Passed = false,
                    Message = "Primary furniture material changed from the observed family."
                }
            ]
        };

        var local = engine.EvaluateLocalReport(report, out var evidences);
        var final = engine.CombineDecisions(
            local,
            new GeminiValidationReport { Decision = "PASS", FurnitureDecision = "PASS" },
            out _,
            evidences,
            allowSemanticResolution: true);

        Assert.Equal(ValidationDecision.Rejected, local);
        Assert.Equal(ValidationDecision.Rejected, final);
    }

    [Fact]
    public void RegistrationFailureCannotHidePersistedCriticalPhysicalEvidence()
    {
        var engine = new ValidationDecisionEngine(new OpenImgOptions());
        var report = new LocalValidationReport
        {
            RegistrationModel = RegistrationModel.None,
            RegistrationConfidence = 0,
            Evidences =
            [
                new ValidationEvidence
                {
                    Category = ValidationEvidenceCategory.Color,
                    Type = "MaterialColorIdentityViolation",
                    Severity = ValidationSeverity.Critical,
                    Passed = false,
                    Message = "Critical material identity change measured before registration failed."
                }
            ]
        };

        var local = engine.EvaluateLocalReport(report, out var evidences);
        var final = engine.CombineDecisions(
            local,
            new GeminiValidationReport { Decision = "PASS", FurnitureDecision = "PASS" },
            out _,
            evidences,
            allowSemanticResolution: true);

        Assert.Contains(evidences, e => e.Type == "MaterialColorIdentityViolation" && !e.Passed);
        Assert.Equal(ValidationDecision.NeedsReview, local);
        Assert.Equal(ValidationDecision.Rejected, final);
    }
}
