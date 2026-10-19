using OpenIMG.Core.Enums;

namespace OpenIMG.Core.Models;

public record RiskEvaluationReport
{
    public string CalibrationId { get; init; } = string.Empty;
    public string SourceFileName { get; init; } = string.Empty;
    public LocalAnalysisRisk Risk { get; init; } = LocalAnalysisRisk.Low;
    public double LocalConfidence { get; init; }
    public List<string> RiskFactors { get; init; } = new();
    public bool FurnitureLocalizationReliable { get; init; }
    public bool StructuralProtectionSufficient { get; init; }
    public bool ColorProtectionSufficient { get; init; }
    public bool GlassOrSpecularDetected { get; init; }
    public bool HardwareComplexityPresent { get; init; }
    public bool OcclusionOrClutterPresent { get; init; }
    public string Recommendation { get; init; } = string.Empty;
}
