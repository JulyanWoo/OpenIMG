namespace OpenIMG.Core.Models;

public record GeminiValidationReport
{
    public bool GeometryChanged { get; init; }
    public bool ColorsChanged { get; init; }
    public bool ColorDistributionChanged { get; init; }
    public bool MaterialsChanged { get; init; }
    public bool DoorsChanged { get; init; }
    public bool DrawersChanged { get; init; }
    public bool ShelvesChanged { get; init; }
    public bool HandlesChanged { get; init; }
    public bool GlassChanged { get; init; }
    public bool PanelsChanged { get; init; }
    public bool DecorativeElementsChanged { get; init; }
    public bool StructuralElementsChanged { get; init; }
    public bool CameraCompositionSignificantlyChanged { get; init; }
    public List<string> UnexpectedChanges { get; init; } = new();
    public List<string> UncertainChanges { get; init; } = new();
    public double Confidence { get; init; }
    public string FurnitureDecision { get; init; } = "UNCERTAIN"; // PASS, FAIL, UNCERTAIN
    public string SceneCleanupDecision { get; init; } = "NOT_APPLICABLE"; // ACCEPTABLE, EXCESSIVE, UNCERTAIN, NOT_APPLICABLE
    public string ReframingDecision { get; init; } = "NOT_APPLICABLE"; // ACCEPTABLE, EXCESSIVE, UNCERTAIN, NOT_APPLICABLE
    public string Decision { get; init; } = "UNCERTAIN"; // PASS, FAIL, UNCERTAIN
    public string? Notes { get; init; }
    public string? RawResponseJson { get; init; }
}
