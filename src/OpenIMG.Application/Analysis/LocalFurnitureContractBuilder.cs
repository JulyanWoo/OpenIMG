using OpenCvSharp;
using OpenIMG.Application.Abstractions.Analysis;
using OpenIMG.Core.Enums;
using OpenIMG.Core.Models;

namespace OpenIMG.Application.Analysis;

public class LocalFurnitureContractBuilder : ILocalContractBuilder
{
    private readonly ILocalContractConflictDetector _conflictDetector;

    public LocalFurnitureContractBuilder()
        : this(new LocalContractConflictDetector())
    {
    }

    public LocalFurnitureContractBuilder(ILocalContractConflictDetector conflictDetector)
    {
        _conflictDetector = conflictDetector ?? throw new ArgumentNullException(nameof(conflictDetector));
    }

    public LocalFurnitureContract BuildContract(
        string sourceFileName,
        string sourceSha256,
        Mat normalizedImage,
        List<LocalDetectedObject> detectedObjects,
        List<LocalStructuralRegion> structuralRegions,
        List<HardwareCandidate> hardwareCandidates,
        List<LocalColorRegion> colorRegions,
        TimeSpan analysisDuration,
        LocalModelInfo? modelInfo,
        LocalAnalysisStatus status = LocalAnalysisStatus.Success,
        List<string>? warnings = null)
    {
        var warnList = warnings != null ? new List<string>(warnings) : new List<string>();

        // 1. Separate SceneObjects from DetectedObjects (e.g. person, chair, dog, etc.)
        var sceneObjects = detectedObjects
            .Where(o => string.Equals(o.Source, "OnnxDetector", StringComparison.OrdinalIgnoreCase))
            .ToList();

        // 2. Derive Material Hints from classical image features in key regions
        var materialHints = ExtractMaterialHints(normalizedImage, structuralRegions, colorRegions);

        // 3. Furniture Region Estimation
        var (furnitureCandidates, furnitureBounds, furnitureConfidence) = EstimateFurnitureRegions(
            structuralRegions,
            colorRegions,
            detectedObjects,
            normalizedImage.Width,
            normalizedImage.Height);

        // 4. Calculate Scene Complexity
        var sceneComplexity = CalculateSceneComplexity(
            detectedObjects.Count,
            structuralRegions.Count,
            hardwareCandidates.Count,
            colorRegions.Count);

        // 5. Calculate Confidence Breakdown & Composite LocalConfidence
        var breakdown = CalculateConfidenceBreakdown(
            detectedObjects,
            structuralRegions,
            hardwareCandidates,
            colorRegions,
            furnitureBounds,
            furnitureConfidence,
            status);

        var localConfidence = breakdown.CalculateCompositeScore();

        var envelopeAuthority = ConstraintAuthority.Strict;
        if (furnitureBounds == null)
        {
            envelopeAuthority = ConstraintAuthority.Uncertain;
            warnList.Add("Furniture region could not be isolated reliably.");
        }
        else
        {
            var bestCand = furnitureCandidates.FirstOrDefault(c => c.Region == furnitureBounds);
            if (bestCand != null && bestCand.LowCoverage)
            {
                envelopeAuthority = ConstraintAuthority.Uncertain;
                warnList.Add("Low coverage furniture envelope detected: downgraded to Uncertain.");
            }
            else if (furnitureConfidence < 0.70)
            {
                envelopeAuthority = ConstraintAuthority.Advisory;
            }
        }

        var initialContract = new LocalFurnitureContract
        {
            SourceFileName = sourceFileName,
            SourceSha256 = sourceSha256,
            Status = status,
            DetectedObjects = detectedObjects,
            StructuralRegions = structuralRegions,
            HardwareCandidates = hardwareCandidates,
            ColorRegions = colorRegions,
            MaterialHints = materialHints,
            SceneObjects = sceneObjects,
            FurnitureRegionCandidates = furnitureCandidates,
            FurnitureBounds = furnitureBounds,
            FurnitureConfidence = furnitureConfidence,
            FurnitureEnvelopeAuthority = envelopeAuthority,
            SceneComplexity = sceneComplexity,
            LocalConfidence = localConfidence,
            ConfidenceBreakdown = breakdown,
            Warnings = warnList,
            ModelInfo = modelInfo,
            AnalysisDuration = analysisDuration
        };

        var report = _conflictDetector.DetectAndResolveConflicts(initialContract);

        return initialContract with
        {
            FurnitureEnvelopeAuthority = report.EnvelopeAuthority,
            AuthorityRecords = report.AuthorityRecords,
            Conflicts = report.Conflicts
        };
    }

    private static List<LocalMaterialHint> ExtractMaterialHints(
        Mat image,
        List<LocalStructuralRegion> structuralRegions,
        List<LocalColorRegion> colorRegions)
    {
        var hints = new List<LocalMaterialHint>();
        if (image.Empty() || image.Width <= 10 || image.Height <= 10)
            return hints;

        using var gray = new Mat();
        if (image.Channels() == 3)
            Cv2.CvtColor(image, gray, ColorConversionCodes.BGR2GRAY);
        else
            image.CopyTo(gray);

        // Sample up to 5 representative regions for conservative classical hints
        var sampleRegions = structuralRegions
            .Where(r => r.Region.Area > 0.04)
            .OrderByDescending(r => r.Region.Area)
            .Take(4)
            .Select(r => r.Region)
            .ToList();

        if (sampleRegions.Count == 0 && colorRegions.Count > 0)
        {
            sampleRegions = colorRegions
                .Where(c => c.IsDominant)
                .Select(c => c.Region)
                .Take(2)
                .ToList();
        }

        foreach (var reg in sampleRegions)
        {
            var clamped = reg.Clamp();
            var px = (int)(clamped.X * gray.Width);
            var py = (int)(clamped.Y * gray.Height);
            var pw = Math.Max(5, (int)(clamped.Width * gray.Width));
            var ph = Math.Max(5, (int)(clamped.Height * gray.Height));

            if (px + pw > gray.Width) pw = gray.Width - px;
            if (py + ph > gray.Height) ph = gray.Height - py;
            if (pw <= 5 || ph <= 5) continue;

            using var roi = new Mat(gray, new Rect(px, py, pw, ph));
            Cv2.MeanStdDev(roi, out var mean, out var stdDev);

            using var laplacian = new Mat();
            Cv2.Laplacian(roi, laplacian, MatType.CV_64F);
            Cv2.MeanStdDev(laplacian, out _, out var lapStdDev);
            var lapVar = lapStdDev.Val0 * lapStdDev.Val0;

            MaterialHintType hintType;
            double confidence;
            string rationale;

            if (lapVar > 300.0 && stdDev.Val0 > 25.0)
            {
                hintType = MaterialHintType.WoodLikeTexture;
                confidence = 0.38;
                rationale = $"High textural edge variance (LapVar={lapVar:F1}, StdDev={stdDev.Val0:F1}) indicative of woodgrain or patterned surface.";
            }
            else if (lapVar < 60.0 && stdDev.Val0 < 18.0)
            {
                hintType = MaterialHintType.UniformMelamineLike;
                confidence = 0.45;
                rationale = $"Low surface variance and high uniform planar reflectance (LapVar={lapVar:F1}, StdDev={stdDev.Val0:F1}) indicative of painted or melamine surface.";
            }
            else if (mean.Val0 > 220.0 && lapVar > 120.0)
            {
                hintType = MaterialHintType.GlassLikeReflective;
                confidence = 0.30;
                rationale = $"High brightness with specular reflection boundary cues (Mean={mean.Val0:F1}).";
            }
            else
            {
                hintType = MaterialHintType.Unknown;
                confidence = 0.20;
                rationale = "Inconclusive classical texture signature.";
            }

            hints.Add(new LocalMaterialHint
            {
                Region = clamped,
                Material = hintType,
                Confidence = confidence,
                Rationale = rationale
            });
        }

        return hints;
    }

    private static (List<FurnitureRegionCandidate> Candidates, NormalizedRectangle? Bounds, double Confidence) EstimateFurnitureRegions(
        List<LocalStructuralRegion> structuralRegions,
        List<LocalColorRegion> colorRegions,
        List<LocalDetectedObject> detectedObjects,
        int imgWidth,
        int imgHeight)
    {
        var candidates = new List<FurnitureRegionCandidate>();

        // 1. Structural bounding hypothesis
        if (structuralRegions.Count > 0)
        {
            var minX = structuralRegions.Min(r => r.Region.X);
            var minY = structuralRegions.Min(r => r.Region.Y);
            var maxX = structuralRegions.Max(r => r.Region.Right);
            var maxY = structuralRegions.Max(r => r.Region.Bottom);

            var structuralEnclosing = new NormalizedRectangle(minX, minY, maxX - minX, maxY - minY).Clamp();
            if (structuralEnclosing.Area > 0.08)
            {
                var structuralCount = structuralRegions.Count(r => structuralEnclosing.IntersectionOverSelf(r.Region) > 0.6);
                var conf = Math.Min(0.85, 0.45 + (structuralCount * 0.05));

                var isLowCoverage = structuralEnclosing.Width < 0.35 ||
                                    structuralEnclosing.Area < 0.22 ||
                                    (structuralEnclosing.X > 0.65 && structuralEnclosing.Width < 0.35) ||
                                    (structuralEnclosing.Right < 0.35 && structuralEnclosing.Width < 0.35);

                if (isLowCoverage)
                {
                    conf = Math.Min(conf, 0.45);
                }

                candidates.Add(new FurnitureRegionCandidate
                {
                    Region = structuralEnclosing,
                    Confidence = conf,
                    LowCoverage = isLowCoverage,
                    Authority = isLowCoverage ? ConstraintAuthority.Uncertain : ConstraintAuthority.Strict,
                    Evidence = isLowCoverage
                        ? $"Low-coverage structural enclosure ({structuralCount} regions, Width={structuralEnclosing.Width:F2}). Downgraded authority."
                        : $"Enclosure of {structuralCount} structural geometric regions."
                });
            }
        }

        // 2. Dominant color region hypothesis
        var dominantColor = colorRegions.FirstOrDefault(c => c.IsDominant && c.Region.Area > 0.15);
        if (dominantColor != null)
        {
            var isLowCoverage = dominantColor.Region.Width < 0.35 || dominantColor.Region.Area < 0.22;
            candidates.Add(new FurnitureRegionCandidate
            {
                Region = dominantColor.Region,
                Confidence = Math.Min(0.70, dominantColor.Confidence),
                LowCoverage = isLowCoverage,
                Authority = isLowCoverage ? ConstraintAuthority.Uncertain : ConstraintAuthority.Advisory,
                Evidence = $"Dominant coherent color surface covering {dominantColor.AreaPercentage * 100:F1}% of frame."
            });
        }

        // 3. Select best candidate as overall FurnitureBounds
        if (candidates.Count == 0)
        {
            return (candidates, null, 0.20);
        }

        var best = candidates.OrderByDescending(c => c.Confidence).First();
        return (candidates, best.Region, best.Confidence);
    }

    private static double CalculateSceneComplexity(
        int detectedCount,
        int structuralCount,
        int hardwareCount,
        int colorCount)
    {
        // Interpretable score from 0.0 (minimalist/empty) to 1.0 (highly cluttered/complex)
        var complexity = (detectedCount * 0.08) +
                         (structuralCount * 0.04) +
                         (hardwareCount * 0.03) +
                         (colorCount * 0.05);

        return Math.Round(Math.Clamp(complexity, 0.1, 1.0), 3);
    }

    private static LocalConfidenceBreakdown CalculateConfidenceBreakdown(
        List<LocalDetectedObject> detectedObjects,
        List<LocalStructuralRegion> structuralRegions,
        List<HardwareCandidate> hardwareCandidates,
        List<LocalColorRegion> colorRegions,
        NormalizedRectangle? furnitureBounds,
        double furnitureConfidence,
        LocalAnalysisStatus status)
    {
        if (status == LocalAnalysisStatus.ModelUnavailable || status == LocalAnalysisStatus.Failed)
        {
            return new LocalConfidenceBreakdown
            {
                ObjectDetectionConfidence = 0.0,
                StructuralCoverageConfidence = structuralRegions.Count > 0 ? 0.4 : 0.0,
                RegionConsistencyConfidence = 0.3,
                ColorSegmentationConfidence = colorRegions.Count > 0 ? 0.5 : 0.0,
                FurnitureLocalizationConfidence = furnitureConfidence,
                HardwareDetectionConfidence = hardwareCandidates.Count > 0 ? 0.4 : 0.0
            };
        }

        // ObjectDetectionConfidence: 0.85 baseline if cleanly executed, lower if noisy
        double objDetConf = detectedObjects.Count switch
        {
            0 => 0.75, // Empty scene is normal for isolated furniture
            <= 3 => 0.85,
            <= 7 => 0.70,
            _ => 0.55
        };

        // StructuralCoverageConfidence: depends on number and area of structural regions
        var structArea = structuralRegions.Sum(r => r.Region.Area);
        double structConf = structuralRegions.Count switch
        {
            0 => 0.15,
            1 => 0.40,
            <= 4 => 0.65,
            <= 10 => 0.82,
            _ => 0.75 // Too many fragmented regions may be texture noise
        };

        // RegionConsistencyConfidence: are divisions orthogonal and within bounds?
        double consistencyConf = 0.60;
        if (structuralRegions.Any(r => r.RegionType == StructuralRegionType.HorizontalDivision || r.RegionType == StructuralRegionType.VerticalDivision))
        {
            consistencyConf += 0.20;
        }
        if (furnitureBounds != null)
        {
            consistencyConf += 0.10;
        }

        // ColorSegmentationConfidence: dominant color clarity
        double colorConf = 0.50;
        var dominant = colorRegions.FirstOrDefault(c => c.IsDominant);
        if (dominant != null && dominant.AreaPercentage >= 0.25)
        {
            colorConf = Math.Min(0.90, 0.60 + dominant.Confidence * 0.3);
        }

        // FurnitureLocalizationConfidence: based on presence and confidence of FurnitureBounds
        double furnLocConf = furnitureBounds != null ? furnitureConfidence : 0.10;

        // HardwareDetectionConfidence
        double hwConf = hardwareCandidates.Count switch
        {
            0 => 0.45,
            <= 4 => 0.75,
            <= 8 => 0.80,
            _ => 0.50 // Too many indicates noisy edge fragments
        };

        return new LocalConfidenceBreakdown
        {
            ObjectDetectionConfidence = Math.Round(objDetConf, 3),
            StructuralCoverageConfidence = Math.Round(structConf, 3),
            RegionConsistencyConfidence = Math.Round(Math.Clamp(consistencyConf, 0.0, 1.0), 3),
            ColorSegmentationConfidence = Math.Round(colorConf, 3),
            FurnitureLocalizationConfidence = Math.Round(furnLocConf, 3),
            HardwareDetectionConfidence = Math.Round(hwConf, 3)
        };
    }
}
