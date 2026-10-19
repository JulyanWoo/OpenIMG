using OpenIMG.Core.Enums;

namespace OpenIMG.Core.Models;

/// <summary>
/// Auditable summary report of confidence-gated authority and conflict resolution for a local contract.
/// </summary>
public record LocalContractAuthorityReport
{
    public string CalibrationId { get; init; } = string.Empty;
    public string SourceFileName { get; init; } = string.Empty;
    public List<ContractAuthorityRecord> Constraints { get; init; } = new();
    public List<ContractConflict> Conflicts { get; init; } = new();
    public bool HasLowCoverageEnvelope { get; init; }
    public bool HasColorConflict { get; init; }
    public bool HasClutterCandidate { get; init; }
    public List<string> StrictConstraints { get; init; } = new();
    public List<string> AdvisoryObservations { get; init; } = new();
    public List<string> UncertainConstraints { get; init; } = new();
}
