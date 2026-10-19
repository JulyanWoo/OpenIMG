namespace OpenIMG.Core.Models;

/// <summary>
/// Record representing an identified contradiction or ambiguity between local contract signals.
/// When detected, affected constraints are downgraded to prevent false-strict instructions.
/// </summary>
public record ContractConflict
{
    public string ConflictId { get; init; } = string.Empty;
    public string ConflictType { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public List<string> AffectedConstraintIds { get; init; } = new();
    public string ResolutionApplied { get; init; } = string.Empty;
}
