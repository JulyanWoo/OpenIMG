namespace OpenIMG.Core.Models;

public record HardwareCandidate
{
    public string Id { get; init; } = string.Empty;
    public NormalizedRectangle Region { get; init; } = new(0, 0, 0, 0);
    public string GeometryType { get; init; } = "hardware_candidate";
    public double Confidence { get; init; }
    public string Orientation { get; init; } = "horizontal";
    public double ContrastScore { get; init; }
    public string? RepeatGroupId { get; init; }
}
