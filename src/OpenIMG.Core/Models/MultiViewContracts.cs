namespace OpenIMG.Core.Models;

public sealed class FurnitureImageGroup
{
    public string GroupId { get; set; } = string.Empty;
    public List<string> ImageIds { get; set; } = [];
    public string CreationMethod { get; set; } = "DeterministicShadow";
    public string Confidence { get; set; } = "Uncertain";
    public List<string> GroupingEvidence { get; set; } = [];
    public bool HumanConfirmed { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class SharedFurnitureIdentityContract
{
    public string GroupId { get; set; } = string.Empty;
    public List<SharedIdentityProperty> Properties { get; set; } = [];
    public List<string> SupportingViews { get; set; } = [];
    public List<string> ConflictingObservations { get; set; } = [];
    public List<SharedMaterialAssignment> SharedMaterialAssignments { get; set; } = [];
    public double ContractConfidence { get; set; }
}

public sealed class SharedIdentityProperty
{
    public string PropertyId { get; set; } = string.Empty;
    public string PropertyType { get; set; } = string.Empty;
    public string ObservedValue { get; set; } = string.Empty;
    public double Confidence { get; set; }
    public string Authority { get; set; } = "Advisory";
    public List<string> SupportingViews { get; set; } = [];
    public List<string> ConflictingViews { get; set; } = [];
    public List<string> Evidence { get; set; } = [];
}

public sealed class ViewSpecificFurnitureContract
{
    public string ImageId { get; set; } = string.Empty;
    public string GroupId { get; set; } = string.Empty;
    public List<string> VisibleComponents { get; set; } = [];
    public List<string> VisibleHardware { get; set; } = [];
    public List<string> VisibleShelves { get; set; } = [];
    public List<string> OccludedRegions { get; set; } = [];
    public string CurrentFurnitureEnvelope { get; set; } = string.Empty;
    public List<string> CurrentNegativeSpaces { get; set; } = [];
    public List<string> PersistentSceneDetails { get; set; } = [];
    public List<string> CleanupTargets { get; set; } = [];
    public List<string> CurrentWallColorReferences { get; set; } = [];
}

public sealed class ReferenceCandidateScore
{
    public string ImageId { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public double Sharpness { get; set; }
    public double ExposureQuality { get; set; }
    public double FurnitureCoverage { get; set; }
    public double MaterialVisibility { get; set; }
    public double HardwareVisibility { get; set; }
    public double StructuralVisibility { get; set; }
    public double Suitability { get; set; }
    public List<string> Reasons { get; set; } = [];
}
