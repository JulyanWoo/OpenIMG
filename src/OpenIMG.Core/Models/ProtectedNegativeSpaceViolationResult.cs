using OpenIMG.Core.Enums;

namespace OpenIMG.Core.Models;

public record ProtectedNegativeSpaceViolationResult
{
    public string ViolationId { get; init; } = string.Empty;
    public string NegativeSpaceRegionId { get; init; } = string.Empty;
    public NormalizedRectangle Region { get; init; } = new(0, 0, 0, 0);
    public ConstraintAuthority ProtectionLevel { get; init; } = ConstraintAuthority.Strict;
    public ValidationSeverity Severity { get; init; } = ValidationSeverity.Critical;
    public double Confidence { get; init; }
    public double OccupancyRatio { get; init; }
    public int UnexpectedEdgePixels { get; init; }
    public bool IsViolation { get; init; }
    public string Description { get; init; } = string.Empty;
}
