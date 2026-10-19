namespace OpenIMG.Core.Models;

public enum CropSeverity
{
    None,
    Minor,
    Moderate,
    Severe,
    Uncertain
}

public enum MissingContextType
{
    SceneOnly,
    FurnitureBoundaryNearEdge,
    FurnitureStructureCut,
    Uncertain
}

public record RecommendedExpansion
{
    public double TopPercent { get; init; }
    public double BottomPercent { get; init; }
    public double LeftPercent { get; init; }
    public double RightPercent { get; init; }
}

public record ImageFramingAnalysis
{
    public bool IsFurnitureCropped { get; init; }
    public CropSeverity CropSeverity { get; init; } = CropSeverity.None;
    public List<string> CropEdges { get; init; } = new();
    public MissingContextType MissingContextType { get; init; } = MissingContextType.SceneOnly;
    public bool SafeZoomOutRecommended { get; init; }
    public RecommendedExpansion RecommendedExpansion { get; init; } = new();
    public double FramingConfidence { get; init; } = 1.0;
}
