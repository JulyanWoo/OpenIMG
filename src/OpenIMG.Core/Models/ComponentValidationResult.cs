using OpenIMG.Core.Enums;

namespace OpenIMG.Core.Models;

public record ComponentValidationResult
{
    public string ComponentId { get; init; } = string.Empty;
    public string ComponentType { get; init; } = string.Empty;
    public ProtectedRegion? Region { get; init; }
    public bool IsStructural { get; init; }
    public string Importance { get; init; } = "normal";
    public double StructuralSimilarity { get; init; }
    public double EdgePrecision { get; init; }
    public double EdgeRecall { get; init; }
    public double EdgeF1 { get; init; }
    public double UnexpectedEdgeRatio { get; init; }
    public double MissingEdgeRatio { get; init; }
    public int UnexpectedEdgeClusterCount { get; init; }
    public int LargestUnexpectedEdgeClusterPixels { get; init; }
    public double LargestUnexpectedEdgeClusterRatio { get; init; }
    public double NormalizedColorDifference { get; init; }
    public double ChangedAreaPercentage { get; init; }
    public string DecisionHint { get; init; } = "Pass";
    public List<ValidationEvidence> Evidences { get; init; } = new();

    // Phase 4C.4 Decomposed Component Change Metrics
    public double TotalChangedAreaRatio { get; init; }
    public double EdgeAlignedChangedAreaRatio { get; init; }
    public double InteriorChangedAreaRatio { get; init; }
    public int TotalPixels { get; init; }
    public int TotalChangedPixels { get; init; }
    public int EdgeAlignedChangedPixels { get; init; }
    public int InteriorChangedPixels { get; init; }
    public double ColorIdentityDelta { get; init; }
    public double LuminanceDelta { get; init; }
    public double ChromaDelta { get; init; }
    public double StructuralBoundaryShift { get; init; }
}
