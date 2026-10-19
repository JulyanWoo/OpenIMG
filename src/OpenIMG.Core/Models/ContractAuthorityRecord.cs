using OpenIMG.Core.Enums;

namespace OpenIMG.Core.Models;

/// <summary>
/// Detailed auditable record for an individual restoration constraint and its authority gating.
/// </summary>
public record ContractAuthorityRecord
{
    public string ConstraintId { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty;
    public double Confidence { get; init; }
    public ConstraintAuthority Authority { get; init; } = ConstraintAuthority.Strict;
    public string SourceEvidence { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
    public bool ConflictDetected { get; init; }
    public string? ConflictReason { get; init; }
    public string PromptStatement { get; init; } = string.Empty;
}
