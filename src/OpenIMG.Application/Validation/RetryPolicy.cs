using OpenIMG.Core.Enums;
using OpenIMG.Core.Models;

namespace OpenIMG.Application.Validation;

public record RetryEvaluationResult
{
    public bool ShouldRetry { get; init; }
    public RestorationRetryReason Reason { get; init; }
    public string? CorrectiveConstraint { get; init; }
    public string Explanation { get; init; } = string.Empty;
}

public class RetryPolicy
{
    private readonly int _maxAutomaticAttempts;

    public RetryPolicy(int maxAutomaticAttempts = 2)
    {
        _maxAutomaticAttempts = maxAutomaticAttempts;
    }

    public RetryEvaluationResult EvaluateRetry(
        int currentAttemptNumber,
        ValidationDecision decision,
        LocalValidationReport? localReport,
        GeminiValidationReport? remoteReport,
        IReadOnlyList<ValidationEvidence> evidences)
    {
        // 1. If accepted or already at/exceeded max attempts, no retry
        if (decision == ValidationDecision.Accepted)
        {
            return new RetryEvaluationResult
            {
                ShouldRetry = false,
                Reason = RestorationRetryReason.Unknown,
                Explanation = "Attempt was accepted."
            };
        }

        if (currentAttemptNumber >= _maxAutomaticAttempts)
        {
            return new RetryEvaluationResult
            {
                ShouldRetry = false,
                Reason = RestorationRetryReason.Unknown,
                Explanation = $"Max automatic attempts reached ({currentAttemptNumber}/{_maxAutomaticAttempts})."
            };
        }

        // 2. If decision is NeedsReview, automatic retry is forbidden!
        if (decision == ValidationDecision.NeedsReview)
        {
            // Determine why it was NeedsReview
            if (localReport?.RegistrationModel == RegistrationModel.Homography ||
                localReport?.RegistrationConfidence < 0.50 ||
                localReport?.RegistrationModel == RegistrationModel.None)
            {
                return new RetryEvaluationResult
                {
                    ShouldRetry = false,
                    Reason = RestorationRetryReason.RegistrationUncertain,
                    Explanation = "Registration required homography or had low confidence. Routing to NeedsReview without retry."
                };
            }

            if (remoteReport != null && string.Equals(remoteReport.Decision, "UNCERTAIN", StringComparison.OrdinalIgnoreCase))
            {
                return new RetryEvaluationResult
                {
                    ShouldRetry = false,
                    Reason = RestorationRetryReason.RemoteUncertain,
                    Explanation = "Gemini comparative validation was UNCERTAIN. Routing to NeedsReview without retry."
                };
            }

            return new RetryEvaluationResult
            {
                ShouldRetry = false,
                Reason = RestorationRetryReason.ValidatorUncertain,
                Explanation = "Local validation indicated uncertainty (warning severity). Routing to NeedsReview without retry."
            };
        }

        // 3. If decision is Rejected, examine evidence for concrete correctable modifications:
        if (localReport != null)
        {
            if (localReport.RegistrationModel == RegistrationModel.Homography ||
                localReport.RegistrationConfidence < 0.50 ||
                localReport.RegistrationModel == RegistrationModel.None)
            {
                return new RetryEvaluationResult
                {
                    ShouldRetry = false,
                    Reason = RestorationRetryReason.RegistrationUncertain,
                    Explanation = "Registration model was Homography or uncertain. No retry allowed."
                };
            }
        }

        // Check remote failure notes first if available
        if (remoteReport != null && string.Equals(remoteReport.Decision, "FAIL", StringComparison.OrdinalIgnoreCase))
        {
            var notes = remoteReport.Notes ?? string.Empty;
            if (notes.Contains("hardware", StringComparison.OrdinalIgnoreCase) ||
                notes.Contains("handle", StringComparison.OrdinalIgnoreCase) ||
                notes.Contains("hinge", StringComparison.OrdinalIgnoreCase) ||
                notes.Contains("knob", StringComparison.OrdinalIgnoreCase))
            {
                return new RetryEvaluationResult
                {
                    ShouldRetry = true,
                    Reason = RestorationRetryReason.ConfirmedDecorativeModification,
                    CorrectiveConstraint = $"The previous attempt hallucinated or altered hardware details: {notes.Trim()}. Do not alter, add, or hallucinate hinges, handles, or decorative hardware; keep the hardware configuration exactly as shown in the original photograph.",
                    Explanation = "Concrete hardware modification detected by remote comparative validator."
                };
            }

            if (notes.Contains("color", StringComparison.OrdinalIgnoreCase) ||
                notes.Contains("paint", StringComparison.OrdinalIgnoreCase) ||
                notes.Contains("tone", StringComparison.OrdinalIgnoreCase))
            {
                return new RetryEvaluationResult
                {
                    ShouldRetry = true,
                    Reason = RestorationRetryReason.ConfirmedColorModification,
                    CorrectiveConstraint = $"The previous attempt altered furniture coloration: {notes.Trim()}. You must retain the exact original paint tone and material finish.",
                    Explanation = "Concrete color modification detected by remote comparative validator."
                };
            }

            if (notes.Contains("door", StringComparison.OrdinalIgnoreCase) ||
                notes.Contains("panel", StringComparison.OrdinalIgnoreCase) ||
                notes.Contains("shelf", StringComparison.OrdinalIgnoreCase) ||
                notes.Contains("drawer", StringComparison.OrdinalIgnoreCase) ||
                notes.Contains("structure", StringComparison.OrdinalIgnoreCase) ||
                notes.Contains("dimension", StringComparison.OrdinalIgnoreCase))
            {
                return new RetryEvaluationResult
                {
                    ShouldRetry = true,
                    Reason = RestorationRetryReason.ConfirmedStructuralModification,
                    CorrectiveConstraint = $"The previous attempt altered furniture structure: {notes.Trim()}. Maintain the exact cabinet divisions, shelf positions, and door outlines as depicted in the original photograph.",
                    Explanation = "Concrete structural modification detected by remote comparative validator."
                };
            }

            if (!string.IsNullOrWhiteSpace(notes))
            {
                return new RetryEvaluationResult
                {
                    ShouldRetry = true,
                    Reason = RestorationRetryReason.ConfirmedUnexpectedObject,
                    CorrectiveConstraint = $"The previous attempt failed remote comparison: {notes.Trim()}. Restore the photograph without introducing any altered or synthetic objects.",
                    Explanation = "Concrete modification noted by comparative validator."
                };
            }
        }

        // Check local critical evidences
        var criticalEvidences = evidences.Where(e => e.Severity == ValidationSeverity.Critical && !e.Passed).ToList();

        // 3a. Protected region recoloring / component modification
        var protectedEv = criticalEvidences.FirstOrDefault(e => e.Category == ValidationEvidenceCategory.ProtectedRegion || e.Type == "ProtectedAreaChangedPercentage");
        if (protectedEv != null)
        {
            var regionName = protectedEv.Region ?? "protected furniture component";
            return new RetryEvaluationResult
            {
                ShouldRetry = true,
                Reason = RestorationRetryReason.ConfirmedComponentModification,
                CorrectiveConstraint = $"Previous attempt incorrectly altered the appearance or color of {regionName}. The {regionName} must remain completely unchanged from the original photograph.",
                Explanation = $"Concrete protected component modification detected ({protectedEv.Message})."
            };
        }

        // 3b. Large changed connected region (e.g. door/panel alteration)
        var largestRegionEv = criticalEvidences.FirstOrDefault(e => e.Type == "LargestChangedRegion");
        if (largestRegionEv != null)
        {
            return new RetryEvaluationResult
            {
                ShouldRetry = true,
                Reason = RestorationRetryReason.ConfirmedStructuralModification,
                CorrectiveConstraint = "Previous attempt introduced a large contiguous modification across a panel or door boundary. The original cabinetry boundaries, door panels, and frames must remain strictly in their original positions without replacement or removal.",
                Explanation = "Concrete large localized panel/door alteration detected."
            };
        }

        // 3c. Normalized color distribution difference
        var colorDistEv = criticalEvidences.FirstOrDefault(e => e.Category == ValidationEvidenceCategory.Color || e.Type == "NormalizedColorDistribution");
        if (colorDistEv != null)
        {
            return new RetryEvaluationResult
            {
                ShouldRetry = true,
                Reason = RestorationRetryReason.ConfirmedColorModification,
                CorrectiveConstraint = "Previous attempt significantly shifted furniture material colors. The exact original wood stains, paint tones, and surface colors must be preserved identically to the original photograph.",
                Explanation = "Concrete color distribution modification detected."
            };
        }

        // 3d. Aspect ratio alteration
        var aspectEv = criticalEvidences.FirstOrDefault(e => e.Type == "AspectRatioConsistency");
        if (aspectEv != null)
        {
            return new RetryEvaluationResult
            {
                ShouldRetry = true,
                Reason = RestorationRetryReason.ConfirmedStructuralModification,
                CorrectiveConstraint = "Previous attempt modified the framing aspect ratio and proportions of the furniture. The exact perspective, proportions, and outer framing must match the original image.",
                Explanation = "Concrete aspect ratio alteration detected."
            };
        }

        // 3e. Silhouette alteration
        var silEv = criticalEvidences.FirstOrDefault(e => e.Category == ValidationEvidenceCategory.Silhouette || e.Type == "SilhouetteSimilarity");
        if (silEv != null)
        {
            return new RetryEvaluationResult
            {
                ShouldRetry = true,
                Reason = RestorationRetryReason.ConfirmedStructuralModification,
                CorrectiveConstraint = "Previous attempt altered the exterior silhouette of the furniture. The exact outer profile and boundaries of the piece must match the original photograph precisely.",
                Explanation = "Concrete silhouette alteration detected."
            };
        }

        // 3f. Structural similarity or edge failure with no other localized violation -> check if metrics conflict
        if (localReport != null && localReport.StructuralSimilarity >= 0.92 && localReport.ChangedAreaPercentage < 0.05)
        {
            return new RetryEvaluationResult
            {
                ShouldRetry = false,
                Reason = RestorationRetryReason.MetricConflict,
                Explanation = "High structural similarity and low changed area conflict with rejection threshold. Routing to NeedsReview without retry."
            };
        }

        // If no concrete correctable modification could be formulated, do not retry blindly
        return new RetryEvaluationResult
        {
            ShouldRetry = false,
            Reason = RestorationRetryReason.Unknown,
            Explanation = "Rejection occurred without identifiable, concrete correctable modification. Routing to NeedsReview."
        };
    }
}
