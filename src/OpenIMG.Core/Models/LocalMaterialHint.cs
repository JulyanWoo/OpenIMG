using OpenIMG.Core.Enums;

namespace OpenIMG.Core.Models;

public record LocalMaterialHint
{
    public NormalizedRectangle Region { get; init; } = new(0, 0, 0, 0);
    public MaterialHintType Material { get; init; } = MaterialHintType.Unknown;
    public double Confidence { get; init; }
    public string Rationale { get; init; } = string.Empty;
}
