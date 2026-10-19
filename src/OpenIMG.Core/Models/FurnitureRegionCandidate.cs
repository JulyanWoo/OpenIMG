namespace OpenIMG.Core.Models;

public record FurnitureRegionCandidate
{
    public NormalizedRectangle Region { get; init; } = new(0, 0, 0, 0);
    public double Confidence { get; init; }
    public bool LowCoverage { get; init; }
    public OpenIMG.Core.Enums.ConstraintAuthority Authority { get; init; } = OpenIMG.Core.Enums.ConstraintAuthority.Strict;
    public string Evidence { get; init; } = string.Empty;
}
