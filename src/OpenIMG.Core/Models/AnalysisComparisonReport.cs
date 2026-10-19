using OpenIMG.Core.Enums;

namespace OpenIMG.Core.Models;

public record ComparisonSemanticMatch
{
    public ComparisonSemanticCategory Category { get; init; } = ComparisonSemanticCategory.Unknown;
    public string GeminiLabel { get; init; } = string.Empty;
    public string LocalLabel { get; init; } = string.Empty;
    public NormalizedRectangle GeminiRegion { get; init; } = new(0, 0, 0, 0);
    public NormalizedRectangle LocalRegion { get; init; } = new(0, 0, 0, 0);
    public double IoU { get; init; }
}

public record AnalysisComparisonReport
{
    public string CalibrationId { get; init; } = string.Empty;
    public string SourceFileName { get; init; } = string.Empty;
    public string SourceSha256 { get; init; } = string.Empty;

    // Measured comparison metrics (nullable to represent missing data truthfully)
    public double? FurnitureRegionOverlap { get; init; }
    public double? DominantColorAgreement { get; init; }
    public double? AccentColorAgreement { get; init; }
    public double? StructuralRegionCoverage { get; init; }
    public double? SceneObjectAgreement { get; init; }
    public int ObjectCountDifference { get; init; }
    public List<NormalizedRectangle> LocalOnlyRegions { get; init; } = new();
    public List<NormalizedRectangle> GeminiOnlyRegions { get; init; } = new();
    public double? HardwareRegionOverlap { get; init; }
    public double? MajorBoundaryAgreement { get; init; }
    public List<ComparisonSemanticMatch> SemanticMatches { get; init; } = new();

    // Explicit measurement statuses
    public FurnitureRegionComparisonStatus FurnitureRegionStatus { get; init; } = FurnitureRegionComparisonStatus.Measured;
    public StructuralComparisonStatus StructuralStatus { get; init; } = StructuralComparisonStatus.Measured;
    public HardwareComparisonStatus HardwareStatus { get; init; } = HardwareComparisonStatus.Measured;
    public AccentColorComparisonStatus AccentColorStatus { get; init; } = AccentColorComparisonStatus.Measured;
    public FurnitureIoUClassification IoUClassification { get; init; } = FurnitureIoUClassification.Uncertain;

    // Risk and Fallback Simulation
    public LocalAnalysisRisk Risk { get; init; } = LocalAnalysisRisk.Low;
    public bool GeminiFallbackWouldHaveBeenRequired { get; init; }
    public string? FallbackReason { get; init; }
    public bool RequiresGeminiAnalysis { get; init; }
    public bool RequiresGeminiComparativeValidation { get; init; }
    public List<FallbackReasonEntry> DetailedReasons { get; init; } = new();
    public List<string> BlockingReasons { get; init; } = new();
    public List<string> WarningReasons { get; init; } = new();
    public List<string> InformationalReasons { get; init; } = new();

    // Barrier 4 Simulation
    public bool WouldRemoteValidationBeRequired { get; init; }
    public string? RemoteValidationSimulationReason { get; init; }

    // Confidence & Timings
    public double LocalConfidence { get; init; }
    public double GeminiConfidence { get; init; }
    public double LocalAnalysisDurationMs { get; init; }
    public double? PeakMemoryMb { get; init; }
}
