using OpenCvSharp;
using OpenIMG.Application.Abstractions.Analysis;
using OpenIMG.Core.Enums;
using OpenIMG.Core.Models;

namespace OpenIMG.Infrastructure.Analysis;

public class OpenCvStructuralRegionDetector : IStructuralRegionDetector
{
    public List<LocalStructuralRegion> DetectStructuralRegions(Mat image)
    {
        var regions = new List<LocalStructuralRegion>();
        if (image.Empty() || image.Width < 50 || image.Height < 50)
            return regions;

        var imgW = image.Width;
        var imgH = image.Height;
        var totalArea = (double)(imgW * imgH);

        // 1. Grayscale normalization and contrast enhancement
        using var gray = new Mat();
        if (image.Channels() == 3)
            Cv2.CvtColor(image, gray, ColorConversionCodes.BGR2GRAY);
        else
            image.CopyTo(gray);

        using var equalized = new Mat();
        using var clahe = Cv2.CreateCLAHE(clipLimit: 2.5, tileGridSize: new Size(8, 8));
        clahe.Apply(gray, equalized);

        // Slight smoothing to preserve edges while reducing sensor noise
        using var blurred = new Mat();
        Cv2.GaussianBlur(equalized, blurred, new Size(3, 3), 0);

        // 2. Edge Extraction
        using var edges = new Mat();
        Cv2.Canny(blurred, edges, 40, 120);

        // 3. Line Detection & Horizontal/Vertical Line Grouping
        using var linesMat = new Mat();
        var minLineLen = Math.Max(25, (int)(Math.Min(imgW, imgH) * 0.035));
        var lines = Cv2.HoughLinesP(edges, 1, Math.PI / 180, threshold: 45, minLineLength: minLineLen, maxLineGap: 14);

        var horizontalLines = new List<LineSegmentPoint>();
        var verticalLines = new List<LineSegmentPoint>();

        foreach (var l in lines)
        {
            var dx = Math.Abs(l.P2.X - l.P1.X);
            var dy = Math.Abs(l.P2.Y - l.P1.Y);

            if (dx > 0 && (double)dy / dx < 0.20) // within ~11 degrees of horizontal
            {
                horizontalLines.Add(l);
            }
            else if (dy > 0 && (double)dx / dy < 0.20) // within ~11 degrees of vertical
            {
                verticalLines.Add(l);
            }
        }

        // Group horizontal lines into division/shelf hypotheses
        var hLineGroups = GroupCollinearLines(horizontalLines, isHorizontal: true, imgW, imgH);
        foreach (var hGroup in hLineGroups)
        {
            var aspect = hGroup.Region.Width / Math.Max(0.001, hGroup.Region.Height);
            var type = aspect > 4.0 ? StructuralRegionType.ShelfCandidate : StructuralRegionType.HorizontalDivision;
            var conf = Math.Min(0.75, 0.40 + (hGroup.Count * 0.08));

            regions.Add(new LocalStructuralRegion
            {
                Id = $"struct_h_{regions.Count + 1}",
                Region = hGroup.Region,
                RegionType = type,
                Confidence = Math.Round(conf, 3),
                Orientation = "Horizontal",
                AspectRatio = Math.Round(aspect, 2),
                Notes = $"Horizontal division hypothesis from {hGroup.Count} aligned segment(s)."
            });
        }

        // Group vertical lines into division hypotheses
        var vLineGroups = GroupCollinearLines(verticalLines, isHorizontal: false, imgW, imgH);
        foreach (var vGroup in vLineGroups)
        {
            var aspect = vGroup.Region.Width / Math.Max(0.001, vGroup.Region.Height);
            var conf = Math.Min(0.70, 0.38 + (vGroup.Count * 0.08));

            regions.Add(new LocalStructuralRegion
            {
                Id = $"struct_v_{regions.Count + 1}",
                Region = vGroup.Region,
                RegionType = StructuralRegionType.VerticalDivision,
                Confidence = Math.Round(conf, 3),
                Orientation = "Vertical",
                AspectRatio = Math.Round(aspect, 2),
                Notes = $"Vertical division hypothesis from {vGroup.Count} aligned segment(s)."
            });
        }

        // 4. Contour detection & Rectangular region proposals
        using var morphed = new Mat();
        using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(5, 5));
        Cv2.MorphologyEx(edges, morphed, MorphTypes.Close, kernel);

        Cv2.FindContours(morphed, out var contours, out _, RetrievalModes.Tree, ContourApproximationModes.ApproxSimple);

        foreach (var c in contours)
        {
            var area = Cv2.ContourArea(c);
            var areaRatio = area / totalArea;

            // Filter out tiny noise and full-image boundary frame (reduced from 0.015 to 0.008 for drawer scales)
            if (areaRatio < 0.008 || areaRatio > 0.92) continue;

            var bbox = Cv2.BoundingRect(c);
            var normX = (double)bbox.X / imgW;
            var normY = (double)bbox.Y / imgH;
            var normW = (double)bbox.Width / imgW;
            var normH = (double)bbox.Height / imgH;
            var normBox = new NormalizedRectangle(normX, normY, normW, normH).Clamp();

            // Compute rectangularity (fill ratio of bounding rect)
            var rectArea = (double)(bbox.Width * bbox.Height);
            var rectangularity = rectArea > 0 ? area / rectArea : 0;
            if (rectangularity < 0.40) continue;

            var aspect = normBox.Width / Math.Max(0.001, normBox.Height);
            StructuralRegionType regionType;
            double confidence;
            string orientation;
            string notes;

            if (normBox.Area > 0.45)
            {
                regionType = StructuralRegionType.LargeFurnitureRegion;
                confidence = 0.72;
                orientation = aspect > 1.2 ? "Horizontal" : (aspect < 0.8 ? "Vertical" : "Neutral");
                notes = $"Dominant large structural region candidate (Area={normBox.Area * 100:F1}%).";
            }
            else if (aspect > 2.0 && normBox.Height < 0.28)
            {
                regionType = StructuralRegionType.DrawerLikeRegion;
                confidence = 0.55; // Geometric hypothesis, not semantic truth
                orientation = "Horizontal";
                notes = "Elongated horizontal rectangular region resembling drawer or horizontal bay.";
            }
            else if (aspect < 0.80 && normBox.Height > 0.22)
            {
                regionType = StructuralRegionType.DoorLikeRegion;
                confidence = 0.58; // Geometric hypothesis, not semantic truth
                orientation = "Vertical";
                notes = "Vertical rectangular region resembling cabinet door panel.";
            }
            else if (rectangularity > 0.75)
            {
                regionType = StructuralRegionType.StructuralPanelCandidate;
                confidence = 0.65;
                orientation = aspect > 1.1 ? "Horizontal" : (aspect < 0.9 ? "Vertical" : "Neutral");
                notes = $"High rectangularity planar panel candidate (Rectangularity={rectangularity:F2}).";
            }
            else
            {
                regionType = StructuralRegionType.RectangularComponentCandidate;
                confidence = 0.50;
                orientation = "Neutral";
                notes = "General rectangular component proposal.";
            }

            regions.Add(new LocalStructuralRegion
            {
                Id = $"struct_rect_{regions.Count + 1}",
                Region = normBox,
                RegionType = regionType,
                Confidence = Math.Round(confidence, 3),
                Orientation = orientation,
                AspectRatio = Math.Round(aspect, 2),
                Notes = notes
            });
        }

        return DeduplicateRegions(regions);
    }

    private record LineGroup(NormalizedRectangle Region, int Count);

    private static List<LineGroup> GroupCollinearLines(List<LineSegmentPoint> lines, bool isHorizontal, int imgW, int imgH)
    {
        var groups = new List<LineGroup>();
        if (lines.Count == 0) return groups;

        var spatialTolerancePx = Math.Max(6, (int)(Math.Min(imgW, imgH) * 0.015));
        var remaining = new List<LineSegmentPoint>(lines);

        while (remaining.Count > 0)
        {
            var seed = remaining[0];
            remaining.RemoveAt(0);

            var cluster = new List<LineSegmentPoint> { seed };
            var minCoord = isHorizontal ? Math.Min(seed.P1.Y, seed.P2.Y) : Math.Min(seed.P1.X, seed.P2.X);
            var maxCoord = isHorizontal ? Math.Max(seed.P1.Y, seed.P2.Y) : Math.Max(seed.P1.X, seed.P2.X);

            for (var i = remaining.Count - 1; i >= 0; i--)
            {
                var candidate = remaining[i];
                var candMin = isHorizontal ? Math.Min(candidate.P1.Y, candidate.P2.Y) : Math.Min(candidate.P1.X, candidate.P2.X);
                var candMax = isHorizontal ? Math.Max(candidate.P1.Y, candidate.P2.Y) : Math.Max(candidate.P1.X, candidate.P2.X);

                if (Math.Abs(candMin - minCoord) <= spatialTolerancePx || Math.Abs(candMax - maxCoord) <= spatialTolerancePx)
                {
                    cluster.Add(candidate);
                    remaining.RemoveAt(i);
                }
            }

            var x1 = cluster.Min(l => Math.Min(l.P1.X, l.P2.X));
            var x2 = cluster.Max(l => Math.Max(l.P1.X, l.P2.X));
            var y1 = cluster.Min(l => Math.Min(l.P1.Y, l.P2.Y));
            var y2 = cluster.Max(l => Math.Max(l.P1.Y, l.P2.Y));

            var width = Math.Max(isHorizontal ? 20 : 6, x2 - x1);
            var height = Math.Max(isHorizontal ? 6 : 20, y2 - y1);
            var span = isHorizontal ? width : height;
            var minSpanPx = Math.Max(26, (int)(Math.Min(imgW, imgH) * 0.035));

            // Accept if multiple collinear segments or single segment of sufficient length
            if (cluster.Count < 2 && span < minSpanPx) continue;

            var normBox = new NormalizedRectangle((double)x1 / imgW, (double)y1 / imgH, (double)width / imgW, (double)height / imgH).Clamp();
            // Lines are primarily 1D features; check span and minimum area
            if (normBox.Area > 0.0002 && (normBox.Width >= 0.025 || normBox.Height >= 0.025))
            {
                groups.Add(new LineGroup(normBox, cluster.Count));
            }
        }

        return groups;
    }

    private static List<LocalStructuralRegion> DeduplicateRegions(List<LocalStructuralRegion> list)
    {
        var sorted = list.OrderByDescending(r => r.Confidence).ThenByDescending(r => r.Region.Area).ToList();
        var kept = new List<LocalStructuralRegion>();

        foreach (var r in sorted)
        {
            var duplicate = false;
            foreach (var k in kept)
            {
                if (k.RegionType == r.RegionType && k.Region.CalculateIoU(r.Region) > 0.80)
                {
                    duplicate = true;
                    break;
                }
            }
            if (!duplicate)
            {
                kept.Add(r);
            }
        }

        return kept.Take(35).ToList();
    }
}
