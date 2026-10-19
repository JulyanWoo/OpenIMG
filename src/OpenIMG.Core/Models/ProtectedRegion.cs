namespace OpenIMG.Core.Models;

public record ProtectedRegion
{
    public double X { get; init; }
    public double Y { get; init; }
    public double Width { get; init; }
    public double Height { get; init; }
    public string RegionType { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public double Importance { get; init; } = 1.0;
}
