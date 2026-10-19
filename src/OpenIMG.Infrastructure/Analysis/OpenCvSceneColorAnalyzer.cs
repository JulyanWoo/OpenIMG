using OpenCvSharp;
using OpenIMG.Application.Abstractions.Analysis;
using OpenIMG.Core.Models;

namespace OpenIMG.Infrastructure.Analysis;

public class OpenCvSceneColorAnalyzer : ISceneColorAnalyzer
{
    public List<SceneColorReference> ExtractSceneColorReferences(Mat image, NormalizedRectangle? furnitureBounds = null)
    {
        var references = new List<SceneColorReference>();
        if (image == null || image.Empty()) return references;

        var bounds = furnitureBounds ?? new NormalizedRectangle(0.1, 0.15, 0.8, 0.6);

        // Candidate 1: Large open scene area below furniture (e.g. wall beneath wall cabinet)
        var lowerY1 = Math.Min(0.95, bounds.Y + bounds.Height + 0.04);
        var lowerH = Math.Min(0.20, 0.98 - lowerY1);

        if (lowerH >= 0.08)
        {
            var wallBox = new NormalizedRectangle(0.25, lowerY1, 0.50, lowerH);
            var sceneRef = AnalyzeCandidateRegion(image, wallBox, "scene_wall_lower", "Wall");
            if (sceneRef != null)
            {
                references.Add(sceneRef);
            }
        }
        else
        {
            // If furniture bounds encompass the lower height, check left or right margin scene areas outside furniture bounds
            NormalizedRectangle? sideBox = null;
            if (bounds.X >= 0.15)
            {
                sideBox = new NormalizedRectangle(0.02, 0.25, Math.Min(0.18, bounds.X - 0.03), 0.50);
            }
            else if (bounds.Right <= 0.85)
            {
                sideBox = new NormalizedRectangle(bounds.Right + 0.03, 0.25, Math.Min(0.18, 0.98 - bounds.Right - 0.03), 0.50);
            }

            if (sideBox != null && sideBox.Area >= 0.03 && bounds.CalculateIoU(sideBox) <= 0.05)
            {
                var sceneRef = AnalyzeCandidateRegion(image, sideBox, "scene_wall_flank", "Wall");
                if (sceneRef != null)
                {
                    references.Add(sceneRef);
                }
            }
        }

        // Candidate 2: Upper wall area above furniture (if furniture starts lower in frame)
        if (bounds.Y >= 0.12)
        {
            var upperH = Math.Min(0.12, bounds.Y - 0.02);
            var upperBox = new NormalizedRectangle(0.25, 0.02, 0.50, upperH);
            var sceneRef = AnalyzeCandidateRegion(image, upperBox, "scene_wall_upper", "Wall");
            if (sceneRef != null)
            {
                references.Add(sceneRef);
            }
        }
        else
        {
            // Probe uppermost scene margin for background wall / ceiling surface
            var probeBox = new NormalizedRectangle(0.25, 0.02, 0.50, 0.10);
            var sceneRef = AnalyzeCandidateRegion(image, probeBox, "scene_wall_upper", "Wall");
            if (sceneRef != null)
            {
                references.Add(sceneRef);
            }
        }

        return references;
    }

    private static SceneColorReference? AnalyzeCandidateRegion(Mat image, NormalizedRectangle normBox, string regionId, string role)
    {
        var x = Math.Clamp((int)(normBox.X * image.Width), 0, image.Width - 1);
        var y = Math.Clamp((int)(normBox.Y * image.Height), 0, image.Height - 1);
        var w = Math.Clamp((int)(normBox.Width * image.Width), 1, image.Width - x);
        var h = Math.Clamp((int)(normBox.Height * image.Height), 1, image.Height - y);

        using var crop = new Mat(image, new Rect(x, y, w, h));
        if (crop.Empty() || crop.Rows < 10 || crop.Cols < 10) return null;

        using var labMat = new Mat();
        Cv2.CvtColor(crop, labMat, ColorConversionCodes.BGR2Lab);

        var points = new List<(double L, double A, double B)>();
        var rows = labMat.Rows;
        var cols = labMat.Cols;
        for (var py = 0; py < rows; py += 3)
        {
            for (var px = 0; px < cols; px += 3)
            {
                var vec = labMat.At<Vec3b>(py, px);
                var lVal = vec.Item0 * 100.0 / 255.0;
                var aVal = vec.Item1 - 128.0;
                var bVal = vec.Item2 - 128.0;

                // Skip extreme shadows, specular highlights, or noise
                if (lVal < 25.0 || lVal > 245.0) continue;
                points.Add((lVal, aVal, bVal));
            }
        }

        if (points.Count < 50) return null;

        points.Sort((p1, p2) => p1.L.CompareTo(p2.L));
        var trimCount = (int)(points.Count * 0.10);
        var trimmed = points.Skip(trimCount).Take(points.Count - 2 * trimCount).ToList();
        if (trimmed.Count == 0) trimmed = points;

        var avgL = Math.Round(trimmed.Average(p => p.L), 2);
        var avgA = Math.Round(trimmed.Average(p => p.A), 2);
        var avgB = Math.Round(trimmed.Average(p => p.B), 2);

        var variance = Math.Round(Math.Sqrt(trimmed.Average(p => Math.Pow(p.L - avgL, 2) + Math.Pow(p.B - avgB, 2))), 2);
        if (variance > 25.0) return null; // Surface is too heterogeneous / noisy to be a stable color reference

        var chroma = Math.Round(Math.Sqrt(avgA * avgA + avgB * avgB), 2);
        var hueDeg = Math.Round((Math.Atan2(avgB, avgA) * (180.0 / Math.PI) + 360.0) % 360.0, 1);

        string warmCoolBias;
        string hueFamily;
        var authority = OpenIMG.Core.Enums.ConstraintAuthority.Strict;
        var conflictDetected = false;

        // Strict CIELAB and Hue Family classification
        if (avgA < -1.8 || (hueDeg >= 105.0 && hueDeg <= 175.0 && avgA < 0))
        {
            // Green / olive / sage spectrum
            warmCoolBias = "Cool";
            hueFamily = "green_tone";
            authority = (chroma >= 5.0 && variance <= 12.0) ? OpenIMG.Core.Enums.ConstraintAuthority.Strict : OpenIMG.Core.Enums.ConstraintAuthority.Advisory;
        }
        else if (avgB <= -2.5 || (hueDeg > 200.0 && hueDeg < 300.0))
        {
            // Cool grey / blue spectrum
            warmCoolBias = "Cool";
            hueFamily = "cool_grey";
            authority = OpenIMG.Core.Enums.ConstraintAuthority.Advisory;
        }
        else if (avgB >= 1.2 && avgA >= -0.8 && (hueDeg <= 100.0 || hueDeg >= 330.0))
        {
            // Warm beige / taupe / amber spectrum
            warmCoolBias = "Warm";
            hueFamily = avgL >= 50.0 && avgB <= 25.0 ? "warm_beige_taupe" : "warm_amber_wood";
            authority = (variance <= 12.0 && chroma >= 5.0) ? OpenIMG.Core.Enums.ConstraintAuthority.Strict : OpenIMG.Core.Enums.ConstraintAuthority.Advisory;
        }
        else if (chroma < 2.0)
        {
            // True neutral grey/white
            warmCoolBias = "Neutral";
            hueFamily = "neutral_grey_white";
            authority = OpenIMG.Core.Enums.ConstraintAuthority.Advisory;
        }
        else
        {
            warmCoolBias = "Neutral";
            hueFamily = "observed_surface_tone";
            authority = OpenIMG.Core.Enums.ConstraintAuthority.Uncertain;
            conflictDetected = true;
        }

        // Compute representative hex
        var refLab = new LabColor(avgL, avgA, avgB);
        var rgbHex = LabToRgbHex(avgL, avgA, avgB);

        return new SceneColorReference
        {
            RegionId = regionId,
            Region = new ProtectedRegion
            {
                X = normBox.X,
                Y = normBox.Y,
                Width = normBox.Width,
                Height = normBox.Height,
                RegionType = "permanent_scene_surface",
                Description = $"{role} permanent surface ({hueFamily})",
                Importance = 0.95
            },
            Role = role,
            ReferenceLab = refLab,
            ReferenceRgb = rgbHex,
            HueFamily = hueFamily,
            WarmCoolBias = warmCoolBias,
            Chroma = chroma,
            HueDegrees = hueDeg,
            RegionVariance = variance,
            Confidence = authority == OpenIMG.Core.Enums.ConstraintAuthority.Strict ? 0.88 : (authority == OpenIMG.Core.Enums.ConstraintAuthority.Advisory ? 0.65 : 0.35),
            Authority = authority,
            ColorConflictDetected = conflictDetected,
            Protected = true,
            AllowedLuminanceAdjustment = true,
            ProhibitNeutralization = (warmCoolBias == "Warm"),
            SourceEvidence = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "Uniform permanent surface detected in open scene area (L*={0:F2}, a*={1:F2}, b*={2:F2}, Chroma={3:F2}, Hue={4:F1}°, variance={5:F2}).",
                avgL, avgA, avgB, chroma, hueDeg, variance)
        };
    }

    private static string LabToRgbHex(double l, double a, double b)
    {
        // Simple approximate RGB representation for hex display
        var y = (l + 16.0) / 116.0;
        var x = a / 500.0 + y;
        var z = y - b / 200.0;

        var x3 = Math.Pow(x, 3) > 0.008856 ? Math.Pow(x, 3) : (x - 16.0 / 116.0) / 7.787;
        var y3 = Math.Pow(y, 3) > 0.008856 ? Math.Pow(y, 3) : (y - 16.0 / 116.0) / 7.787;
        var z3 = Math.Pow(z, 3) > 0.008856 ? Math.Pow(z, 3) : (z - 16.0 / 116.0) / 7.787;

        var xr = x3 * 95.047;
        var yr = y3 * 100.0;
        var zr = z3 * 108.883;

        var r = xr * 3.2406 + yr * -1.5372 + zr * -0.4986;
        var g = xr * -0.9689 + yr * 1.8758 + zr * 0.0415;
        var bl = xr * 0.0557 + yr * -0.2040 + zr * 1.0570;

        var rByte = (byte)Math.Clamp((int)(r > 0.0031308 ? 1.055 * Math.Pow(r / 100.0, 1.0 / 2.4) - 0.055 : 12.92 * (r / 100.0)) * 255, 0, 255);
        var gByte = (byte)Math.Clamp((int)(g > 0.0031308 ? 1.055 * Math.Pow(g / 100.0, 1.0 / 2.4) - 0.055 : 12.92 * (g / 100.0)) * 255, 0, 255);
        var bByte = (byte)Math.Clamp((int)(bl > 0.0031308 ? 1.055 * Math.Pow(bl / 100.0, 1.0 / 2.4) - 0.055 : 12.92 * (bl / 100.0)) * 255, 0, 255);

        return $"#{rByte:X2}{gByte:X2}{bByte:X2}";
    }
}
