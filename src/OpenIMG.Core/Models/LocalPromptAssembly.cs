namespace OpenIMG.Core.Models;

public record LocalPromptAssembly
{
    public string AssemblyId { get; init; } = string.Empty;
    public string AssemblyType { get; init; } = string.Empty;
    public NormalizedRectangle Bounds { get; init; } = new(0, 0, 0, 0);
    public int MemberCount { get; init; }
    public List<string> MemberRegionIds { get; init; } = new();
    public string StructuralPattern { get; init; } = string.Empty;
    public string Orientation { get; init; } = "none";
    public double? RepeatedSpacing { get; init; }
    public List<string> PrimaryColors { get; init; } = new();
    public string Material { get; init; } = "observed structural finish";
    public bool Protected { get; init; } = true;
    public double Confidence { get; init; }
    public OpenIMG.Core.Enums.ConstraintAuthority Authority { get; init; } = OpenIMG.Core.Enums.ConstraintAuthority.Strict;
    public string? AuthorityReason { get; init; }
    public string Summary { get; init; } = string.Empty;
}

public record HardwareCompactionTrace
{
    public string ConstraintId { get; init; } = string.Empty;
    public List<string> SourceRegionIds { get; init; } = new();
    public int ObservedCount { get; init; }
    public NormalizedRectangle LocalizedBounds { get; init; } = new(0, 0, 0, 0);
    public string CompactedPromptStatement { get; init; } = string.Empty;
}

public record PromptCompactionReport
{
    public int RawRegionCount { get; init; }
    public int CompactedAssemblyCount { get; init; }
    public int IndividualCriticalRegionCount { get; init; }
    public int PromptCharacterCount { get; init; }
    public int EstimatedPromptTokenCount { get; init; }
    public double CompactionRatio { get; init; }
    public List<LocalPromptAssembly> Assemblies { get; init; } = new();
    public List<string> IndividualCriticalRegionIds { get; init; } = new();
    public List<HardwareCompactionTrace> HardwareTraces { get; init; } = new();
    public List<string> DiagnosticNotes { get; init; } = new();
}
