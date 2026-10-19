using OpenIMG.Core.Enums;

namespace OpenIMG.Core.Models;

public record UnsupportedPhysicalAdditionResult
{
    public string AdditionId { get; init; } = string.Empty;
    public NormalizedRectangle Region { get; init; } = new(0, 0, 0, 0);
    public PhysicalAdditionScale Scale { get; init; } = PhysicalAdditionScale.Small;
    public double Confidence { get; init; }
    public int CoherentEdgePixels { get; init; }
    public double CoherentClusterRatio { get; init; }
    public double StructuralEdgeDensityGain { get; init; }
    public SourceSupportStatus SourceSupportStatus { get; init; } = SourceSupportStatus.Unsupported;
    public string SourceSupportEvidence { get; init; } = string.Empty;
    public ValidationSeverity Severity { get; init; } = ValidationSeverity.Critical;
    public bool IsViolation { get; init; }
    public PhysicalAdditionCategory Category { get; init; } = PhysicalAdditionCategory.FurniturePhysicalAddition;
    public string Description { get; init; } = string.Empty;
}
