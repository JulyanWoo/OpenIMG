using OpenIMG.Core.Enums;

namespace OpenIMG.Core.Models;

public record LocalStructuralRegion
{
    public string Id { get; init; } = string.Empty;
    public NormalizedRectangle Region { get; init; } = new(0, 0, 0, 0);
    public StructuralRegionType RegionType { get; init; } = StructuralRegionType.UnknownStructuralRegion;
    public double Confidence { get; init; }
    public string Orientation { get; init; } = "Neutral"; // Horizontal, Vertical, Neutral
    public double AspectRatio { get; init; }
    public string Notes { get; init; } = string.Empty;
}
