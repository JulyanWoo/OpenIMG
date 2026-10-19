namespace OpenIMG.Core.Models;

public record LocalConfidenceBreakdown
{
    public double ObjectDetectionConfidence { get; init; }
    public double StructuralCoverageConfidence { get; init; }
    public double RegionConsistencyConfidence { get; init; }
    public double ColorSegmentationConfidence { get; init; }
    public double FurnitureLocalizationConfidence { get; init; }
    public double HardwareDetectionConfidence { get; init; }

    public double CalculateCompositeScore()
    {
        // Interpretable weighted average across structural, color, localization and hardware signals
        // FurnitureLocalization (0.30) + StructuralCoverage (0.25) + RegionConsistency (0.15)
        // + ColorSegmentation (0.15) + ObjectDetection (0.10) + HardwareDetection (0.05)
        var composite =
            (FurnitureLocalizationConfidence * 0.30) +
            (StructuralCoverageConfidence * 0.25) +
            (RegionConsistencyConfidence * 0.15) +
            (ColorSegmentationConfidence * 0.15) +
            (ObjectDetectionConfidence * 0.10) +
            (HardwareDetectionConfidence * 0.05);

        return Math.Round(Math.Clamp(composite, 0.0, 1.0), 4);
    }
}
