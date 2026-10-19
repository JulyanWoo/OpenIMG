namespace OpenIMG.Core.Models;

public record FurnitureAnalysis
{
    public string FurnitureType { get; init; } = string.Empty;
    public string GeometryDescription { get; init; } = string.Empty;
    public double Confidence { get; init; } = 1.0;
    public List<FurnitureComponent> Components { get; init; } = new();
    public List<string> MainMaterials { get; init; } = new();
    public List<string> MainColors { get; init; } = new();
    public List<string> AccentColors { get; init; } = new();
    public Dictionary<string, string> ColorLocations { get; init; } = new();
    public List<string> Doors { get; init; } = new();
    public List<string> Drawers { get; init; } = new();
    public List<string> Shelves { get; init; } = new();
    public List<string> Handles { get; init; } = new();
    public List<string> GlassAreas { get; init; } = new();
    public List<string> Panels { get; init; } = new();
    public List<string> DecorativeElements { get; init; } = new();
    public List<string> StructuralElements { get; init; } = new();
    public List<string> ProtectedFeatures { get; init; } = new();
    public List<ProtectedRegion> ProtectedRegions { get; init; } = new();
    public List<ComponentCardinalityConstraint> ComponentCardinalityConstraints { get; set; } = new();
    public List<HardwareAbsenceConstraint> HardwareAbsenceConstraints { get; set; } = new();
    public List<SceneColorReference> SceneColorReferences { get; set; } = new();
    public List<ProtectedAbsenceRegion> ProtectedAbsenceRegions { get; set; } = new();
    public List<SceneDetailCandidate> PersistentSceneDetails { get; set; } = new();
    public List<ObservedNegativeSpaceRegion> NegativeSpaceRegions { get; set; } = new();
    public List<string> UncertainFeatures { get; init; } = new();
    public List<string> PhotoDefects { get; init; } = new();
    public ImageFramingAnalysis? FramingAnalysis { get; set; }
    public List<SceneObject> SceneObjects { get; set; } = new();
    public List<string> RemovableClutter { get; set; } = new();
    public OpenIMG.Core.Enums.ConstraintAuthority FurnitureEnvelopeAuthority { get; set; } = OpenIMG.Core.Enums.ConstraintAuthority.Strict;
    public List<ContractAuthorityRecord> AuthorityRecords { get; set; } = new();
    public List<ContractConflict> Conflicts { get; set; } = new();
    public string? RawAnalysisJson { get; set; }
}
