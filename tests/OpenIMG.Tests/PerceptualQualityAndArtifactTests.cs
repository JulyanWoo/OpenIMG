using OpenCvSharp;
using OpenIMG.Application.Analysis;
using OpenIMG.Core.Enums;
using OpenIMG.Core.Models;
using OpenIMG.Infrastructure.ImageProcessing;

namespace OpenIMG.Tests;

[Trait("Category", TestCategories.Integration)]
public class PerceptualQualityAndArtifactTests
{
    [Fact]
    public void SharpeningWithNoiseAndWorseHaze_IsInsufficient()
    {
        var source = new LocalImageQualityAssessment
        {
            SharpnessLevel = "VeryLow",
            ContrastLevel = "Low",
            HazeLevel = "High",
            HazeScore = 90
        };
        var report = new LocalValidationReport
        {
            SharpnessGain = 0.52,
            ContrastGain = 0.05,
            DynamicRangeGain = 7,
            HazeReduction = -6,
            NoiseEstimateBefore = 0.65,
            NoiseEstimateAfter = 0.79
        };

        Assert.Equal(PhotographicImprovementAssessment.Insufficient,
            OpenIMG.Application.Analysis.OpenCvQualityAnalyzer.AssessImprovement(source, report));
    }

    [Fact]
    public void MultiAxisImprovementWithoutRegression_IsStrong()
    {
        var source = new LocalImageQualityAssessment
        {
            SharpnessLevel = "VeryLow",
            ContrastLevel = "Low",
            HazeLevel = "High",
            HazeScore = 90
        };
        var report = new LocalValidationReport
        {
            SharpnessGain = 0.35,
            ContrastGain = 0.14,
            DynamicRangeGain = 14,
            HazeReduction = 12,
            NoiseEstimateBefore = 1.0,
            NoiseEstimateAfter = 1.05
        };

        Assert.Equal(PhotographicImprovementAssessment.Strong,
            OpenIMG.Application.Analysis.OpenCvQualityAnalyzer.AssessImprovement(source, report));
    }

    [Fact]
    public void CopiedRawTileAfterGlobalCorrection_IsCriticalArtifact()
    {
        using var original = new Mat(420, 420, MatType.CV_8UC3, new Scalar(80, 100, 120));
        Cv2.Rectangle(original, new Rect(30, 40, 120, 80), new Scalar(70, 90, 110), -1);
        using var restored = new Mat();
        original.ConvertTo(restored, -1, 1.15, 12);

        // Simulates the defective old safety operation: a raw source tile pasted
        // over an otherwise globally corrected photograph.
        using (var sourceTile = new Mat(original, new Rect(180, 170, 110, 130)))
        using (var targetTile = new Mat(restored, new Rect(180, 170, 110, 130)))
            sourceTile.CopyTo(targetTile);

        var evidence = OpenCvVisualArtifactValidator.Validate(original, restored);

        Assert.Contains(evidence, e => e.Type == "ArtificialRectangularArtifact" && e.Severity == ValidationSeverity.Critical);
    }

    [Fact]
    public void SmoothGlobalExposureCorrection_IsNotArtifact()
    {
        using var original = new Mat(420, 420, MatType.CV_8UC3, new Scalar(80, 100, 120));
        Cv2.Circle(original, new Point(160, 210), 70, new Scalar(40, 60, 80), -1);
        using var restored = new Mat();
        original.ConvertTo(restored, -1, 1.12, 8);

        var evidence = OpenCvVisualArtifactValidator.Validate(original, restored);

        Assert.DoesNotContain(evidence, e => e.Type == "ArtificialRectangularArtifact");
    }
}
