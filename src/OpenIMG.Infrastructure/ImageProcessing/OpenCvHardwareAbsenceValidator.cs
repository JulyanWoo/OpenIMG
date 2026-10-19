using OpenCvSharp;
using OpenIMG.Core.Enums;
using OpenIMG.Core.Models;

namespace OpenIMG.Infrastructure.ImageProcessing;

public static class OpenCvHardwareAbsenceValidator
{
    public static List<ValidationEvidence> Validate(Mat originalGray, Mat restoredGray, FurnitureAnalysis analysis)
    {
        var evidence = new List<ValidationEvidence>();

        foreach (var absence in analysis.HardwareAbsenceConstraints.Where(x => x.Strict && x.Confidence >= 0.70))
        {
            using var o = Crop(originalGray, absence.Region);
            using var r = Crop(restoredGray, absence.Region);
            if (o.Empty() || r.Empty()) continue;

            using var oe = Edges(o);
            using var re = Edges(r);
            using var oeDilated = new Mat();
            using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(3, 3));
            Cv2.Dilate(oe, oeDilated, kernel);
            using var unexpected = new Mat();
            Cv2.Subtract(re, oeDilated, unexpected);

            var originalEdges = Cv2.CountNonZero(oe);
            var restoredEdges = Cv2.CountNonZero(re);
            var unexpectedEdges = Cv2.CountNonZero(unexpected);

            if (restoredEdges < 20) continue; // Clean, empty region as expected

            var edgeRatio = unexpectedEdges / (double)Math.Max(1, restoredEdges);

            using var labels = new Mat();
            using var stats = new Mat();
            using var centroids = new Mat();
            var count = Cv2.ConnectedComponentsWithStats(unexpected, labels, stats, centroids);

            var largestCluster = 0;
            var isElongated = false;

            for (var i = 1; i < count; i++)
            {
                var area = stats.Get<int>(i, (int)ConnectedComponentsTypes.Area);
                var w = stats.Get<int>(i, (int)ConnectedComponentsTypes.Width);
                var h = stats.Get<int>(i, (int)ConnectedComponentsTypes.Height);

                if (area > largestCluster)
                {
                    largestCluster = area;
                    var aspect = Math.Max((double)w / Math.Max(1, h), (double)h / Math.Max(1, w));
                    isElongated = aspect >= 2.0;
                }
            }

            // If an unexpected edge cluster is detected and it is substantial and elongated (hardware-like)
            if (edgeRatio > 0.25 && largestCluster > 30 && isElongated)
            {
                evidence.Add(new ValidationEvidence
                {
                    Category = ValidationEvidenceCategory.Structural,
                    Type = "HardwareAbsenceViolated",
                    Region = absence.ConstraintId,
                    Score = edgeRatio,
                    Threshold = 0.25,
                    Severity = ValidationSeverity.Critical,
                    Passed = false,
                    Message = $"Unexpected hardware mechanism detected in protected hardware absence region '{absence.ConstraintId}' on the {absence.Side} side (unexpected edges={edgeRatio:P1}, largest cluster={largestCluster}px, elongated={isElongated}). Bilateral asymmetry violated."
                });
            }
        }

        return evidence;
    }

    private static Mat Edges(Mat m)
    {
        var e = new Mat();
        Cv2.Canny(m, e, 50, 130);
        return e;
    }

    private static Mat Crop(Mat source, ProtectedRegion region)
    {
        var x = Math.Clamp((int)(region.X * source.Width), 0, source.Width - 1);
        var y = Math.Clamp((int)(region.Y * source.Height), 0, source.Height - 1);
        var w = Math.Clamp((int)(region.Width * source.Width), 1, source.Width - x);
        var h = Math.Clamp((int)(region.Height * source.Height), 1, source.Height - y);
        return new Mat(source, new Rect(x, y, w, h));
    }
}
