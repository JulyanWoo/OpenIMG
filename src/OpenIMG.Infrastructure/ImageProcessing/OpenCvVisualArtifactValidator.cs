using OpenCvSharp;
using OpenIMG.Core.Enums;
using OpenIMG.Core.Models;

namespace OpenIMG.Infrastructure.ImageProcessing;

/// <summary>
/// Detects newly introduced, axis-aligned photographic blocks. It works on the
/// residual between the source and registered candidate, so smooth global exposure
/// or white-balance correction is not mistaken for an artifact.
/// </summary>
public static class OpenCvVisualArtifactValidator
{
    public static IReadOnlyList<ValidationEvidence> Validate(
        Mat originalBgr,
        Mat restoredBgr,
        string? diagnosticsDirectory = null,
        string? assetBaseName = null)
    {
        if (originalBgr.Empty() || restoredBgr.Empty() || originalBgr.Size() != restoredBgr.Size())
            return Array.Empty<ValidationEvidence>();

        using var originalGray = new Mat();
        using var restoredGray = new Mat();
        Cv2.CvtColor(originalBgr, originalGray, ColorConversionCodes.BGR2GRAY);
        Cv2.CvtColor(restoredBgr, restoredGray, ColorConversionCodes.BGR2GRAY);

        using var change = new Mat();
        Cv2.Absdiff(originalGray, restoredGray, change);
        using var localExpectation = new Mat();
        Cv2.GaussianBlur(change, localExpectation, new Size(61, 61), 0);
        using var residual = new Mat();
        Cv2.Absdiff(change, localExpectation, residual);
        Cv2.GaussianBlur(residual, residual, new Size(5, 5), 0);

        Cv2.MeanStdDev(residual, out var mean, out var stddev);
        var threshold = Math.Max(6.0, mean.Val0 + (1.75 * stddev.Val0));
        using var binary = new Mat();
        Cv2.Threshold(residual, binary, threshold, 255, ThresholdTypes.Binary);
        using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(5, 5));
        Cv2.MorphologyEx(binary, binary, MorphTypes.Close, kernel);

        Cv2.FindContours(binary, out var contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
        var evidences = new List<ValidationEvidence>();
        var minArea = Math.Max(250.0, originalBgr.Rows * originalBgr.Cols * 0.0015);

        foreach (var contour in contours)
        {
            var area = Cv2.ContourArea(contour);
            if (area < minArea)
                continue;

            var bounds = Cv2.BoundingRect(contour);
            if (bounds.Width < 16 || bounds.Height < 16)
                continue;

            var rectangularity = area / Math.Max(1.0, bounds.Width * bounds.Height);
            if (rectangularity < 0.68)
                continue;

            // A copied tile produces a residual mainly along its artificial seam;
            // evaluate active residual pixels rather than diluting that seam over
            // the complete rectangular envelope.
            using var residualRoi = new Mat(residual, bounds);
            using var binaryRoi = new Mat(binary, bounds);
            var residualMean = Cv2.Mean(residualRoi, binaryRoi).Val0;
            if (residualMean < Math.Max(5.0, mean.Val0 * 1.5))
                continue;

            // The failure observed in the calibration was a source tile pasted
            // onto an otherwise globally corrected result. Its signature is a
            // rectangular island with markedly *less* source-to-result change than
            // its immediate surround. Real object removal has the opposite signal;
            // sharpening does not create a suppressed-change island.
            var margin = Math.Max(8, Math.Min(bounds.Width, bounds.Height) / 8);
            var outer = new Rect(
                Math.Max(0, bounds.X - margin),
                Math.Max(0, bounds.Y - margin),
                Math.Min(originalGray.Width, bounds.Right + margin) - Math.Max(0, bounds.X - margin),
                Math.Min(originalGray.Height, bounds.Bottom + margin) - Math.Max(0, bounds.Y - margin));
            using var outerChange = new Mat(change, outer);
            using var ringMask = new Mat(outer.Size, MatType.CV_8UC1, Scalar.White);
            Cv2.Rectangle(ringMask, new Rect(bounds.X - outer.X, bounds.Y - outer.Y, bounds.Width, bounds.Height), Scalar.Black, -1);
            var surroundChange = Cv2.Mean(outerChange, ringMask).Val0;
            var insideChange = Cv2.Mean(new Mat(change, bounds)).Val0;
            if (surroundChange < 4.0 || insideChange > surroundChange * 0.55)
                continue;

            evidences.Add(new ValidationEvidence
            {
                Category = ValidationEvidenceCategory.ImageQuality,
                Type = "ArtificialRectangularArtifact",
                Score = area / (originalBgr.Rows * (double)originalBgr.Cols),
                Threshold = 0,
                Severity = ValidationSeverity.Critical,
                Passed = false,
                Region = $"{bounds.X},{bounds.Y},{bounds.Width},{bounds.Height}",
                Message = $"New coherent rectangular photographic artifact detected (area {area:F0}px, rectangularity {rectangularity:F2}, residual {residualMean:F1}). Automatic acceptance is forbidden."
            });
        }

        if (!string.IsNullOrWhiteSpace(diagnosticsDirectory))
        {
            Directory.CreateDirectory(diagnosticsDirectory);
            using var overlay = restoredBgr.Clone();
            foreach (var item in evidences)
            {
                var parts = item.Region!.Split(',').Select(int.Parse).ToArray();
                Cv2.Rectangle(overlay, new Rect(parts[0], parts[1], parts[2], parts[3]), Scalar.Red, 3);
            }
            var name = string.IsNullOrWhiteSpace(assetBaseName) ? "artificial_rectangular_artifacts.png" : $"{assetBaseName}__artificial_rectangular_artifacts.png";
            Cv2.ImWrite(Path.Combine(diagnosticsDirectory, name), overlay);
        }

        return evidences;
    }
}
