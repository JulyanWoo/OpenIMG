using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenCvSharp;
using OpenIMG.Application.Abstractions.Analysis;
using OpenIMG.Application.Analysis;
using OpenIMG.Application.Configuration;
using OpenIMG.Core.Enums;
using OpenIMG.Core.Models;
using OpenIMG.Infrastructure.Analysis;

namespace OpenIMG.Tests;

[Trait("Category", TestCategories.Integration)]
public class LocalAnalysisTests
{
    [Fact]
    public void NormalizedRectangle_IoU_CalculatesCorrectly()
    {
        var boxA = new NormalizedRectangle(0.1, 0.1, 0.4, 0.4);
        var boxB = new NormalizedRectangle(0.1, 0.1, 0.4, 0.4);
        Assert.Equal(1.0, boxA.CalculateIoU(boxB), precision: 4);

        var boxDisjoint = new NormalizedRectangle(0.6, 0.6, 0.3, 0.3);
        Assert.Equal(0.0, boxA.CalculateIoU(boxDisjoint), precision: 4);

        // 50% overlap horizontally
        var boxOverlap = new NormalizedRectangle(0.3, 0.1, 0.4, 0.4);
        // Intersection: [0.3, 0.1] to [0.5, 0.5] -> 0.2 * 0.4 = 0.08
        // AreaA = 0.16, AreaOverlap = 0.16, Union = 0.16 + 0.16 - 0.08 = 0.24
        // IoU = 0.08 / 0.24 = 0.3333...
        Assert.Equal(0.3333, boxA.CalculateIoU(boxOverlap), precision: 3);
    }

    [Fact]
    public void NormalizedRectangle_Clamp_KeepsWithinUnitSquare()
    {
        var outOfBounds = new NormalizedRectangle(-0.2, -0.1, 1.5, 1.3);
        var clamped = outOfBounds.Clamp();

        Assert.Equal(0.0, clamped.X);
        Assert.Equal(0.0, clamped.Y);
        Assert.True(clamped.Right <= 1.0);
        Assert.True(clamped.Bottom <= 1.0);
    }

    [Fact]
    public void OpenCvStructuralRegionDetector_DetectsRectanglesAndDivisions()
    {
        var detector = new OpenCvStructuralRegionDetector();

        // Create a 600x800 white image with a dark cabinet frame and shelf
        using var mat = new Mat(800, 600, MatType.CV_8UC3, new Scalar(240, 240, 240));

        // Draw outer carcase
        Cv2.Rectangle(mat, new Rect(50, 100, 500, 600), new Scalar(40, 40, 40), thickness: 4);
        // Draw horizontal shelf line
        Cv2.Line(mat, new Point(50, 400), new Point(550, 400), new Scalar(40, 40, 40), thickness: 4);
        // Draw vertical division line
        Cv2.Line(mat, new Point(300, 400), new Point(300, 700), new Scalar(40, 40, 40), thickness: 4);

        var regions = detector.DetectStructuralRegions(mat);

        Assert.NotEmpty(regions);
        Assert.Contains(regions, r => r.RegionType == StructuralRegionType.HorizontalDivision ||
                                      r.RegionType == StructuralRegionType.ShelfCandidate ||
                                      r.RegionType == StructuralRegionType.StructuralPanelCandidate ||
                                      r.RegionType == StructuralRegionType.LargeFurnitureRegion);

        foreach (var r in regions)
        {
            Assert.True(r.Confidence >= 0.0 && r.Confidence <= 1.0);
            Assert.True(r.Region.Area > 0.0);
        }
    }

    [Fact]
    public void OpenCvColorRegionAnalyzer_IdentifiesDominantAndAccent()
    {
        var analyzer = new OpenCvColorRegionAnalyzer();

        // Create an image: 85% white, 15% bright blue accent
        using var mat = new Mat(400, 400, MatType.CV_8UC3, new Scalar(250, 250, 250));
        // Blue accent patch: BGR = (220, 50, 20)
        Cv2.Rectangle(mat, new Rect(150, 150, 100, 100), new Scalar(220, 50, 20), -1);

        var colorRegions = analyzer.AnalyzeColorRegions(mat);

        Assert.NotEmpty(colorRegions);
        var dominant = colorRegions.FirstOrDefault(c => c.IsDominant);
        Assert.NotNull(dominant);
        Assert.True(dominant.AreaPercentage >= 0.50);

        var accent = colorRegions.FirstOrDefault(c => c.IsAccentCandidate);
        Assert.NotNull(accent);
        Assert.True(accent.AreaPercentage > 0.02 && accent.AreaPercentage < 0.30);
    }

    [Fact]
    public void OpenCvHardwareCandidateDetector_IdentifiesHighContrastFeatures()
    {
        var detector = new OpenCvHardwareCandidateDetector();

        // Light background with a dark elongated handle
        using var mat = new Mat(500, 500, MatType.CV_8UC3, new Scalar(230, 230, 230));
        // Draw vertical metallic handle
        Cv2.Rectangle(mat, new Rect(240, 180, 15, 120), new Scalar(20, 20, 20), -1);

        var candidates = detector.DetectHardwareCandidates(mat);

        Assert.NotEmpty(candidates);
        var handle = candidates.FirstOrDefault(c => c.Region.X > 0.40 && c.Region.Right < 0.60);
        Assert.NotNull(handle);
        Assert.True(handle.Confidence > 0.40);
        Assert.True(handle.ContrastScore > 0.20);
    }

    [Fact]
    public void LocalFurnitureContract_RoundTrip_Serialization()
    {
        var contract = new LocalFurnitureContract
        {
            SourceFileName = "test_image.jpg",
            SourceSha256 = "ABCD1234EF5678",
            Status = LocalAnalysisStatus.Success,
            FurnitureBounds = new NormalizedRectangle(0.1, 0.15, 0.8, 0.7),
            FurnitureConfidence = 0.88,
            SceneComplexity = 0.35,
            LocalConfidence = 0.82,
            ConfidenceBreakdown = new LocalConfidenceBreakdown
            {
                ObjectDetectionConfidence = 0.85,
                StructuralCoverageConfidence = 0.80,
                RegionConsistencyConfidence = 0.75,
                ColorSegmentationConfidence = 0.88,
                FurnitureLocalizationConfidence = 0.88,
                HardwareDetectionConfidence = 0.70
            },
            StructuralRegions = new List<LocalStructuralRegion>
            {
                new()
                {
                    Id = "struct_1",
                    Region = new NormalizedRectangle(0.1, 0.15, 0.8, 0.7),
                    RegionType = StructuralRegionType.LargeFurnitureRegion,
                    Confidence = 0.85
                }
            },
            HardwareCandidates = new List<HardwareCandidate>
            {
                new()
                {
                    Id = "hw_1",
                    Region = new NormalizedRectangle(0.48, 0.45, 0.04, 0.10),
                    GeometryType = "small_elongated_segment",
                    Confidence = 0.75,
                    Orientation = "vertical",
                    ContrastScore = 0.82
                }
            },
            ColorRegions = new List<LocalColorRegion>
            {
                new()
                {
                    Region = new NormalizedRectangle(0.1, 0.15, 0.8, 0.7),
                    DominantLab = new LabColor(92.5, -0.5, 2.1),
                    RepresentativeRgb = "#ECECEC",
                    AreaPercentage = 0.82,
                    IsDominant = true,
                    Confidence = 0.90
                }
            },
            MaterialHints = new List<LocalMaterialHint>
            {
                new()
                {
                    Region = new NormalizedRectangle(0.1, 0.15, 0.8, 0.7),
                    Material = MaterialHintType.UniformMelamineLike,
                    Confidence = 0.45,
                    Rationale = "Uniform planar reflectance"
                }
            },
            ModelInfo = new LocalModelInfo
            {
                ModelName = "yolo11n",
                ModelVersion = "8.3.0",
                ExecutionProvider = "CPUExecutionProvider",
                Precision = "FP32"
            },
            AnalysisDuration = TimeSpan.FromMilliseconds(245)
        };

        var json = JsonSerializer.Serialize(contract, new JsonSerializerOptions { WriteIndented = true });
        var deserialized = JsonSerializer.Deserialize<LocalFurnitureContract>(json);

        Assert.NotNull(deserialized);
        Assert.Equal(contract.SourceFileName, deserialized.SourceFileName);
        Assert.Equal(contract.SourceSha256, deserialized.SourceSha256);
        Assert.Equal(contract.Status, deserialized.Status);
        Assert.Equal(contract.FurnitureBounds, deserialized.FurnitureBounds);
        Assert.Equal(contract.LocalConfidence, deserialized.LocalConfidence);
        Assert.Equal(contract.ConfidenceBreakdown.ObjectDetectionConfidence, deserialized.ConfidenceBreakdown.ObjectDetectionConfidence);
        Assert.Single(deserialized.StructuralRegions);
        Assert.Single(deserialized.HardwareCandidates);
        Assert.Single(deserialized.ColorRegions);
        Assert.Single(deserialized.MaterialHints);
    }

    [Fact]
    public void AnalysisComparator_GeneratesReportAndSimulatesPolicy()
    {
        var comparator = new AnalysisComparator();

        var gemini = new FurnitureAnalysis
        {
            FurnitureType = "upper cabinet",
            Confidence = 0.95,
            MainColors = new List<string> { "white" },
            AccentColors = new List<string> { "silver" },
            Components = new List<FurnitureComponent>
            {
                new()
                {
                    Id = "door",
                    Type = "door",
                    Description = "White cabinet door",
                    Region = new ProtectedRegion { X = 0.2, Y = 0.2, Width = 0.6, Height = 0.6 }
                },
                new()
                {
                    Id = "handle",
                    Type = "hardware",
                    Description = "Silver handle",
                    Region = new ProtectedRegion { X = 0.48, Y = 0.45, Width = 0.04, Height = 0.10 }
                }
            }
        };

        var local = new LocalFurnitureContract
        {
            SourceFileName = "test.jpg",
            LocalConfidence = 0.80,
            FurnitureBounds = new NormalizedRectangle(0.18, 0.18, 0.64, 0.64),
            FurnitureConfidence = 0.85,
            SceneComplexity = 0.25,
            StructuralRegions = new List<LocalStructuralRegion>
            {
                new()
                {
                    Id = "s1",
                    Region = new NormalizedRectangle(0.2, 0.2, 0.6, 0.6),
                    RegionType = StructuralRegionType.DoorLikeRegion,
                    Confidence = 0.70
                }
            },
            HardwareCandidates = new List<HardwareCandidate>
            {
                new()
                {
                    Id = "h1",
                    Region = new NormalizedRectangle(0.48, 0.45, 0.04, 0.10),
                    Confidence = 0.75
                }
            },
            ColorRegions = new List<LocalColorRegion>
            {
                new()
                {
                    Region = new NormalizedRectangle(0.2, 0.2, 0.6, 0.6),
                    DominantLab = new LabColor(90.0, 0.0, 0.0),
                    AreaPercentage = 0.80,
                    IsDominant = true
                }
            }
        };

        var report = comparator.Compare("CAL01", "test.jpg", "HASH123", gemini, local);

        Assert.NotNull(report);
        Assert.True(report.FurnitureRegionOverlap > 0.80);
        Assert.True(report.StructuralRegionCoverage >= 0.90);
        Assert.True(report.HardwareRegionOverlap >= 0.90);
        Assert.True(report.DominantColorAgreement > 0.80);
        Assert.False(report.GeminiFallbackWouldHaveBeenRequired);
    }
}
