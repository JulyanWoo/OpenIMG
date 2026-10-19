using OpenCvSharp;
using OpenIMG.Core.Enums;

namespace OpenIMG.Infrastructure.ImageProcessing;

/// <summary>
/// Owns image resizing and geometric registration before validation metrics run.
/// </summary>
internal static class OpenCvImageRegistration
{
    internal static (Mat Aligned, RegistrationModel Model, double Confidence, double Scale, double AspectConsistency) RegisterImages(Mat orig, Mat rest)
    {
        // Step 1: Detect ORB features
        using var orb = ORB.Create(2000);
        using var kptsOrig = new Mat();
        using var kptsRest = new Mat();
        using var descOrig = new Mat();
        using var descRest = new Mat();

        orb.DetectAndCompute(orig, null, out var keypointsOrig, descOrig);
        orb.DetectAndCompute(rest, null, out var keypointsRest, descRest);

        if (keypointsOrig.Length < 10 || keypointsRest.Length < 10 || descOrig.Empty() || descRest.Empty())
        {
            return (CreateFallbackAlignedMat(orig, rest), RegistrationModel.None, 0.0, 1.0, 1.0);
        }

        // Match descriptors using BFMatcher with Hamming norm
        using var matcher = new BFMatcher(NormTypes.Hamming, crossCheck: true);
        var matches = matcher.Match(descOrig, descRest);
        if (matches.Length < 10)
        {
            return (CreateFallbackAlignedMat(orig, rest), RegistrationModel.None, 0.0, 1.0, 1.0);
        }

        var goodMatches = matches.OrderBy(m => m.Distance).Take(Math.Min(200, matches.Length)).ToArray();
        var ptsOrig = goodMatches.Select(m => keypointsOrig[m.QueryIdx].Pt).ToArray();
        var ptsRest = goodMatches.Select(m => keypointsRest[m.TrainIdx].Pt).ToArray();

        // Step 2 & 3: Try Similarity Transform (EstimateAffinePartial2D)
        using var inliers = new Mat();
        using var similarityMatrix = Cv2.EstimateAffinePartial2D(InputArray.Create(ptsRest), InputArray.Create(ptsOrig), inliers, RobustEstimationAlgorithms.RANSAC, 3.0);
        var inlierCount = Cv2.CountNonZero(inliers);
        var inlierRatio = (double)inlierCount / goodMatches.Length;

        if (similarityMatrix != null && !similarityMatrix.Empty() && (inlierRatio >= 0.40 || inlierCount >= 30))
        {
            var m00 = similarityMatrix.At<double>(0, 0);
            var m10 = similarityMatrix.At<double>(1, 0);
            var s = Math.Sqrt(m00 * m00 + m10 * m10);
            var scale = s > 0.001 ? (1.0 / s) : 1.0;

            var aligned = new Mat();
            Cv2.WarpAffine(rest, aligned, similarityMatrix, orig.Size());
            var confidence = Math.Clamp(Math.Max(inlierRatio, (double)inlierCount / 40.0), 0.0, 1.0);
            return (aligned, RegistrationModel.Similarity, confidence, scale, 1.0);
        }

        // Step 4: Limited Affine Transform (EstimateAffine2D)
        using var inliersAffine = new Mat();
        using var affineMatrix = Cv2.EstimateAffine2D(InputArray.Create(ptsRest), InputArray.Create(ptsOrig), inliersAffine, RobustEstimationAlgorithms.RANSAC, 4.0);
        var affineInliers = Cv2.CountNonZero(inliersAffine);
        var affineRatio = (double)affineInliers / goodMatches.Length;

        if (affineMatrix != null && !affineMatrix.Empty() && (affineRatio >= 0.35 || affineInliers >= 25))
        {
            var a00 = affineMatrix.At<double>(0, 0);
            var a01 = affineMatrix.At<double>(0, 1);
            var a10 = affineMatrix.At<double>(1, 0);
            var a11 = affineMatrix.At<double>(1, 1);
            var sx = Math.Sqrt(a00 * a00 + a01 * a01);
            var sy = Math.Sqrt(a10 * a10 + a11 * a11);
            var scale = sx > 0.001 ? (1.0 / sx) : 1.0;
            var maxS = Math.Max(sx, sy);
            var aspectConsistency = maxS > 0.001 ? Math.Min(sx, sy) / maxS : 1.0;

            var aligned = new Mat();
            Cv2.WarpAffine(rest, aligned, affineMatrix, orig.Size());
            var confidence = Math.Clamp(Math.Max(affineRatio, (double)affineInliers / 35.0), 0.0, 1.0);
            return (aligned, RegistrationModel.Affine, confidence, scale, aspectConsistency);
        }

        // Step 5: Homography ONLY as diagnostic fallback
        using var inliersH = new Mat();
        using var hMatrix = Cv2.FindHomography(InputArray.Create(ptsRest), InputArray.Create(ptsOrig), HomographyMethods.Ransac, 5.0, inliersH);
        var hInliers = Cv2.CountNonZero(inliersH);
        var hRatio = (double)hInliers / goodMatches.Length;

        if (hMatrix != null && !hMatrix.Empty() && (hRatio >= 0.30 || hInliers >= 25))
        {
            var aligned = new Mat();
            Cv2.WarpPerspective(rest, aligned, hMatrix, orig.Size());
            var confidence = Math.Clamp(Math.Max(hRatio, (double)hInliers / 35.0), 0.0, 1.0);
            return (aligned, RegistrationModel.Homography, confidence, 1.0, 1.0);
        }

        // Fallback scale estimation from keypoint spread
        double fallbackScale = 1.0;
        if (ptsOrig.Length >= 4 && ptsRest.Length >= 4)
        {
            var cOrigX = ptsOrig.Average(p => p.X);
            var cOrigY = ptsOrig.Average(p => p.Y);
            var cRestX = ptsRest.Average(p => p.X);
            var cRestY = ptsRest.Average(p => p.Y);

            var avgDistOrig = ptsOrig.Average(p => Math.Sqrt(Math.Pow(p.X - cOrigX, 2) + Math.Pow(p.Y - cOrigY, 2)));
            var avgDistRest = ptsRest.Average(p => Math.Sqrt(Math.Pow(p.X - cRestX, 2) + Math.Pow(p.Y - cRestY, 2)));

            if (avgDistOrig > 5.0)
            {
                fallbackScale = avgDistRest / avgDistOrig;
            }
        }

        // If all alignments fail, return unaligned resized to match orig dimensions
        return (CreateFallbackAlignedMat(orig, rest), RegistrationModel.None, 0.0, fallbackScale, 1.0);
    }

    internal static Mat CreateFallbackAlignedMat(Mat orig, Mat rest)
    {
        if (rest.Width == orig.Width && rest.Height == orig.Height)
        {
            return rest.Clone();
        }

        var resized = new Mat();
        Cv2.Resize(rest, resized, orig.Size(), 0, 0, InterpolationFlags.Area);
        return resized;
    }

    internal static Mat ResizeToMaxDimension(Mat src, int maxDim)
    {
        if (src.Width <= maxDim && src.Height <= maxDim)
            return src.Clone();

        var scale = Math.Min((double)maxDim / src.Width, (double)maxDim / src.Height);
        var newWidth = Math.Max(1, (int)(src.Width * scale));
        var newHeight = Math.Max(1, (int)(src.Height * scale));

        var dst = new Mat();
        Cv2.Resize(src, dst, new Size(newWidth, newHeight), 0, 0, InterpolationFlags.Area);
        return dst;
    }

    internal static Mat ResizeToMatch(Mat src, int targetWidth, int targetHeight)
    {
        var dst = new Mat();
        Cv2.Resize(src, dst, new Size(targetWidth, targetHeight), 0, 0, InterpolationFlags.Area);
        return dst;
    }
}
