using OpenCvSharp;

namespace OpenIMG.Infrastructure.ImageProcessing;

/// <summary>
/// Pure OpenCV photometric metrics used by the structural validator.
/// Keeping these calculations independent makes the validator responsible for
/// evidence orchestration rather than low-level image math.
/// </summary>
internal static class OpenCvPhotometricMetrics
{
    public static double CalculateSsim(Mat i1, Mat i2)
    {
        if (i1.Width != i2.Width || i1.Height != i2.Height)
        {
            using var resized2 = new Mat();
            Cv2.Resize(i2, resized2, i1.Size(), 0, 0, InterpolationFlags.Area);
            return CalculateSsim(i1, resized2);
        }

        const double c1 = 6.5025, c2 = 58.5225;

        using var i1Double = new Mat();
        using var i2Double = new Mat();
        i1.ConvertTo(i1Double, MatType.CV_32F);
        i2.ConvertTo(i2Double, MatType.CV_32F);

        using var i1Sq = i1Double.Mul(i1Double);
        using var i2Sq = i2Double.Mul(i2Double);
        using var i1I2 = i1Double.Mul(i2Double);

        using var mu1 = new Mat();
        using var mu2 = new Mat();
        Cv2.GaussianBlur(i1Double, mu1, new Size(11, 11), 1.5);
        Cv2.GaussianBlur(i2Double, mu2, new Size(11, 11), 1.5);

        using var mu1Sq = mu1.Mul(mu1);
        using var mu2Sq = mu2.Mul(mu2);
        using var mu1Mu2 = mu1.Mul(mu2);

        using var sigma1Sq = new Mat();
        using var sigma2Sq = new Mat();
        using var sigma12 = new Mat();

        using var g1 = new Mat();
        using var g2 = new Mat();
        using var g3 = new Mat();
        Cv2.GaussianBlur(i1Sq, g1, new Size(11, 11), 1.5);
        Cv2.GaussianBlur(i2Sq, g2, new Size(11, 11), 1.5);
        Cv2.GaussianBlur(i1I2, g3, new Size(11, 11), 1.5);

        Cv2.Subtract(g1, mu1Sq, sigma1Sq);
        Cv2.Subtract(g2, mu2Sq, sigma2Sq);
        Cv2.Subtract(g3, mu1Mu2, sigma12);

        using var t1 = new Mat();
        using var t2 = new Mat();
        using var t3 = new Mat();

        // t1 = 2 * mu1_mu2 + C1
        using var twoMu1Mu2 = mu1Mu2 * 2;
        Cv2.Add(twoMu1Mu2, new Scalar(c1), t1);

        // t2 = 2 * sigma12 + C2
        using var twoSigma12 = sigma12 * 2;
        Cv2.Add(twoSigma12, new Scalar(c2), t2);

        // t3 = t1 * t2
        using var num = t1.Mul(t2);

        // den1 = mu1_sq + mu2_sq + C1
        using var den1 = new Mat();
        using var muSum = new Mat();
        Cv2.Add(mu1Sq, mu2Sq, muSum);
        Cv2.Add(muSum, new Scalar(c1), den1);

        // den2 = sigma1_sq + sigma2_sq + C2
        using var den2 = new Mat();
        using var sigSum = new Mat();
        Cv2.Add(sigma1Sq, sigma2Sq, sigSum);
        Cv2.Add(sigSum, new Scalar(c2), den2);

        using var den = den1.Mul(den2);
        using var ssimMap = new Mat();
        Cv2.Divide(num, den, ssimMap);

        var mean = Cv2.Mean(ssimMap);
        return Math.Clamp(mean.Val0, 0.0, 1.0);
    }

    public static (double RawDiff, double NormDiff) CalculateColorMetrics(Mat lab1, Mat lab2)
    {
        var mean1 = Cv2.Mean(lab1);
        var mean2 = Cv2.Mean(lab2);

        // Raw color difference (normalized L*a*b* Euclidean distance of means)
        var dL = (mean1.Val0 - mean2.Val0) / 255.0;
        var da = (mean1.Val1 - mean2.Val1) / 255.0;
        var db = (mean1.Val2 - mean2.Val2) / 255.0;
        var rawDiff = Math.Sqrt(dL * dL + da * da + db * db);

        // Normalized spatial color distribution: subtract global shift before measuring
        using var shifted2 = new Mat();
        Cv2.Subtract(lab2, new Scalar(mean2.Val0 - mean1.Val0, mean2.Val1 - mean1.Val1, mean2.Val2 - mean1.Val2), shifted2);

        using var diff = new Mat();
        Cv2.Absdiff(lab1, shifted2, diff);
        var diffMean = Cv2.Mean(diff);
        var normDiff = (diffMean.Val0 + diffMean.Val1 + diffMean.Val2) / (3.0 * 255.0);

        return (rawDiff, normDiff);
    }

    public static double CalculateLargestConnectedRegionPercentage(Mat diffBinary, int totalPixels)
    {
        using var labels = new Mat();
        using var stats = new Mat();
        using var centroids = new Mat();

        var numLabels = Cv2.ConnectedComponentsWithStats(diffBinary, labels, stats, centroids);
        if (numLabels <= 1) return 0.0;

        var maxArea = 0;
        for (int i = 1; i < numLabels; i++)
        {
            var area = stats.At<int>(i, (int)ConnectedComponentsTypes.Area);
            if (area > maxArea)
                maxArea = area;
        }

        return (double)maxArea / totalPixels;
    }

    public static (double LumDelta, double ChromaDelta, double ChromaticDist) CalculateLabColorShift(Mat origLab, Mat alignedLab)
    {
        var meanOrig = Cv2.Mean(origLab);
        var meanAligned = Cv2.Mean(alignedLab);

        var lumDelta = Math.Abs(meanAligned[0] - meanOrig[0]);
        var da = meanAligned[1] - meanOrig[1];
        var db = meanAligned[2] - meanOrig[2];
        var chromaticDist = Math.Sqrt(da * da + db * db);

        var cOrig = Math.Sqrt(meanOrig[1] * meanOrig[1] + meanOrig[2] * meanOrig[2]);
        var cAligned = Math.Sqrt(meanAligned[1] * meanAligned[1] + meanAligned[2] * meanAligned[2]);
        var chromaDelta = Math.Abs(cAligned - cOrig);

        return (lumDelta, chromaDelta, chromaticDist);
    }

    public static Mat CreateColorDeltaMap(Mat origLab, Mat alignedLab)
    {
        Cv2.Split(origLab, out var origChannels);
        Cv2.Split(alignedLab, out var alignedChannels);

        using var da = new Mat();
        using var db = new Mat();
        Cv2.Absdiff(alignedChannels[1], origChannels[1], da);
        Cv2.Absdiff(alignedChannels[2], origChannels[2], db);

        using var da16 = new Mat();
        using var db16 = new Mat();
        da.ConvertTo(da16, MatType.CV_16SC1);
        db.ConvertTo(db16, MatType.CV_16SC1);

        using var da2 = new Mat();
        using var db2 = new Mat();
        Cv2.Multiply(da16, da16, da2);
        Cv2.Multiply(db16, db16, db2);

        using var sumSq = new Mat();
        Cv2.Add(da2, db2, sumSq);

        using var sumSq32 = new Mat();
        sumSq.ConvertTo(sumSq32, MatType.CV_32FC1);

        using var dist32 = new Mat();
        Cv2.Sqrt(sumSq32, dist32);

        using var colorDeltaMap = new Mat();
        dist32.ConvertTo(colorDeltaMap, MatType.CV_8UC1, 8.5);
        var colorHeatmap = new Mat();
        Cv2.ApplyColorMap(colorDeltaMap, colorHeatmap, ColormapTypes.Turbo);

        foreach (var c in origChannels) c.Dispose();
        foreach (var c in alignedChannels) c.Dispose();

        return colorHeatmap;
    }
}
