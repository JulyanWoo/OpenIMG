namespace OpenIMG.Core.Models;

public record ComponentCardinalityConstraint
{
    public string ConstraintId { get; init; } = string.Empty;
    public string Category { get; init; } = "component_candidate";
    public int RawCandidateCount { get; init; }
    public int DeduplicatedCount { get; init; }
    public int MinimumAllowedCount { get; init; }
    public int MaximumAllowedCount { get; init; }
    public ProtectedRegion Region { get; init; } = new();
    public double Confidence { get; init; }
    public List<string> SourceRegionIds { get; init; } = new();
    public bool Strict { get; init; }
    public string Side { get; init; } = "None"; // Left, Right, Center, Top, Bottom, None
    public string RelativePosition { get; init; } = "General"; // RightFlank, LeftFlank, Interior, General
    public bool IsSingletonMechanism { get; init; }
    public string? OppositeAbsenceConstraintId { get; init; }
    public string? CompactedPromptStatement { get; init; }
}

public record HardwareAbsenceConstraint
{
    public string ConstraintId { get; init; } = string.Empty;
    public ProtectedRegion Region { get; init; } = new();
    public string HardwareFamily { get; init; } = "elongated_hardware_mechanism";
    public int ObservedCount { get; init; } = 0;
    public double Confidence { get; init; } = 0.85;
    public string? AdjacentConstraintId { get; init; }
    public bool Strict { get; init; } = true;
    public string Side { get; init; } = "Left";
    public string SourceEvidence { get; init; } = string.Empty;
}

public record ProtectedAbsenceRegion
{
    public string ConstraintId { get; init; } = string.Empty;
    public ProtectedRegion Region { get; init; } = new();
    public string AbsenceType { get; init; } = "OpenScene";
    public double Confidence { get; init; }
    public List<string> AdjacentFurnitureIds { get; init; } = new();
    public string SourceEvidence { get; init; } = string.Empty;
    public bool Strict { get; init; }
}

public record SceneDetailCandidate
{
    public string CandidateId { get; init; } = string.Empty;
    public ProtectedRegion Region { get; init; } = new();
    public double Confidence { get; init; }
    public string SourceEvidence { get; init; } = string.Empty;
    public bool Strict { get; init; }
    public double ContrastScore { get; init; }
    public double BoundaryStrength { get; init; }
    public string ShapeDescriptor { get; init; } = "compact_contour";
    public string AttachmentContext { get; init; } = "adjacent_scene";
}

public record SceneColorReference
{
    public string RegionId { get; init; } = string.Empty;
    public ProtectedRegion Region { get; init; } = new();
    public string Role { get; init; } = "Wall"; // Wall, Ceiling, Floor, Background, PermanentSurface, UnknownSceneSurface
    public LabColor ReferenceLab { get; init; } = new(0, 0, 0);
    public string ReferenceRgb { get; init; } = "#FFFFFF";
    public string HueFamily { get; init; } = "warm_beige_taupe";
    public string WarmCoolBias { get; init; } = "Warm"; // Warm, Cool, Neutral
    public double Chroma { get; init; }
    public double HueDegrees { get; init; }
    public double RegionVariance { get; init; }
    public double Confidence { get; init; } = 0.85;
    public bool Protected { get; init; } = true;
    public bool AllowedLuminanceAdjustment { get; init; } = true;
    public bool ProhibitNeutralization { get; init; } = true;
    public OpenIMG.Core.Enums.ConstraintAuthority Authority { get; init; } = OpenIMG.Core.Enums.ConstraintAuthority.Strict;
    public bool ColorConflictDetected { get; init; }
    public string SourceEvidence { get; init; } = string.Empty;
}
