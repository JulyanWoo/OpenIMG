using System.IO;
using OpenCvSharp;
using OpenIMG.Application.Configuration;
using OpenIMG.Application.Validation;
using OpenIMG.Core.Enums;
using OpenIMG.Core.Models;
using OpenIMG.Infrastructure.ImageProcessing;
using Xunit;

namespace OpenIMG.Tests;

[Trait("Category", TestCategories.Integration)]
public class OpenCvValidatorTests : IDisposable
{
    private readonly string _tempDir;
    private readonly OpenCvImageValidator _validator;
    private readonly ValidationDecisionEngine _decisionEngine;
    private readonly OpenImgOptions _options;

    public OpenCvValidatorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"OpenIMG_CvTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _options = new OpenImgOptions
        {
            MinStructuralSimilarity = 0.70,
            MinEdgeSimilarity = 0.65,
            EdgeMatchTolerancePixels = 3.0,
            MinSilhouetteSimilarity = 0.75,
            MaxAllowedGlobalColorDifference = 0.35,
            MaxAllowedNormalizedColorDistributionDifference = 0.30,
            MaxAllowedChangedAreaPercentage = 0.20,
            MaxAllowedLargestChangedRegionPercentage = 0.08,
            MaxAllowedProtectedAreaChangedPercentage = 0.05,
            MinImageRegistrationConfidence = 0.50,
            MaxAspectRatioDifference = 0.05
        };
        _validator = new OpenCvImageValidator(_options);
        _decisionEngine = new ValidationDecisionEngine(_options);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch { }
    }

    private Mat CreateBaseCabinetImage()
    {
        // 500x500 white background
        var mat = new Mat(500, 500, MatType.CV_8UC3, new Scalar(255, 255, 255));
        // Cabinet outer carcase (dark frame, filled with medium carcase tone)
        Cv2.Rectangle(mat, new Rect(50, 50, 400, 400), new Scalar(140, 150, 160), -1);
        Cv2.Rectangle(mat, new Rect(50, 50, 400, 400), new Scalar(40, 40, 40), 4);
        // Left door
        Cv2.Rectangle(mat, new Rect(70, 70, 160, 360), new Scalar(180, 190, 200), -1);
        Cv2.Rectangle(mat, new Rect(70, 70, 160, 360), new Scalar(40, 40, 40), 4);
        // Right door
        Cv2.Rectangle(mat, new Rect(270, 70, 160, 360), new Scalar(180, 190, 200), -1);
        Cv2.Rectangle(mat, new Rect(270, 70, 160, 360), new Scalar(40, 40, 40), 4);
        // Handles
        Cv2.Circle(mat, new Point(210, 250), 6, new Scalar(20, 20, 20), -1);
        Cv2.Circle(mat, new Point(290, 250), 6, new Scalar(20, 20, 20), -1);
        return mat;
    }

    private FurnitureAnalysis CreateCabinetAnalysis()
    {
        return new FurnitureAnalysis
        {
            FurnitureType = "cabinet",
            GeometryDescription = "Two-door wall cabinet",
            Components = new List<FurnitureComponent>
            {
                new()
                {
                    Id = "left_door",
                    Type = "door",
                    Color = "white",
                    Material = "melamine",
                    IsStructural = true,
                    Region = new ProtectedRegion { X = 0.14, Y = 0.14, Width = 0.32, Height = 0.72, RegionType = "door" }
                },
                new()
                {
                    Id = "right_door",
                    Type = "door",
                    Color = "white",
                    Material = "melamine",
                    IsStructural = true,
                    Region = new ProtectedRegion { X = 0.54, Y = 0.14, Width = 0.32, Height = 0.72, RegionType = "door" }
                }
            },
            ProtectedRegions = new List<ProtectedRegion>()
        };
    }

    // 1. Identical image -> very high structural agreement
    [Fact]
    public async Task ValidateAsync_1_IdenticalImage_HighStructuralAgreement()
    {
        var img1 = Path.Combine(_tempDir, "base.png");
        var img2 = Path.Combine(_tempDir, "identical.png");
        using var mat = CreateBaseCabinetImage();
        Cv2.ImWrite(img1, mat);
        Cv2.ImWrite(img2, mat);

        var report = await _validator.ValidateAsync(img1, img2, CreateCabinetAnalysis(), _tempDir, "test1");
        var decision = _decisionEngine.EvaluateLocalReport(report, out _);

        Assert.True(report.Passed);
        Assert.True(report.EdgeF1 > 0.95, $"Edge F1 was {report.EdgeF1}");
        Assert.True(report.StructuralSimilarity > 0.95, $"SSIM was {report.StructuralSimilarity}");
        Assert.True(report.SilhouetteSimilarity > 0.95, $"Silhouette was {report.SilhouetteSimilarity}");
        Assert.Equal(ValidationDecision.Accepted, decision);
    }

    // 2. Exposure-only change -> not structural failure
    [Fact]
    public async Task ValidateAsync_2_ExposureOnlyChange_NotStructuralFailure()
    {
        var img1 = Path.Combine(_tempDir, "orig_exp.png");
        var img2 = Path.Combine(_tempDir, "rest_exp.png");
        using var mat1 = CreateBaseCabinetImage();
        Cv2.ImWrite(img1, mat1);

        using var mat2 = new Mat();
        mat1.ConvertTo(mat2, -1, 1.15, 10); // Brighten exposure
        Cv2.ImWrite(img2, mat2);

        var report = await _validator.ValidateAsync(img1, img2, CreateCabinetAnalysis(), _tempDir, "test2");

        // Edge structure and silhouette must survive exposure change
        Assert.True(report.EdgeF1 >= _options.MinEdgeSimilarity, $"Edge F1 was {report.EdgeF1}");
        Assert.True(report.StructuralSimilarity >= 0.70, $"SSIM was {report.StructuralSimilarity}");
        Assert.True(report.SilhouetteSimilarity >= 0.80, $"Silhouette was {report.SilhouetteSimilarity}");
    }

    // 3. White-balance-only change -> not structural failure
    [Fact]
    public async Task ValidateAsync_3_WhiteBalanceOnlyChange_NotStructuralFailure()
    {
        var img1 = Path.Combine(_tempDir, "orig_wb.png");
        var img2 = Path.Combine(_tempDir, "rest_wb.png");
        using var mat1 = CreateBaseCabinetImage();
        Cv2.ImWrite(img1, mat1);

        using var mat2 = mat1.Clone();
        var channels = Cv2.Split(mat2);
        channels[0] *= 0.92; // Slightly cooler blue
        channels[2] *= 1.08; // Slightly warmer red
        Cv2.Merge(channels, mat2);
        Cv2.ImWrite(img2, mat2);

        var report = await _validator.ValidateAsync(img1, img2, CreateCabinetAnalysis(), _tempDir, "test3");

        Assert.True(report.EdgeF1 >= _options.MinEdgeSimilarity, $"Edge F1 was {report.EdgeF1}");
        Assert.True(report.StructuralSimilarity >= 0.75, $"SSIM was {report.StructuralSimilarity}");
        Assert.True(report.SilhouetteSimilarity >= 0.85, $"Silhouette was {report.SilhouetteSimilarity}");
    }

    // 4. Sharpening-only change -> not structural failure
    [Fact]
    public async Task ValidateAsync_4_SharpeningOnlyChange_NotStructuralFailure()
    {
        var img1 = Path.Combine(_tempDir, "orig_sharp.png");
        var img2 = Path.Combine(_tempDir, "rest_sharp.png");
        using var mat1 = CreateBaseCabinetImage();
        Cv2.ImWrite(img1, mat1);

        using var mat2 = new Mat();
        using var kernel = new Mat(3, 3, MatType.CV_32FC1);
        kernel.Set(0, 0, 0f); kernel.Set(0, 1, -1f); kernel.Set(0, 2, 0f);
        kernel.Set(1, 0, -1f); kernel.Set(1, 1, 5f); kernel.Set(1, 2, -1f);
        kernel.Set(2, 0, 0f); kernel.Set(2, 1, -1f); kernel.Set(2, 2, 0f);
        Cv2.Filter2D(mat1, mat2, mat1.Type(), kernel);
        Cv2.ImWrite(img2, mat2);

        var report = await _validator.ValidateAsync(img1, img2, CreateCabinetAnalysis(), _tempDir, "test4");

        // Spatial tolerance prevents sharpening edge variance from failing
        Assert.True(report.EdgeF1 >= _options.MinEdgeSimilarity, $"Edge F1 was {report.EdgeF1}");
        Assert.True(report.StructuralSimilarity >= 0.70, $"SSIM was {report.StructuralSimilarity}");
    }

    // 5. Noise reduction -> not structural failure
    [Fact]
    public async Task ValidateAsync_5_NoiseReduction_NotStructuralFailure()
    {
        var img1 = Path.Combine(_tempDir, "orig_noise.png");
        var img2 = Path.Combine(_tempDir, "rest_denoised.png");
        using var mat1 = CreateBaseCabinetImage();

        // Add subtle sensor noise to original
        using var noise = new Mat(mat1.Size(), mat1.Type());
        Cv2.Randn(noise, new Scalar(0, 0, 0), new Scalar(10, 10, 10));
        using var noisyOrig = new Mat();
        Cv2.Add(mat1, noise, noisyOrig);
        Cv2.ImWrite(img1, noisyOrig);

        // Restored image is clean / denoised
        Cv2.ImWrite(img2, mat1);

        var report = await _validator.ValidateAsync(img1, img2, CreateCabinetAnalysis(), _tempDir, "test5");

        Assert.True(report.EdgeF1 >= _options.MinEdgeSimilarity, $"Edge F1 was {report.EdgeF1}");
        Assert.True(report.StructuralSimilarity >= 0.70, $"SSIM was {report.StructuralSimilarity}");
    }

    // 6. Large panel recolored -> Critical color evidence
    [Fact]
    public async Task ValidateAsync_6_LargePanelRecolored_CriticalColorEvidence()
    {
        var img1 = Path.Combine(_tempDir, "orig_color.png");
        var img2 = Path.Combine(_tempDir, "rest_color.png");
        using var mat1 = CreateBaseCabinetImage();
        Cv2.ImWrite(img1, mat1);

        using var mat2 = mat1.Clone();
        // Recolor left door to dark blue
        Cv2.Rectangle(mat2, new Rect(72, 72, 156, 356), new Scalar(180, 50, 30), -1);
        Cv2.ImWrite(img2, mat2);

        var report = await _validator.ValidateAsync(img1, img2, CreateCabinetAnalysis(), _tempDir, "test6");
        var decision = _decisionEngine.EvaluateLocalReport(report, out var evidences);

        Assert.Equal(ValidationDecision.Rejected, decision);
        Assert.Contains(evidences, e => (e.Category == ValidationEvidenceCategory.Color || e.Category == ValidationEvidenceCategory.ProtectedRegion) &&
                                        e.Severity == ValidationSeverity.Critical && !e.Passed);
    }

    // 7. Door boundary removed -> Critical structural evidence
    [Fact]
    public async Task ValidateAsync_7_DoorBoundaryRemoved_CriticalStructuralEvidence()
    {
        var img1 = Path.Combine(_tempDir, "orig_doors.png");
        var img2 = Path.Combine(_tempDir, "rest_doors.png");
        using var mat1 = CreateBaseCabinetImage();
        Cv2.ImWrite(img1, mat1);

        using var mat2 = mat1.Clone();
        // Erase center dividing seam and frame between the two doors, merging into carcase panel
        Cv2.Rectangle(mat2, new Rect(215, 66, 70, 368), new Scalar(140, 150, 160), -1);
        Cv2.ImWrite(img2, mat2);

        var report = await _validator.ValidateAsync(img1, img2, CreateCabinetAnalysis(), _tempDir, "test7");
        var decision = _decisionEngine.EvaluateLocalReport(report, out var evidences);

        Assert.Equal(ValidationDecision.Rejected, decision);
        Assert.Contains(evidences, e => (e.Category == ValidationEvidenceCategory.Structural || e.Category == ValidationEvidenceCategory.Component || e.Category == ValidationEvidenceCategory.Geometry) &&
                                        e.Severity == ValidationSeverity.Critical && !e.Passed);
    }

    // 8. New vertical panel added -> Critical structural evidence
    [Fact]
    public async Task ValidateAsync_8_NewVerticalPanelAdded_CriticalStructuralEvidence()
    {
        var img1 = Path.Combine(_tempDir, "orig_panelext.png");
        var img2 = Path.Combine(_tempDir, "rest_panelext.png");
        using var mat1 = CreateBaseCabinetImage();
        Cv2.ImWrite(img1, mat1);

        using var mat2 = mat1.Clone();
        // Draw a wide new vertical dark divider dividing the right door in half
        Cv2.Rectangle(mat2, new Rect(345, 70, 24, 360), new Scalar(40, 40, 40), -1);
        Cv2.ImWrite(img2, mat2);

        var report = await _validator.ValidateAsync(img1, img2, CreateCabinetAnalysis(), _tempDir, "test8");
        var decision = _decisionEngine.EvaluateLocalReport(report, out var evidences);

        Assert.Equal(ValidationDecision.Rejected, decision);
        Assert.Contains(evidences, e => (e.Category == ValidationEvidenceCategory.Structural || e.Category == ValidationEvidenceCategory.Component || e.Category == ValidationEvidenceCategory.Geometry) &&
                                        e.Severity == ValidationSeverity.Critical && !e.Passed);
    }

    // 9. Drawer shifted -> Critical geometry evidence
    [Fact]
    public async Task ValidateAsync_9_DrawerShifted_CriticalGeometryEvidence()
    {
        var img1 = Path.Combine(_tempDir, "orig_shift.png");
        var img2 = Path.Combine(_tempDir, "rest_shift.png");
        using var mat1 = CreateBaseCabinetImage();
        Cv2.ImWrite(img1, mat1);

        using var mat2 = mat1.Clone();
        // Shift right handle downwards by 60 pixels and modify bottom border
        Cv2.Circle(mat2, new Point(290, 250), 8, new Scalar(180, 190, 200), -1);
        Cv2.Circle(mat2, new Point(290, 310), 8, new Scalar(20, 20, 20), -1);
        Cv2.Rectangle(mat2, new Rect(270, 390, 160, 40), new Scalar(40, 40, 40), -1);
        Cv2.ImWrite(img2, mat2);

        var report = await _validator.ValidateAsync(img1, img2, CreateCabinetAnalysis(), _tempDir, "test9");
        var decision = _decisionEngine.EvaluateLocalReport(report, out var evidences);

        Assert.Equal(ValidationDecision.Rejected, decision);
    }

    // 10. Large silhouette alteration -> Critical silhouette evidence
    [Fact]
    public async Task ValidateAsync_10_LargeSilhouetteAlteration_CriticalSilhouetteEvidence()
    {
        var img1 = Path.Combine(_tempDir, "orig_sil.png");
        var img2 = Path.Combine(_tempDir, "rest_sil.png");
        using var mat1 = CreateBaseCabinetImage();
        Cv2.ImWrite(img1, mat1);

        using var mat2 = mat1.Clone();
        // Remove the top 150 pixels of the cabinet carcase completely
        Cv2.Rectangle(mat2, new Rect(50, 50, 400, 150), new Scalar(255, 255, 255), -1);
        Cv2.ImWrite(img2, mat2);

        var report = await _validator.ValidateAsync(img1, img2, CreateCabinetAnalysis(), _tempDir, "test10");
        var decision = _decisionEngine.EvaluateLocalReport(report, out var evidences);

        Assert.True(report.SilhouetteSimilarity < _options.MinSilhouetteSimilarity, $"Silhouette was {report.SilhouetteSimilarity}");
        Assert.Equal(ValidationDecision.Rejected, decision);
        Assert.Contains(evidences, e => e.Category == ValidationEvidenceCategory.Silhouette && e.Severity == ValidationSeverity.Critical && !e.Passed);
    }

    // 11. Minor 1-2 px registration offset -> structurally valid after alignment
    [Fact]
    public async Task ValidateAsync_11_MinorPixelRegistrationOffset_StructurallyValidAfterAlignment()
    {
        var img1 = Path.Combine(_tempDir, "orig_reg.png");
        var img2 = Path.Combine(_tempDir, "rest_reg.png");
        using var mat1 = CreateBaseCabinetImage();
        Cv2.ImWrite(img1, mat1);

        // Shift by 2 pixels in X and Y
        using var mat2 = new Mat();
        using var shiftMatrix = new Mat(2, 3, MatType.CV_32FC1);
        shiftMatrix.Set(0, 0, 1f); shiftMatrix.Set(0, 1, 0f); shiftMatrix.Set(0, 2, 2f);
        shiftMatrix.Set(1, 0, 0f); shiftMatrix.Set(1, 1, 1f); shiftMatrix.Set(1, 2, 2f);
        Cv2.WarpAffine(mat1, mat2, shiftMatrix, mat1.Size());
        Cv2.ImWrite(img2, mat2);

        var report = await _validator.ValidateAsync(img1, img2, CreateCabinetAnalysis(), _tempDir, "test11");

        // Registration aligns it back or distance transform tolerates 2 px offset
        Assert.True(report.EdgeF1 >= 0.80, $"Edge F1 was {report.EdgeF1}");
        Assert.True(report.StructuralSimilarity >= 0.80, $"SSIM was {report.StructuralSimilarity}");
    }

    // 12. Aggressive perspective deformation requiring homography -> NeedsReview
    [Fact]
    public async Task ValidateAsync_12_SeverePerspectiveDeformation_RequiresHomography_ForcesNeedsReview()
    {
        var img1 = Path.Combine(_tempDir, "orig_warp.png");
        var img2 = Path.Combine(_tempDir, "rest_warp.png");
        using var mat1 = CreateBaseCabinetImage();
        Cv2.ImWrite(img1, mat1);

        // Severe non-affine 4-point perspective warp
        var srcPts = new Point2f[]
        {
            new(50, 50),
            new(450, 50),
            new(450, 450),
            new(50, 450)
        };
        var dstPts = new Point2f[]
        {
            new(80, 30),
            new(430, 70),
            new(470, 420),
            new(30, 460)
        };
        using var h = Cv2.GetPerspectiveTransform(srcPts, dstPts);
        using var mat2 = new Mat();
        Cv2.WarpPerspective(mat1, mat2, h, mat1.Size());
        Cv2.ImWrite(img2, mat2);

        var report = await _validator.ValidateAsync(img1, img2, CreateCabinetAnalysis(), _tempDir, "test12");
        var decision = _decisionEngine.EvaluateLocalReport(report, out var evidences);

        // Homography or low confidence forces NeedsReview (or Rejected if critical distortion)
        // Automatic acceptance is strictly forbidden!
        Assert.NotEqual(ValidationDecision.Accepted, decision);
        if (report.RegistrationModel == RegistrationModel.Homography)
        {
            Assert.Contains(evidences, e => e.Type == "RegistrationModel" && !e.Passed);
        }
    }

    // 13. Original preserved + new hinge-like line structure added -> worse EdgePrecision and measurable UnexpectedEdgeMap cluster
    [Fact]
    public async Task ValidateAsync_13_OriginalPreserved_NewHingeAdded_ProducesUnexpectedClusterAndLowerPrecision()
    {
        var img1 = Path.Combine(_tempDir, "orig_hinge.png");
        var img2 = Path.Combine(_tempDir, "rest_hinge.png");
        using var mat1 = CreateBaseCabinetImage();
        Cv2.ImWrite(img1, mat1);

        using var mat2 = mat1.Clone();
        // Add prominent new mechanical hinges on the outer edge of the carcase
        Cv2.Rectangle(mat2, new Rect(445, 100, 16, 45), new Scalar(20, 20, 20), -1);
        Cv2.Rectangle(mat2, new Rect(445, 340, 16, 45), new Scalar(20, 20, 20), -1);
        Cv2.ImWrite(img2, mat2);

        var report = await _validator.ValidateAsync(img1, img2, CreateCabinetAnalysis(), _tempDir, "test13");

        Assert.True(report.EdgePrecision < report.EdgeRecall, $"Precision ({report.EdgePrecision}) should be lower than Recall ({report.EdgeRecall})");
        Assert.True(report.EdgeRecall > 0.95, $"Recall should remain high ({report.EdgeRecall})");
        Assert.True(report.UnexpectedEdgeClusterCount >= 1, $"Unexpected cluster count was {report.UnexpectedEdgeClusterCount}");
        Assert.True(report.LargestUnexpectedEdgeClusterPixels >= _options.MinimumUnexpectedEdgeClusterPixels, $"Largest cluster was {report.LargestUnexpectedEdgeClusterPixels}");
        Assert.True(report.UnexpectedEdgeRatio > 0.02, $"UnexpectedEdgeRatio was {report.UnexpectedEdgeRatio}");
    }

    // 14. Original preserved + new handle added -> component-level unexpected cluster and lower precision
    [Fact]
    public async Task ValidateAsync_14_OriginalPreserved_NewHandleAdded_ProducesComponentLevelUnexpectedCluster()
    {
        var img1 = Path.Combine(_tempDir, "orig_handle.png");
        var img2 = Path.Combine(_tempDir, "rest_handle.png");
        using var mat1 = CreateBaseCabinetImage();
        Cv2.ImWrite(img1, mat1);

        using var mat2 = mat1.Clone();
        // Add a second prominent handle on the right door
        Cv2.Circle(mat2, new Point(290, 150), 10, new Scalar(20, 20, 20), -1);
        Cv2.Circle(mat2, new Point(290, 150), 6, new Scalar(220, 220, 220), -1);
        Cv2.ImWrite(img2, mat2);

        var report = await _validator.ValidateAsync(img1, img2, CreateCabinetAnalysis(), _tempDir, "test14");

        Assert.True(report.EdgePrecision < report.EdgeRecall, $"Precision ({report.EdgePrecision}) should be lower than Recall ({report.EdgeRecall})");
        Assert.True(report.EdgeRecall > 0.95, $"Recall should remain high ({report.EdgeRecall})");
        var rightDoor = report.ComponentResults.FirstOrDefault(c => c.ComponentId == "right_door");
        Assert.NotNull(rightDoor);
        Assert.True(rightDoor.UnexpectedEdgeClusterCount >= 1 || rightDoor.UnexpectedEdgeRatio > 0.05,
            $"Right door unexpected cluster count was {rightDoor.UnexpectedEdgeClusterCount}, ratio was {rightDoor.UnexpectedEdgeRatio}");
    }

    // 15. Original preserved + thin new vertical panel boundary added -> reduces precision
    [Fact]
    public async Task ValidateAsync_15_OriginalPreserved_ThinNewVerticalPanelBoundaryAdded_ReducesPrecision()
    {
        var img1 = Path.Combine(_tempDir, "orig_thinpanel.png");
        var img2 = Path.Combine(_tempDir, "rest_thinpanel.png");
        using var mat1 = CreateBaseCabinetImage();
        Cv2.ImWrite(img1, mat1);

        using var mat2 = mat1.Clone();
        // Add a thin vertical panel seam dividing the left door
        Cv2.Line(mat2, new Point(150, 72), new Point(150, 428), new Scalar(30, 30, 30), 3);
        Cv2.ImWrite(img2, mat2);

        var report = await _validator.ValidateAsync(img1, img2, CreateCabinetAnalysis(), _tempDir, "test15");

        Assert.True(report.EdgePrecision < report.EdgeRecall, $"Precision ({report.EdgePrecision}) should be lower than Recall ({report.EdgeRecall})");
        Assert.True(report.EdgeRecall > 0.95, $"Recall should remain high ({report.EdgeRecall})");
        Assert.True(report.LargestUnexpectedEdgeClusterPixels >= _options.MinimumUnexpectedEdgeClusterPixels,
            $"Largest cluster pixels was {report.LargestUnexpectedEdgeClusterPixels}");
    }

    // 16. Original preserved + random distributed sharpening edges -> does not produce large coherent structural addition
    [Fact]
    public async Task ValidateAsync_16_OriginalPreserved_DistributedSharpening_DoesNotProduceLargeCoherentCluster()
    {
        var img1 = Path.Combine(_tempDir, "orig_distsharp.png");
        var img2 = Path.Combine(_tempDir, "rest_distsharp.png");
        using var mat1 = CreateBaseCabinetImage();
        Cv2.ImWrite(img1, mat1);

        using var mat2 = new Mat();
        using var kernel = new Mat(3, 3, MatType.CV_32FC1);
        kernel.Set(0, 0, 0f); kernel.Set(0, 1, -0.5f); kernel.Set(0, 2, 0f);
        kernel.Set(1, 0, -0.5f); kernel.Set(1, 1, 3.0f); kernel.Set(1, 2, -0.5f);
        kernel.Set(2, 0, 0f); kernel.Set(2, 1, -0.5f); kernel.Set(2, 2, 0f);
        Cv2.Filter2D(mat1, mat2, mat1.Type(), kernel);
        Cv2.ImWrite(img2, mat2);

        var report = await _validator.ValidateAsync(img1, img2, CreateCabinetAnalysis(), _tempDir, "test16");

        // Coarse unexpected clusters should be 0 because sharpening is distributed, not a coherent structural block
        Assert.Equal(0, report.UnexpectedEdgeClusterCount);
        Assert.True(report.LargestUnexpectedEdgeClusterPixels < _options.MinimumUnexpectedEdgeClusterPixels);
    }

    // 17. Original preserved + localized coherent structural edge cluster -> detected
    [Fact]
    public async Task ValidateAsync_17_OriginalPreserved_LocalizedCoherentStructuralEdgeCluster_Detected()
    {
        var img1 = Path.Combine(_tempDir, "orig_bracket.png");
        var img2 = Path.Combine(_tempDir, "rest_bracket.png");
        using var mat1 = CreateBaseCabinetImage();
        Cv2.ImWrite(img1, mat1);

        using var mat2 = mat1.Clone();
        // Add a coherent bracket / mechanical mount inside the left door
        Cv2.Rectangle(mat2, new Rect(100, 150, 40, 40), new Scalar(30, 30, 30), 4);
        Cv2.Line(mat2, new Point(100, 150), new Point(140, 190), new Scalar(30, 30, 30), 4);
        Cv2.ImWrite(img2, mat2);

        var report = await _validator.ValidateAsync(img1, img2, CreateCabinetAnalysis(), _tempDir, "test17");

        Assert.True(report.UnexpectedEdgeClusterCount >= 1, $"Unexpected cluster count was {report.UnexpectedEdgeClusterCount}");
        Assert.True(report.LargestUnexpectedEdgeClusterPixels >= 25, $"Largest cluster pixels was {report.LargestUnexpectedEdgeClusterPixels}");
        Assert.True(report.LargestUnexpectedEdgeClusterRatio > 0.01, $"Largest cluster ratio was {report.LargestUnexpectedEdgeClusterRatio}");
    }

    // 18. Missing original panel -> reduces EdgeRecall (asymmetric detection)
    [Fact]
    public async Task ValidateAsync_18_MissingOriginalPanel_ReducesRecallNotPrecision()
    {
        var img1 = Path.Combine(_tempDir, "orig_missingpanel.png");
        var img2 = Path.Combine(_tempDir, "rest_missingpanel.png");
        using var mat1 = CreateBaseCabinetImage();
        Cv2.ImWrite(img1, mat1);

        using var mat2 = mat1.Clone();
        // Erase left door completely by blending into carcase interior (removes door edges seamlessly)
        Cv2.Rectangle(mat2, new Rect(65, 65, 175, 375), new Scalar(140, 150, 160), -1);
        Cv2.ImWrite(img2, mat2);

        var report = await _validator.ValidateAsync(img1, img2, CreateCabinetAnalysis(), _tempDir, "test18");

        // EdgeRecall drops drastically while EdgePrecision remains substantially higher
        Assert.True(report.EdgeRecall < 0.80, $"EdgeRecall was {report.EdgeRecall:F3}");
        Assert.True(report.EdgePrecision > report.EdgeRecall,
            $"EdgePrecision ({report.EdgePrecision:F3}) should be greater than EdgeRecall ({report.EdgeRecall:F3}) when original structure is removed");
    }
}
