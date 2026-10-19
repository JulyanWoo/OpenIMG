using OpenIMG.Core.Enums;
using OpenIMG.Core.Models;

namespace OpenIMG.Application.Validation;

/// <summary>
/// Combines deterministic Barrier 3 with an optional remote comparative
/// result. Local critical physical evidence always remains authoritative.
/// </summary>
public sealed class RemoteDecisionCombiner
{
    public ValidationDecision Combine(
        ValidationDecision localDecision,
        GeminiValidationReport? remoteReport,
        out List<ValidationEvidence> finalEvidences,
        IEnumerable<ValidationEvidence> localEvidences,
        bool allowSemanticResolution = false)
    {
        finalEvidences = new List<ValidationEvidence>(localEvidences);

        if (localDecision == ValidationDecision.Rejected)
        {
            return ValidationDecision.Rejected;
        }

        if (remoteReport == null)
        {
            finalEvidences.Add(new ValidationEvidence
            {
                Category = ValidationEvidenceCategory.Diagnostic,
                Type = "GeminiValidation",
                Score = 0.0,
                Threshold = 1.0,
                Severity = ValidationSeverity.Warning,
                Passed = false,
                Message = "Gemini remote validation report is missing."
            });
            return ValidationDecision.NeedsReview;
        }

        if (string.Equals(remoteReport.FurnitureDecision, "FAIL", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(remoteReport.Decision, "FAIL", StringComparison.OrdinalIgnoreCase))
        {
            finalEvidences.Add(new ValidationEvidence
            {
                Category = ValidationEvidenceCategory.Diagnostic,
                Type = "GeminiValidation",
                Score = 0.0,
                Threshold = 1.0,
                Severity = ValidationSeverity.Critical,
                Passed = false,
                Message = $"Gemini comparative validator detected furniture alteration (FAIL). Notes: {remoteReport.Notes}"
            });
            return ValidationDecision.Rejected;
        }

        if (string.Equals(remoteReport.FurnitureDecision, "UNCERTAIN", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(remoteReport.Decision, "UNCERTAIN", StringComparison.OrdinalIgnoreCase))
        {
            finalEvidences.Add(new ValidationEvidence
            {
                Category = ValidationEvidenceCategory.Diagnostic,
                Type = "GeminiValidation",
                Score = 0.5,
                Threshold = 1.0,
                Severity = ValidationSeverity.Warning,
                Passed = false,
                Message = $"Gemini comparative validator reported uncertainty. Notes: {remoteReport.Notes}"
            });
            return ValidationDecision.NeedsReview;
        }

        if (string.Equals(remoteReport.FurnitureDecision, "PASS", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(remoteReport.Decision, "PASS", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(remoteReport.FurnitureDecision, "ACCEPTED", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(remoteReport.Decision, "ACCEPTED", StringComparison.OrdinalIgnoreCase))
        {
            if (finalEvidences.Any(e => e.Severity == ValidationSeverity.Critical && !e.Passed))
            {
                finalEvidences.Add(new ValidationEvidence
                {
                    Category = ValidationEvidenceCategory.Diagnostic,
                    Type = "GeminiPassOverriddenByLocalCritical",
                    Score = 0.0,
                    Threshold = 1.0,
                    Severity = ValidationSeverity.Critical,
                    Passed = false,
                    Message = "Gemini PASS cannot override unresolved deterministic local physical Critical violation."
                });
                return ValidationDecision.Rejected;
            }

            if (string.Equals(remoteReport.SceneCleanupDecision, "EXCESSIVE", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(remoteReport.ReframingDecision, "EXCESSIVE", StringComparison.OrdinalIgnoreCase))
            {
                finalEvidences.Add(new ValidationEvidence
                {
                    Category = ValidationEvidenceCategory.Diagnostic,
                    Type = "GeminiValidation",
                    Score = 0.5,
                    Threshold = 1.0,
                    Severity = ValidationSeverity.Warning,
                    Passed = false,
                    Message = $"Gemini reported excessive cleanup or reframing. Notes: {remoteReport.Notes}"
                });
                return ValidationDecision.NeedsReview;
            }

            if (localDecision == ValidationDecision.NeedsReview)
            {
                if (allowSemanticResolution)
                {
                    if (finalEvidences.Any(e => e.Type == "InsufficientPhotographicImprovement" && !e.Passed))
                    {
                        finalEvidences.Add(new ValidationEvidence
                        {
                            Category = ValidationEvidenceCategory.Diagnostic,
                            Type = "PhotographicImprovementInsufficientWarningRetained",
                            Score = 0.5,
                            Threshold = 1.0,
                            Severity = ValidationSeverity.Warning,
                            Passed = false,
                            Message = "Candidate has high structural fidelity (Gemini PASS), but photographic improvement remains insufficient on a degraded source. Requires human review."
                        });
                        return ValidationDecision.NeedsReview;
                    }

                    finalEvidences.Add(new ValidationEvidence
                    {
                        Category = ValidationEvidenceCategory.Diagnostic,
                        Type = "GeminiValidation",
                        Score = 1.0,
                        Threshold = 1.0,
                        Severity = ValidationSeverity.Info,
                        Passed = true,
                        Message = "Gemini comparative validator confirmed semantic fidelity, resolving local warnings (PASS)."
                    });
                    return ValidationDecision.Accepted;
                }

                return ValidationDecision.NeedsReview;
            }

            finalEvidences.Add(new ValidationEvidence
            {
                Category = ValidationEvidenceCategory.Diagnostic,
                Type = "GeminiValidation",
                Score = 1.0,
                Threshold = 1.0,
                Severity = ValidationSeverity.Info,
                Passed = true,
                Message = "Gemini comparative validator confirmed no furniture alteration (PASS)."
            });
            return ValidationDecision.Accepted;
        }

        return ValidationDecision.NeedsReview;
    }
}
