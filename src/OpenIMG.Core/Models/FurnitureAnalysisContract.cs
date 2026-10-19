using OpenIMG.Core.Enums;

namespace OpenIMG.Core.Models;

/// <summary>
/// Canonical product representation of what OpenIMG knows about one source
/// photograph. It contains observations and their authority; it is not a
/// provider DTO and it does not contain OpenCV or UI types.
/// </summary>
public record FurnitureAnalysisContract
{
    public string ContractVersion { get; init; } = "product-baseline-v1";
    public CanonicalSourceIdentity SourceIdentity { get; init; } = new();
    public AnalysisSource AnalysisSource { get; init; } = AnalysisSource.Local;

    public string FurnitureType { get; init; } = string.Empty;
    public string GeometryDescription { get; init; } = string.Empty;
    public double Confidence { get; init; }

    public List<FurnitureRegionCandidate> FurnitureRegions { get; init; } = new();
    public NormalizedRectangle? FurnitureBounds { get; init; }
    public ConstraintAuthority FurnitureEnvelopeAuthority { get; init; } = ConstraintAuthority.Uncertain;
    public List<LocalStructuralRegion> StructuralRegions { get; init; } = new();
    public List<FurnitureComponent> Components { get; init; } = new();
    public List<ProtectedRegion> ProtectedRegions { get; init; } = new();
    public List<HardwareCandidate> Hardware { get; init; } = new();
    public List<ComponentCardinalityConstraint> ComponentCardinalityConstraints { get; init; } = new();
    public List<HardwareAbsenceConstraint> HardwareAbsenceConstraints { get; init; } = new();

    public List<LocalMaterialHint> MaterialHints { get; init; } = new();
    public List<LocalColorRegion> ColorRegions { get; init; } = new();
    public List<SceneColorReference> SceneColorReferences { get; init; } = new();
    public List<string> MainMaterials { get; init; } = new();
    public List<string> MainColors { get; init; } = new();
    public List<string> AccentColors { get; init; } = new();
    public Dictionary<string, string> ColorLocations { get; init; } = new();

    public List<ProtectedAbsenceRegion> ProtectedAbsenceRegions { get; init; } = new();
    public List<ObservedNegativeSpaceRegion> NegativeSpaceRegions { get; init; } = new();
    public List<SceneDetailCandidate> PersistentSceneDetails { get; init; } = new();
    public List<SceneObject> SceneObjects { get; init; } = new();
    public List<CleanupTargetRecord> CleanupTargets { get; init; } = new();

    public List<string> Doors { get; init; } = new();
    public List<string> Drawers { get; init; } = new();
    public List<string> Shelves { get; init; } = new();
    public List<string> Handles { get; init; } = new();
    public List<string> GlassAreas { get; init; } = new();
    public List<string> Panels { get; init; } = new();
    public List<string> DecorativeElements { get; init; } = new();
    public List<string> StructuralElements { get; init; } = new();
    public List<string> ProtectedFeatures { get; init; } = new();
    public List<string> UncertainFeatures { get; init; } = new();
    public List<string> PhotoDefects { get; init; } = new();
    public List<string> RemovableClutter { get; init; } = new();

    public ImageFramingAnalysis? Framing { get; init; }
    public LocalImageQualityAssessment? QualityObservations { get; init; }
    public ContractConfidenceMetadata ConfidenceMetadata { get; init; } = new();
    public List<ContractAuthorityRecord> AuthorityRecords { get; init; } = new();
    public List<ContractConflict> Conflicts { get; init; } = new();
}

public record CanonicalSourceIdentity
{
    public string FileName { get; init; } = string.Empty;
    public string Sha256 { get; init; } = string.Empty;
    public ConstraintAuthority Authority { get; init; } = ConstraintAuthority.Strict;
}

public record ContractConfidenceMetadata
{
    public double Overall { get; init; }
    public double Furniture { get; init; }
    public double Structure { get; init; }
    public double Components { get; init; }
    public double Scene { get; init; }
    public double Materials { get; init; }
    public double Colors { get; init; }
    public string EvidenceSummary { get; init; } = string.Empty;
}
