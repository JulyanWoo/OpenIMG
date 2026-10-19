namespace OpenIMG.Core.Models;

public record LocalDetectedObject
{
    public string Id { get; init; } = string.Empty;
    public string ClassName { get; init; } = string.Empty;
    public double Confidence { get; init; }
    public NormalizedRectangle Region { get; init; } = new(0, 0, 0, 0);
    public string Source { get; init; } = "OnnxDetector"; // OnnxDetector, OpenCv, Derived
    public bool IsRemovableClutter { get; init; }
    public OpenIMG.Core.Enums.ConstraintAuthority Authority { get; init; } = OpenIMG.Core.Enums.ConstraintAuthority.Advisory;
}
