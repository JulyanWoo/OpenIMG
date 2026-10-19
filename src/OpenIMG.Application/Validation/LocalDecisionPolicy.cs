using OpenIMG.Application.Configuration;
using OpenIMG.Core.Enums;
using OpenIMG.Core.Models;

namespace OpenIMG.Application.Validation;

/// <summary>
/// Applies the deterministic Barrier 3 policy to classified evidence.
/// Measurement and evidence generation remain outside this class.
/// </summary>
public sealed class LocalDecisionPolicy
{
    private readonly ValidationEvidenceClassifier _classifier;

    public LocalDecisionPolicy(ValidationEvidenceClassifier classifier)
    {
        _classifier = classifier;
    }

    public ValidationDecision Evaluate(
        LocalValidationReport report,
        IEnumerable<ValidationEvidence> evaluatedEvidences,
        List<string> photographicNotes,
        out DecisionExplanation explanation)
    {
        var classification = _classifier.Classify(report, evaluatedEvidences);
        var tierAFailures = classification.TierAFailures;
        var tierBFailures = classification.TierBFailures;
        var tierCFailures = classification.TierCFailures;

        if (tierAFailures.Count > 0)
        {
            var reasons = tierAFailures.Select(f => f.Message).Distinct().ToList();
            var warnings = tierBFailures.Concat(tierCFailures).Select(f => f.Message).Distinct().ToList();
            explanation = new DecisionExplanation(reasons, warnings, photographicNotes);
            return ValidationDecision.Rejected;
        }

        var independentTierBConcepts = tierBFailures
            .Select(_classifier.GetTierBConcept)
            .Distinct()
            .Count();

        if (independentTierBConcepts >= 2)
        {
            var reasons = tierBFailures
                .Select(f => $"Corroborated geometric deviation: {f.Message}")
                .Distinct()
                .ToList();
            var warnings = tierCFailures.Select(f => f.Message).Distinct().ToList();
            explanation = new DecisionExplanation(reasons, warnings, photographicNotes);
            return ValidationDecision.Rejected;
        }

        if (tierBFailures.Count > 0)
        {
            var warnings = tierBFailures
                .Concat(tierCFailures)
                .Select(f => f.Message)
                .Distinct()
                .ToList();
            explanation = new DecisionExplanation(
                new List<string> { "None (Physical structure preserved; geometric warning requires review)" },
                warnings,
                photographicNotes);
            return ValidationDecision.NeedsReview;
        }

        var unmitigatedTierC = tierCFailures
            .Where(c => !_classifier.IsNonBlockingPhotographicTierC(c, report))
            .ToList();

        if (unmitigatedTierC.Count > 0)
        {
            var warnings = unmitigatedTierC.Select(f => f.Message).Distinct().ToList();
            var cleanupEvidence = unmitigatedTierC.FirstOrDefault(c => c.Type == "CleanupIncomplete");
            var primaryReason = cleanupEvidence != null
                ? cleanupEvidence.Message
                : "None (Physical structure preserved; low-level photographic differences require review)";

            explanation = new DecisionExplanation(
                new List<string> { primaryReason },
                warnings,
                photographicNotes);
            return ValidationDecision.NeedsReview;
        }

        explanation = new DecisionExplanation(
            new List<string> { "None" },
            new List<string>(),
            photographicNotes);
        return ValidationDecision.Accepted;
    }
}
