using OpenIMG.Core.Enums;

namespace OpenIMG.Core.Models;

public record FallbackEvaluationReport
{
    public string CalibrationId { get; init; } = string.Empty;
    public string SourceFileName { get; init; } = string.Empty;
    public double LocalConfidence { get; init; }
    public LocalAnalysisRisk Risk { get; init; } = LocalAnalysisRisk.Low;
    public bool FallbackRequired { get; init; }
    public bool RequiresGeminiAnalysis { get; init; }
    public bool RequiresGeminiComparativeValidation { get; init; }
    public List<string> FallbackReasons { get; init; } = new();
    public List<FallbackReasonEntry> BlockingReasons { get; init; } = new();
    public List<FallbackReasonEntry> WarningReasons { get; init; } = new();
    public List<FallbackReasonEntry> InformationalReasons { get; init; } = new();
    public FurnitureIoUClassification IoUClassification { get; init; } = FurnitureIoUClassification.Uncertain;
    public FurnitureRegionComparisonStatus FurnitureStatus { get; init; } = FurnitureRegionComparisonStatus.Measured;
    public StructuralComparisonStatus StructuralStatus { get; init; } = StructuralComparisonStatus.Measured;
    public HardwareComparisonStatus HardwareStatus { get; init; } = HardwareComparisonStatus.Measured;
    public AccentColorComparisonStatus AccentColorStatus { get; init; } = AccentColorComparisonStatus.Measured;
    public string? Notes { get; init; }
}
