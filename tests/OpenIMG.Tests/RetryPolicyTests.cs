using OpenIMG.Application.Validation;
using OpenIMG.Core.Enums;
using OpenIMG.Core.Models;
using Xunit;

namespace OpenIMG.Tests;

[Trait("Category", TestCategories.Unit)]
public class RetryPolicyTests
{
    private readonly RetryPolicy _policy = new(maxAutomaticAttempts: 2);

    [Fact]
    public void EvaluateRetry_Accepted_ReturnsShouldRetryFalse()
    {
        var result = _policy.EvaluateRetry(
            currentAttemptNumber: 1,
            decision: ValidationDecision.Accepted,
            localReport: new LocalValidationReport(),
            remoteReport: null,
            evidences: new List<ValidationEvidence>());

        Assert.False(result.ShouldRetry);
    }

    [Fact]
    public void EvaluateRetry_MaxAttemptsReached_ReturnsShouldRetryFalse()
    {
        var result = _policy.EvaluateRetry(
            currentAttemptNumber: 2,
            decision: ValidationDecision.Rejected,
            localReport: new LocalValidationReport(),
            remoteReport: null,
            evidences: new List<ValidationEvidence>());

        Assert.False(result.ShouldRetry);
    }

    [Fact]
    public void EvaluateRetry_Homography_ReturnsRegistrationUncertain_NoRetry()
    {
        var report = new LocalValidationReport
        {
            RegistrationModel = RegistrationModel.Homography,
            RegistrationConfidence = 0.85
        };

        var result = _policy.EvaluateRetry(
            currentAttemptNumber: 1,
            decision: ValidationDecision.NeedsReview,
            localReport: report,
            remoteReport: null,
            evidences: new List<ValidationEvidence>());

        Assert.False(result.ShouldRetry);
        Assert.Equal(RestorationRetryReason.RegistrationUncertain, result.Reason);
    }

    [Fact]
    public void EvaluateRetry_RemoteUncertain_ReturnsRemoteUncertain_NoRetry()
    {
        var remote = new GeminiValidationReport
        {
            Decision = "UNCERTAIN",
            Notes = "Lighting changed too much to verify."
        };

        var result = _policy.EvaluateRetry(
            currentAttemptNumber: 1,
            decision: ValidationDecision.NeedsReview,
            localReport: new LocalValidationReport { RegistrationModel = RegistrationModel.Similarity, RegistrationConfidence = 0.90 },
            remoteReport: remote,
            evidences: new List<ValidationEvidence>());

        Assert.False(result.ShouldRetry);
        Assert.Equal(RestorationRetryReason.RemoteUncertain, result.Reason);
    }

    [Fact]
    public void EvaluateRetry_RemoteFailWithHardwareNotes_ReturnsConfirmedDecorativeModification_RetryTrueWithConstraint()
    {
        var remote = new GeminiValidationReport
        {
            Decision = "FAIL",
            Notes = "Added new chrome handles and extra hinge details."
        };

        var result = _policy.EvaluateRetry(
            currentAttemptNumber: 1,
            decision: ValidationDecision.Rejected,
            localReport: new LocalValidationReport { RegistrationModel = RegistrationModel.Similarity, RegistrationConfidence = 0.90 },
            remoteReport: remote,
            evidences: new List<ValidationEvidence>());

        Assert.True(result.ShouldRetry);
        Assert.Equal(RestorationRetryReason.ConfirmedDecorativeModification, result.Reason);
        Assert.NotNull(result.CorrectiveConstraint);
        Assert.Contains("hardware", result.CorrectiveConstraint);
    }

    [Fact]
    public void EvaluateRetry_ProtectedComponentRecolored_ReturnsConfirmedComponentModification_RetryTrue()
    {
        var evidences = new List<ValidationEvidence>
        {
            new()
            {
                Category = ValidationEvidenceCategory.ProtectedRegion,
                Type = "ProtectedAreaChangedPercentage",
                Severity = ValidationSeverity.Critical,
                Passed = false,
                Region = "left_upper_door",
                Message = "Component color shifted from white to blue."
            }
        };

        var result = _policy.EvaluateRetry(
            currentAttemptNumber: 1,
            decision: ValidationDecision.Rejected,
            localReport: new LocalValidationReport { RegistrationModel = RegistrationModel.Similarity, RegistrationConfidence = 0.88 },
            remoteReport: null,
            evidences: evidences);

        Assert.True(result.ShouldRetry);
        Assert.Equal(RestorationRetryReason.ConfirmedComponentModification, result.Reason);
        Assert.Contains("left_upper_door", result.CorrectiveConstraint);
    }

    [Fact]
    public void EvaluateRetry_MetricConflict_HighSsimLowChangedArea_ReturnsMetricConflict_NoRetry()
    {
        var report = new LocalValidationReport
        {
            RegistrationModel = RegistrationModel.Similarity,
            RegistrationConfidence = 0.90,
            StructuralSimilarity = 0.96,
            ChangedAreaPercentage = 0.02
        };

        var evidences = new List<ValidationEvidence>
        {
            new()
            {
                Category = ValidationEvidenceCategory.Structural,
                Type = "EdgeSimilarity",
                Severity = ValidationSeverity.Critical,
                Passed = false,
                Message = "Edge metric fell below threshold."
            }
        };

        var result = _policy.EvaluateRetry(
            currentAttemptNumber: 1,
            decision: ValidationDecision.Rejected,
            localReport: report,
            remoteReport: null,
            evidences: evidences);

        Assert.False(result.ShouldRetry);
        Assert.Equal(RestorationRetryReason.MetricConflict, result.Reason);
    }
}
