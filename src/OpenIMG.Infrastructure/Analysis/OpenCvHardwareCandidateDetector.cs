using OpenCvSharp;
using OpenIMG.Application.Abstractions.Analysis;
using OpenIMG.Core.Models;

namespace OpenIMG.Infrastructure.Analysis;

public class OpenCvHardwareCandidateDetector : IHardwareCandidateDetector
{
    public List<HardwareCandidate> DetectHardwareCandidates(Mat image, NormalizedRectangle? furnitureBounds = null)
    {
        var candidates = new List<HardwareCandidate>();
        if (image.Empty() || image.Width < 50 || image.Height < 50)
            return candidates;

        var imgW = image.Width;
        var imgH = image.Height;
        var totalArea = (double)(imgW * imgH);

        // 1. Grayscale
        using var gray = new Mat();
        if (image.Channels() == 3)
            Cv2.CvtColor(image, gray, ColorConversionCodes.BGR2GRAY);
        else
            image.CopyTo(gray);

        // 2. Morphological TopHat & BlackHat to capture small localized high-contrast metallic features
        using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(25, 25));
        using var topHat = new Mat();
        using var blackHat = new Mat();
        Cv2.MorphologyEx(gray, topHat, MorphTypes.TopHat, kernel);
        Cv2.MorphologyEx(gray, blackHat, MorphTypes.BlackHat, kernel);

        using var highContrast = new Mat();
        Cv2.Add(topHat, blackHat, highContrast);

        using var thresh = new Mat();
        Cv2.Threshold(highContrast, thresh, 25, 255, ThresholdTypes.Binary);

        // 3. Find contours
        Cv2.FindContours(thresh, out var contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);

        var candidateBoxes = new List<(NormalizedRectangle Box, double Contrast, string Orientation, string GeometryType)>();

        foreach (var c in contours)
        {
            var area = Cv2.ContourArea(c);
            var areaRatio = area / totalArea;

            // Hardware scale: small, bounded area (0.0001 to 0.04 of frame)
            if (areaRatio < 0.0001 || areaRatio > 0.04) continue;

            var bbox = Cv2.BoundingRect(c);
            if (bbox.Width < 3 || bbox.Height < 3) continue;

            var normX = (double)bbox.X / imgW;
            var normY = (double)bbox.Y / imgH;
            var normW = (double)bbox.Width / imgW;
            var normH = (double)bbox.Height / imgH;
            var normBox = new NormalizedRectangle(normX, normY, normW, normH).Clamp();

            // If furnitureBounds is specified, prioritize candidates inside or near the furniture boundary
            if (furnitureBounds != null && furnitureBounds.Area > 0.10)
            {
                // Expanded boundary check (10% margin)
                var margin = 0.08;
                if (normBox.Right < furnitureBounds.X - margin ||
                    normBox.X > furnitureBounds.Right + margin ||
                    normBox.Bottom < furnitureBounds.Y - margin ||
                    normBox.Y > furnitureBounds.Bottom + margin)
                {
                    continue; // outside furniture vicinity
                }
            }

            // Contrast score inside the box
            using var roi = new Mat(highContrast, bbox);
            var meanContrast = Cv2.Mean(roi).Val0;
            var contrastScore = Math.Round(Math.Clamp(meanContrast / 255.0 * 2.0, 0.1, 1.0), 3);

            var aspect = normBox.Width / Math.Max(0.001, normBox.Height);
            string orientation;
            string geomType;

            if (aspect > 2.5)
            {
                orientation = "horizontal";
                geomType = "small_elongated_segment";
            }
            else if (aspect < 0.4)
            {
                orientation = "vertical";
                geomType = "small_elongated_segment";
            }
            else if (contrastScore > 0.5)
            {
                orientation = "point";
                geomType = "metallic_highlight";
            }
            else
            {
                orientation = "neutral";
                geomType = "hardware_candidate";
            }

            candidateBoxes.Add((normBox, contrastScore, orientation, geomType));
        }

        // 4. Repeat Group detection (e.g. multiple aligned handles or hinges)
        var repeatGroups = IdentifyRepeatGroups(candidateBoxes);

        var candId = 1;
        foreach (var (box, contrast, orientation, geomType) in candidateBoxes.OrderByDescending(c => c.Contrast).Take(12))
        {
            var conf = Math.Min(0.80, 0.40 + (contrast * 0.35));
            var repeatId = repeatGroups.TryGetValue(box, out var grp) ? grp : null;

            candidates.Add(new HardwareCandidate
            {
                Id = $"hw_{candId++}",
                Region = box,
                GeometryType = geomType,
                Confidence = Math.Round(conf, 3),
                Orientation = orientation,
                ContrastScore = contrast,
                RepeatGroupId = repeatId
            });
        }

        return candidates;
    }

    private static Dictionary<NormalizedRectangle, string> IdentifyRepeatGroups(
        List<(NormalizedRectangle Box, double Contrast, string Orientation, string GeometryType)> items)
    {
        var result = new Dictionary<NormalizedRectangle, string>();
        var groupId = 1;

        for (var i = 0; i < items.Count; i++)
        {
            var a = items[i].Box;
            var groupMembers = new List<NormalizedRectangle> { a };

            for (var j = i + 1; j < items.Count; j++)
            {
                var b = items[j].Box;
                // Check similar size and aspect ratio
                var wRatio = a.Width / Math.Max(0.001, b.Width);
                var hRatio = a.Height / Math.Max(0.001, b.Height);

                if (wRatio > 0.65 && wRatio < 1.5 && hRatio > 0.65 && hRatio < 1.5)
                {
                    // Check alignment along X (vertical alignment) or Y (horizontal alignment)
                    var xDiff = Math.Abs(a.X - b.X);
                    var yDiff = Math.Abs(a.Y - b.Y);
                    if (xDiff < 0.05 || yDiff < 0.05)
                    {
                        groupMembers.Add(b);
                    }
                }
            }

            if (groupMembers.Count >= 2)
            {
                var name = $"hw_group_{groupId++}";
                foreach (var m in groupMembers)
                {
                    if (!result.ContainsKey(m))
                        result[m] = name;
                }
            }
        }

        return result;
    }
}
