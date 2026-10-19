namespace OpenIMG.Core.Models;

public record CandidateEvaluation
{
    public string FurnitureDecision { get; init; } = "ACCEPTED";
    public string GeometryPreservation { get; init; } = "EXCELLENT";
    public string ComponentPreservation { get; init; } = "EXCELLENT";
    public string ColorIdentityPreservation { get; init; } = "EXCELLENT";
    public string AccentColorLocationPreservation { get; init; } = "EXCELLENT";
    public string MaterialPreservation { get; init; } = "EXCELLENT";
    public string HardwarePreservation { get; init; } = "EXCELLENT";
    public string HardwareCountPreservation { get; init; } = "EXCELLENT";
    public string HardwareLocationPreservation { get; init; } = "EXCELLENT";
    public string AsymmetryPreservation { get; init; } = "EXCELLENT";
    public string FurnitureColorIdentity { get; init; } = "EXCELLENT";
    public string SceneColorIdentity { get; init; } = "EXCELLENT";
    public string ScenePreservation { get; init; } = "EXCELLENT";
    public string FramingPreservation { get; init; } = "EXCELLENT";

    public string PhotographicQualityImprovement { get; init; } = "STRONG";
    public string SharpnessImprovement { get; init; } = "GOOD";
    public string ExposureImprovement { get; init; } = "GOOD";
    public string WhiteBalanceImprovement { get; init; } = "GOOD";
    public string NoiseReduction { get; init; } = "GOOD";
    public string ColorImprovement { get; init; } = "GOOD";

    public bool PerspectiveCorrectionApplied { get; init; }
    public bool PerspectiveCorrectionSafe { get; init; } = true;
    public string PersistentSceneDetailPreservation { get; init; } = "EXCELLENT";
    public string NegativeSpacePreservation { get; init; } = "EXCELLENT";

    public List<string> UnexpectedAddedFurniture { get; init; } = new();
    public List<string> UnexpectedAddedHardware { get; init; } = new();
    public List<string> UnexpectedAddedDetails { get; init; } = new();
    public List<string> UnexpectedRemovedDetails { get; init; } = new();
    public bool OverprocessingDetected { get; init; }
    public string Decision { get; init; } = "ACCEPTED";
    public string Notes { get; init; } = string.Empty;
}

public record ComparisonSummary
{
    public bool MeaningfulFidelityDifferenceDetected { get; init; }
    public string DifferenceDescription { get; init; } = string.Empty;
}

public record Phase4C1ComparativeValidationReport
{
    public string CalibrationId { get; init; } = string.Empty;
    public CandidateEvaluation CandidateA { get; init; } = new();
    public CandidateEvaluation CandidateB { get; init; } = new();
    public ComparisonSummary Comparison { get; init; } = new();
    public string RawGeminiResponse { get; init; } = string.Empty;
}
