using OpenIMG.Core.Enums;

namespace OpenIMG.Core.Models;

/// <summary>
/// Typed, evidence-backed reconciliation instructions for correcting a draft.
/// It separates physical source truth from draft photographic/cleanup work.
/// </summary>
public sealed record CorrectiveRestorationContract
{
    public string SourceSha256 { get; init; } = string.Empty;
    public CorrectiveRestorationRoute Route { get; init; } = CorrectiveRestorationRoute.DeltaCorrection;
    public string RouteReason { get; init; } = "No structural conflict was reported by local comparison.";
    public List<CorrectiveInstruction> PreserveFromDraft { get; init; } = new();
    public List<CorrectiveInstruction> RestoreFromOriginal { get; init; } = new();
    public List<CorrectiveInstruction> RemoveUnsupportedFromDraft { get; init; } = new();
    public List<CorrectiveInstruction> UncertainDoNotInfer { get; init; } = new();
}

/// <summary>One safe-to-render instruction backed by a known evidence type.</summary>
public sealed record CorrectiveInstruction
{
    public string EvidenceType { get; init; } = "Policy";
    public string? Region { get; init; }
    public NormalizedRectangle? Location { get; init; }
    public string Feature { get; init; } = "permanent component";
    public ValidationSeverity Severity { get; init; } = ValidationSeverity.Info;
    public double Confidence { get; init; }

    public static CorrectiveInstruction Policy(string evidenceType) => new()
    {
        EvidenceType = evidenceType
    };

    public static CorrectiveInstruction Evidence(string evidenceType, string? region, ValidationSeverity severity, double confidence = 1, string feature = "permanent component", NormalizedRectangle? location = null)
        => new()
        {
            EvidenceType = evidenceType,
            Region = region,
            Location = location,
            Feature = feature,
            Severity = severity,
            Confidence = double.IsFinite(confidence) ? Math.Clamp(confidence, 0, 1) : 0
        };
}
