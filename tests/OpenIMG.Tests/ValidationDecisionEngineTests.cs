using OpenIMG.Application.Configuration;
using OpenIMG.Application.Validation;
using OpenIMG.Core.Enums;
using OpenIMG.Core.Models;
using Xunit;

namespace OpenIMG.Tests;

[Trait("Category", TestCategories.Unit)]
public class ValidationDecisionEngineTests
{
    private readonly OpenImgOptions _options = new()
    {
        MinStructuralSimilarity = 0.70,
        MinEdgeSimilarity = 0.65,
        MinSilhouetteSimilarity = 0.75,
        MaxAllowedGlobalColorDifference = 0.35,
        MaxAllowedNormalizedColorDistributionDifference = 0.30,
        MaxAllowedChangedAreaPercentage = 0.20,
        MaxAllowedLargestChangedRegionPercentage = 0.08,
        MaxAllowedProtectedAreaChangedPercentage = 0.05,
        MinImageRegistrationConfidence = 0.50,
        MaxAspectRatioDifference = 0.05
    };

    [Fact]
    public void EvaluateLocalReport_CleanMetrics_ReturnsAccepted()
    {
        var engine = new ValidationDecisionEngine(_options);
        var report = new LocalValidationReport
        {
            RegistrationModel = RegistrationModel.Similarity,
            RegistrationConfidence = 0.85,
            StructuralSimilarity = 0.88,
            EdgeSimilarity = 0.82,
            SilhouetteSimilarity = 0.91,
            NormalizedColorDistributionDifference = 0.12,
            ChangedAreaPercentage = 0.09,
            LargestChangedRegionPercentage = 0.03,
            ProtectedAreaChangedPercentage = 0.02,
            AspectRatioDifference = 0.01
        };

        var decision = engine.EvaluateLocalReport(report, out var evidences);

        Assert.Equal(ValidationDecision.Accepted, decision);
        Assert.All(evidences, e => Assert.True(e.Passed));
    }

    [Fact]
    public void EvaluateLocalReport_HomographyRegistration_ForcesNeedsReview()
    {
        var engine = new ValidationDecisionEngine(_options);
        var report = new LocalValidationReport
        {
            RegistrationModel = RegistrationModel.Homography, // Aggressive fallback!
            RegistrationConfidence = 0.80,
            StructuralSimilarity = 0.90,
            EdgeSimilarity = 0.85,
            SilhouetteSimilarity = 0.92,
            NormalizedColorDistributionDifference = 0.10,
            ChangedAreaPercentage = 0.05,
            LargestChangedRegionPercentage = 0.02,
            ProtectedAreaChangedPercentage = 0.01,
            AspectRatioDifference = 0.01
        };

        var decision = engine.EvaluateLocalReport(report, out var evidences);

        Assert.Equal(ValidationDecision.NeedsReview, decision);
        Assert.Contains(evidences, e => e.Type == "RegistrationModel" && !e.Passed);
    }

    [Fact]
    public void EvaluateLocalReport_ProtectedAreaRecolored_ReturnsRejected()
    {
        var engine = new ValidationDecisionEngine(_options);
        var report = new LocalValidationReport
        {
            RegistrationModel = RegistrationModel.Similarity,
            RegistrationConfidence = 0.85,
            StructuralSimilarity = 0.88,
            EdgeSimilarity = 0.82,
            SilhouetteSimilarity = 0.91,
            NormalizedColorDistributionDifference = 0.12,
            ChangedAreaPercentage = 0.09,
            LargestChangedRegionPercentage = 0.03,
            ProtectedAreaChangedPercentage = 0.18, // Above 0.05 threshold -> Critical!
            AspectRatioDifference = 0.01
        };

        var decision = engine.EvaluateLocalReport(report, out var evidences);

        Assert.Equal(ValidationDecision.Rejected, decision);
        Assert.Contains(evidences, e => e.Type == "ProtectedAreaChangedPercentage" && e.Severity == ValidationSeverity.Critical && !e.Passed);
    }

    [Fact]
    public void CombineDecisions_LocalRejected_RemainsRejectedRegardlessOfRemote()
    {
        var engine = new ValidationDecisionEngine(_options);
        var localDecision = ValidationDecision.Rejected;
        var remoteReport = new GeminiValidationReport
        {
            Decision = "PASS", // Even if Gemini says PASS, local rejection (Barrier 3) must never be overridden!
            Notes = "Looks good to Gemini."
        };

        var finalDecision = engine.CombineDecisions(localDecision, remoteReport, out var finalEvidences, new List<ValidationEvidence>());

        Assert.Equal(ValidationDecision.Rejected, finalDecision);
    }

    [Fact]
    public void CombineDecisions_LocalNeedsReview_RemainsNeedsReviewRegardlessOfRemote()
    {
        var engine = new ValidationDecisionEngine(_options);
        var localDecision = ValidationDecision.NeedsReview;
        var remoteReport = new GeminiValidationReport
        {
            Decision = "PASS",
            Notes = "Looks good to Gemini."
        };

        var finalDecision = engine.CombineDecisions(localDecision, remoteReport, out var finalEvidences, new List<ValidationEvidence>());

        Assert.Equal(ValidationDecision.NeedsReview, finalDecision);
    }

    [Fact]
    public void CombineDecisions_RemoteFail_ForcesRejected()
    {
        var engine = new ValidationDecisionEngine(_options);
        var localDecision = ValidationDecision.Accepted;
        var remoteReport = new GeminiValidationReport
        {
            Decision = "FAIL",
            Notes = "Drawer handle was removed."
        };

        var finalDecision = engine.CombineDecisions(localDecision, remoteReport, out var finalEvidences, new List<ValidationEvidence>());

        Assert.Equal(ValidationDecision.Rejected, finalDecision);
        Assert.Contains(finalEvidences, e => e.Type == "GeminiValidation" && e.Severity == ValidationSeverity.Critical && !e.Passed);
    }

    [Fact]
    public void CombineDecisions_RemoteUncertain_ForcesNeedsReview()
    {
        var engine = new ValidationDecisionEngine(_options);
        var localDecision = ValidationDecision.Accepted;
        var remoteReport = new GeminiValidationReport
        {
            Decision = "UNCERTAIN",
            Notes = "Could not confirm if cabinet grain is preserved."
        };

        var finalDecision = engine.CombineDecisions(localDecision, remoteReport, out var finalEvidences, new List<ValidationEvidence>());

        Assert.Equal(ValidationDecision.NeedsReview, finalDecision);
        Assert.Contains(finalEvidences, e => e.Type == "GeminiValidation" && !e.Passed);
    }
}
