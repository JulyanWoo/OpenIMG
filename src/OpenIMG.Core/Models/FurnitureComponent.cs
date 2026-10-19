namespace OpenIMG.Core.Models;

public record FurnitureComponent
{
    public string Id { get; init; } = string.Empty;
    public string? ParentComponentId { get; init; }
    public string Type { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string Color { get; init; } = string.Empty;
    public string Material { get; init; } = string.Empty;
    public ProtectedRegion? Region { get; init; }
    public string Importance { get; init; } = "high";
    public double Confidence { get; init; } = 1.0;
    public bool IsStructural { get; init; } = true;
}
