using OpenCvSharp;
using OpenIMG.Application.Abstractions.Analysis;
using OpenIMG.Core.Models;

namespace OpenIMG.Infrastructure.Analysis;

public class OpenCvColorRegionAnalyzer : IColorRegionAnalyzer
{
    public List<LocalColorRegion> AnalyzeColorRegions(Mat image, NormalizedRectangle? furnitureBounds = null)
    {
        var result = new List<LocalColorRegion>();
        if (image.Empty() || image.Width < 20 || image.Height < 20)
            return result;

        // 1. Determine region of interest (furniture bounds or whole frame)
        Mat roiSource = image;
        var offsetX = 0.0;
        var offsetY = 0.0;
        var scaleX = 1.0;
        var scaleY = 1.0;

        if (furnitureBounds != null && furnitureBounds.Area > 0.05)
        {
            var clamped = furnitureBounds.Clamp();
            var rx = (int)(clamped.X * image.Width);
            var ry = (int)(clamped.Y * image.Height);
            var rw = Math.Max(10, (int)(clamped.Width * image.Width));
            var rh = Math.Max(10, (int)(clamped.Height * image.Height));

            if (rx + rw > image.Width) rw = image.Width - rx;
            if (ry + rh > image.Height) rh = image.Height - ry;

            if (rw >= 20 && rh >= 20)
            {
                roiSource = new Mat(image, new Rect(rx, ry, rw, rh));
                offsetX = (double)rx / image.Width;
                offsetY = (double)ry / image.Height;
                scaleX = (double)rw / image.Width;
                scaleY = (double)rh / image.Height;
            }
        }

        try
        {
            // 2. Downsample for fast, bounded-memory CPU clustering (160x160 max)
            const int sampleSize = 160;
            using var sampled = new Mat();
            var sampleScale = Math.Min((double)sampleSize / roiSource.Width, (double)sampleSize / roiSource.Height);
            var sw = Math.Max(16, (int)(roiSource.Width * sampleScale));
            var sh = Math.Max(16, (int)(roiSource.Height * sampleScale));
            Cv2.Resize(roiSource, sampled, new Size(sw, sh), 0, 0, InterpolationFlags.Area);

            // 3. Convert to Lab perceptual color space
            using var lab = new Mat();
            Cv2.CvtColor(sampled, lab, ColorConversionCodes.BGR2Lab);

            var totalPixels = sw * sh;
            using var points = new Mat(totalPixels, 3, MatType.CV_32F);

            unsafe
            {
                var labPtr = (byte*)lab.Data.ToPointer();
                var floatPtr = (float*)points.Data.ToPointer();

                for (var y = 0; y < sh; y++)
                {
                    var row = labPtr + (y * lab.Step());
                    for (var x = 0; x < sw; x++)
                    {
                        var px = row + (x * 3);
                        var idx = (y * sw + x) * 3;
                        floatPtr[idx] = px[0];     // L in 0..255
                        floatPtr[idx + 1] = px[1]; // a in 0..255
                        floatPtr[idx + 2] = px[2]; // b in 0..255
                    }
                }
            }

            // 4. K-Means clustering (K=4)
            const int k = 4;
            using var labels = new Mat();
            using var centers = new Mat();
            var criteria = new TermCriteria(CriteriaTypes.Eps | CriteriaTypes.MaxIter, 10, 1.0);
            Cv2.Kmeans(points, k, labels, criteria, 3, KMeansFlags.PpCenters, centers);

            // Count cluster sizes and bounding rectangles
            var clusterCounts = new int[k];
            var minX = new int[k];
            var minY = new int[k];
            var maxX = new int[k];
            var maxY = new int[k];

            for (var i = 0; i < k; i++)
            {
                minX[i] = sw; minY[i] = sh;
                maxX[i] = 0; maxY[i] = 0;
            }

            for (var y = 0; y < sh; y++)
            {
                for (var x = 0; x < sw; x++)
                {
                    var label = labels.At<int>(y * sw + x, 0);
                    clusterCounts[label]++;
                    if (x < minX[label]) minX[label] = x;
                    if (y < minY[label]) minY[label] = y;
                    if (x > maxX[label]) maxX[label] = x;
                    if (y > maxY[label]) maxY[label] = y;
                }
            }

            // Identify dominant cluster
            var maxCount = clusterCounts.Max();
            var dominantIdx = Array.IndexOf(clusterCounts, maxCount);

            // Build LocalColorRegions
            for (var i = 0; i < k; i++)
            {
                var count = clusterCounts[i];
                var areaPercentage = Math.Round((double)count / totalPixels, 3);
                if (areaPercentage < 0.02) continue; // ignore tiny noise clusters

                var lVal = centers.At<float>(i, 0);
                var aVal = centers.At<float>(i, 1);
                var bVal = centers.At<float>(i, 2);

                // Convert OpenCV Lab values to standard CIELAB: L in [0, 100], a,b in [-128, +127]
                var standardL = Math.Round(lVal * 100.0 / 255.0, 2);
                var standardA = Math.Round(aVal - 128.0, 2);
                var standardB = Math.Round(bVal - 128.0, 2);
                var labColor = new LabColor(standardL, standardA, standardB);

                // Convert back to RGB for human-readable hex string
                var rgbHex = LabToRgbHex((byte)Math.Clamp(lVal, 0, 255), (byte)Math.Clamp(aVal, 0, 255), (byte)Math.Clamp(bVal, 0, 255));

                var isDominant = (i == dominantIdx) || (areaPercentage >= 0.30);
                var chroma = Math.Sqrt(standardA * standardA + standardB * standardB);
                var isAccent = !isDominant && (areaPercentage >= 0.03 && areaPercentage <= 0.20) && (chroma > 15.0 || Math.Abs(standardL - 50.0) > 35.0);

                var localNormX = (double)minX[i] / sw;
                var localNormY = (double)minY[i] / sh;
                var localNormW = Math.Max(0.05, (double)(maxX[i] - minX[i]) / sw);
                var localNormH = Math.Max(0.05, (double)(maxY[i] - minY[i]) / sh);

                // Map back to whole image coordinate space
                var wholeX = offsetX + (localNormX * scaleX);
                var wholeY = offsetY + (localNormY * scaleY);
                var wholeW = localNormW * scaleX;
                var wholeH = localNormH * scaleY;
                var finalRegion = new NormalizedRectangle(wholeX, wholeY, wholeW, wholeH).Clamp();

                var conf = isDominant ? 0.90 : (isAccent ? 0.78 : 0.65);

                result.Add(new LocalColorRegion
                {
                    Region = finalRegion,
                    DominantLab = labColor,
                    RepresentativeRgb = rgbHex,
                    AreaPercentage = areaPercentage,
                    IsDominant = isDominant,
                    IsAccentCandidate = isAccent,
                    Confidence = conf
                });
            }
        }
        finally
        {
            if (roiSource != image)
            {
                roiSource.Dispose();
            }
        }

        return result.OrderByDescending(r => r.AreaPercentage).ToList();
    }

    private static string LabToRgbHex(byte l, byte a, byte b)
    {
        using var singleLab = new Mat(1, 1, MatType.CV_8UC3, new Scalar(l, a, b));
        using var singleBgr = new Mat();
        Cv2.CvtColor(singleLab, singleBgr, ColorConversionCodes.Lab2BGR);
        var bgr = singleBgr.At<Vec3b>(0, 0);
        return $"#{bgr.Item2:X2}{bgr.Item1:X2}{bgr.Item0:X2}";
    }
}
