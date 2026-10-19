using System;
using OpenCvSharp;
using OpenIMG.Core.Enums;
using OpenIMG.Core.Models;

namespace OpenIMG.Application.Analysis;

public static class OpenCvQualityAnalyzer
{
    public static LocalImageQualityAssessment Analyze(string imagePath)
    {
        using var mat = Cv2.ImRead(imagePath);
        if (mat.Empty())
        {
            throw new ArgumentException($"Cannot read image for quality assessment at: {imagePath}");
        }

        return Analyze(mat);
    }

    public static LocalImageQualityAssessment AnalyzeQuality(Mat mat) => Analyze(mat);

    public static LocalImageQualityAssessment Analyze(Mat mat)
    {
        using var gray = new Mat();
        if (mat.Channels() == 3)
        {
            Cv2.CvtColor(mat, gray, ColorConversionCodes.BGR2GRAY);
        }
        else
        {
            mat.CopyTo(gray);
        }

        // 1. Sharpness (Laplacian Variance)
        using var lap = new Mat();
        Cv2.Laplacian(gray, lap, MatType.CV_64F);
        Cv2.MeanStdDev(lap, out _, out var lapStd);
        double sharpnessScore = Math.Round(lapStd.Val0 * lapStd.Val0, 2);
        string sharpnessLevel = sharpnessScore switch
        {
            < 180.0 => "VeryLow",
            < 500.0 => "Low",
            < 1400.0 => "Moderate",
            _ => "Good"
        };

        // 2. Contrast (Standard deviation of luminance)
        Cv2.MeanStdDev(gray, out var grayMean, out var grayStd);
        double contrastScore = Math.Round(grayStd.Val0, 2);
        string contrastLevel = contrastScore switch
        {
            < 45.0 => "Low",
            < 68.0 => "Moderate",
            _ => "Good"
        };

        // 3. Exposure
        double exposureMean = Math.Round(grayMean.Val0, 2);
        string exposureIssue = exposureMean switch
        {
            < 75.0 => "Underexposed",
            > 180.0 => "Overexposed",
            _ => "None"
        };

        // 4. Haze Metric (Dark Channel Prior estimation)
        double hazeScore = CalculateDarkChannelVeil(mat);
        string hazeLevel = hazeScore switch
        {
            < 20.0 => "None",
            < 35.0 => "Low",
            < 55.0 => "Moderate",
            _ => "High"
        };

        // 5. Color Cast (CIELAB distance from neutral 128,128)
        double colorCastDelta = 0.0;
        bool colorCastDetected = false;
        if (mat.Channels() == 3)
        {
            using var lab = new Mat();
            Cv2.CvtColor(mat, lab, ColorConversionCodes.BGR2Lab);
            Cv2.MeanStdDev(lab, out var labMean, out _);
            double dLabA = labMean.Val1 - 128.0;
            double dLabB = labMean.Val2 - 128.0;
            colorCastDelta = Math.Round(Math.Sqrt(dLabA * dLabA + dLabB * dLabB), 2);
            colorCastDetected = colorCastDelta > 14.0;
        }

        // 6. Dynamic Range (1st to 99th percentile luminance span)
        double dynamicRangeSpan = Math.Round(CalculateDynamicRange(gray), 2);
        string dynamicRangeLevel = dynamicRangeSpan switch
        {
            < 160.0 => "Low",
            < 220.0 => "Moderate",
            _ => "High"
        };

        // 7. Noise Estimate (Median blur high-frequency residual std dev)
        using var med = new Mat();
        using var noiseRes = new Mat();
        Cv2.MedianBlur(gray, med, 3);
        Cv2.Absdiff(gray, med, noiseRes);
        Cv2.MeanStdDev(noiseRes, out _, out var noiseStd);
        double noiseScore = Math.Round(noiseStd.Val0, 2);
        string noiseLevel = noiseScore switch
        {
            < 2.5 => "Low",
            < 5.5 => "Moderate",
            _ => "High"
        };

        return new LocalImageQualityAssessment
        {
            SharpnessScore = sharpnessScore,
            SharpnessLevel = sharpnessLevel,
            ContrastScore = contrastScore,
            ContrastLevel = contrastLevel,
            ExposureMean = exposureMean,
            ExposureIssue = exposureIssue,
            HazeScore = hazeScore,
            HazeLevel = hazeLevel,
            ColorCastDelta = colorCastDelta,
            ColorCastDetected = colorCastDetected,
            DynamicRangeSpan = dynamicRangeSpan,
            DynamicRangeLevel = dynamicRangeLevel,
            NoiseScore = noiseScore,
            NoiseLevel = noiseLevel
        };
    }

    public static double CalculateDarkChannelVeil(Mat bgrMat)
    {
        if (bgrMat.Channels() < 3) return 0.0;

        Cv2.Split(bgrMat, out var channels);
        using var min1 = new Mat();
        using var darkCh = new Mat();
        Cv2.Min(channels[0], channels[1], min1);
        Cv2.Min(min1, channels[2], darkCh);

        using var element = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(15, 15));
        using var darkEroded = new Mat();
        Cv2.Erode(darkCh, darkEroded, element);

        Cv2.MeanStdDev(darkEroded, out var mean, out _);
        foreach (var ch in channels) ch.Dispose();

        return Math.Round(mean.Val0, 2);
    }

    public static double CalculateDynamicRange(Mat gray)
    {
        int[] histSize = { 256 };
        Rangef[] ranges = { new(0, 256) };
        using var hist = new Mat();
        Cv2.CalcHist(new[] { gray }, new[] { 0 }, null, hist, 1, histSize, ranges);

        double totalPixels = gray.Rows * gray.Cols;
        double p1 = totalPixels * 0.01;
        double p99 = totalPixels * 0.99;

        double accum = 0;
        int lowVal = 0, highVal = 255;
        bool foundLow = false;

        for (int i = 0; i < 256; i++)
        {
            accum += hist.At<float>(i);
            if (!foundLow && accum >= p1)
            {
                lowVal = i;
                foundLow = true;
            }
            if (accum >= p99)
            {
                highVal = i;
                break;
            }
        }

        return Math.Max(0, highVal - lowVal);
    }

    public static PhotographicImprovementAssessment AssessImprovement(
        LocalImageQualityAssessment sourceQuality,
        LocalValidationReport report)
    {
        if (sourceQuality.HasMeasurableDefects)
        {
            // If source had clear defects and the candidate is nearly identical across all quality dimensions
            bool nearIdentical = report.StructuralSimilarity >= 0.995 &&
                                 Math.Abs(report.SharpnessGain) < 0.15 &&
                                 Math.Abs(report.ContrastGain) < 0.05 &&
                                 report.DynamicRangeGain < 5.0 &&
                                 report.HazeReduction < 5.0;

            if (nearIdentical)
            {
                return PhotographicImprovementAssessment.Insufficient;
            }

            // A single high-frequency metric can be inflated by sharpening or
            // noise. Perceptible restoration needs corroboration from independent
            // quality dimensions and must not materially worsen a detected defect.
            var sharpnessImproved = report.SharpnessGain >= 0.25 &&
                                    (report.NoiseEstimateBefore <= 0 || report.NoiseEstimateAfter <= report.NoiseEstimateBefore * 1.15);
            var contrastImproved = report.ContrastGain >= 0.10;
            var rangeImproved = report.DynamicRangeGain >= 10.0;
            var hazeImproved = report.HazeReduction >= 5.0;
            var colorImproved = sourceQuality.ColorCastDetected && report.ColorCastReduction >= 2.0;
            var meaningfulGains = new[] { sharpnessImproved, contrastImproved, rangeImproved, hazeImproved, colorImproved }.Count(x => x);

            var hazeRegressed = sourceQuality.HazeLevel is "Moderate" or "High" && report.HazeReduction < -2.0;
            var colorRegressed = sourceQuality.ColorCastDetected && report.ColorCastReduction < -1.0;
            var noiseRegressed = report.NoiseEstimateBefore > 0 && report.NoiseEstimateAfter > report.NoiseEstimateBefore * 1.15;

            if (hazeRegressed || colorRegressed || (meaningfulGains == 0 && noiseRegressed))
                return PhotographicImprovementAssessment.Insufficient;

            if (meaningfulGains >= 2)
                return PhotographicImprovementAssessment.Strong;

            return meaningfulGains == 1
                ? PhotographicImprovementAssessment.Moderate
                : PhotographicImprovementAssessment.Insufficient;
        }

        // Already high-quality source: small improvement does not produce false insufficiency
        if (report.SharpnessGain >= 0.25 || report.ContrastGain >= 0.15)
        {
            return PhotographicImprovementAssessment.Strong;
        }

        return PhotographicImprovementAssessment.Moderate;
    }
}
