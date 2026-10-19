namespace OpenIMG.Core.Models;

public enum SceneClassification
{
    Uncertain = 0,
    RemovableClutter,
    Person,
    Reflection,
    PermanentEnvironment
}

public record SceneObject
{
    public string Id { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public SceneClassification Classification { get; init; } = SceneClassification.Uncertain;
    public RegionPrecedence Precedence { get; init; } = RegionPrecedence.RemovableScene;
    public OcclusionType Occlusion { get; init; } = OcclusionType.SceneOnly;
    public ProtectedRegion? Region { get; init; }
    public bool OverlapsFurniture { get; init; }
    public bool IsRemovable { get; init; }
    public double Confidence { get; init; } = 1.0;
}
