using OpenIMG.Core.Enums;

namespace OpenIMG.Core.Models;

public record ObservedNegativeSpaceRegion
{
    public string RegionId { get; init; } = string.Empty;
    public NormalizedRectangle Region { get; init; } = new(0, 0, 0, 0);
    public double Confidence { get; init; } = 0.85;
    public string SourceEvidence { get; init; } = string.Empty;
    public NegativeSpaceType SpaceType { get; init; } = NegativeSpaceType.UnknownNonFurniture;
    public double StructuralEdgeDensity { get; init; }
    public double TextureConsistency { get; init; }
    public double BoundaryConfidence { get; init; } = 0.85;
    public ConstraintAuthority ProtectionLevel { get; init; } = ConstraintAuthority.Strict;
}
