using OpenIMG.Core.Enums;

namespace OpenIMG.Core.Models;

public record LocalFurnitureContract
{
    /// <summary>
    /// Version of the authority-resolution policy used to turn local observations into prompt constraints.
    /// Empty values identify contracts persisted before authority propagation existed and must be refreshed.
    /// </summary>
    public string AuthorityPolicyVersion { get; init; } = string.Empty;
    public string SourceFileName { get; init; } = string.Empty;
    public string SourceSha256 { get; init; } = string.Empty;
    public LocalAnalysisStatus Status { get; init; } = LocalAnalysisStatus.Success;
    public List<LocalDetectedObject> DetectedObjects { get; init; } = new();
    public List<LocalStructuralRegion> StructuralRegions { get; init; } = new();
    public List<HardwareCandidate> HardwareCandidates { get; init; } = new();
    public List<LocalColorRegion> ColorRegions { get; init; } = new();
    public List<LocalMaterialHint> MaterialHints { get; init; } = new();
    public List<LocalDetectedObject> SceneObjects { get; init; } = new();
    public List<FurnitureRegionCandidate> FurnitureRegionCandidates { get; init; } = new();
    public List<ProtectedAbsenceRegion> ProtectedAbsenceRegions { get; init; } = new();
    public List<HardwareAbsenceConstraint> HardwareAbsenceConstraints { get; init; } = new();
    public List<SceneColorReference> SceneColorReferences { get; init; } = new();
    public List<SceneDetailCandidate> PersistentSceneDetails { get; init; } = new();
    public List<ObservedNegativeSpaceRegion> NegativeSpaceRegions { get; init; } = new();
    public NormalizedRectangle? FurnitureBounds { get; init; }
    public double FurnitureConfidence { get; init; }
    public ConstraintAuthority FurnitureEnvelopeAuthority { get; init; } = ConstraintAuthority.Strict;
    public double SceneComplexity { get; init; }
    public double LocalConfidence { get; init; }
    public LocalConfidenceBreakdown ConfidenceBreakdown { get; init; } = new();
    public List<ContractAuthorityRecord> AuthorityRecords { get; init; } = new();
    public List<ContractConflict> Conflicts { get; init; } = new();
    public List<string> Warnings { get; init; } = new();
    public LocalModelInfo? ModelInfo { get; init; }
    public TimeSpan AnalysisDuration { get; init; }
    public double AnalysisDurationMs => AnalysisDuration.TotalMilliseconds;
}
