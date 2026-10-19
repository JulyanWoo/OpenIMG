using OpenCvSharp;
using OpenIMG.Core.Enums;
using OpenIMG.Core.Models;

namespace OpenIMG.Infrastructure.ImageProcessing;

public record SceneColorValidationMetrics
{
    public double DeltaE { get; init; }
    public double HueShift { get; init; }
    public double ChromaRatio { get; init; } = 1.0;
    public double WarmCoolBiasDelta { get; init; }
    public double LightnessChange { get; init; }
    public bool ColorIdentityPreserved { get; init; } = true;
    public List<ValidationEvidence> Evidences { get; init; } = new();
}

public static class OpenCvSceneColorValidator
{
    public static SceneColorValidationMetrics Validate(Mat originalBgr, Mat restoredBgr, List<SceneColorReference> references)
    {
        var evidences = new List<ValidationEvidence>();
        var maxDeltaE = 0.0;
        var maxHueShift = 0.0;
        var minChromaRatio = 1.0;
        var maxBiasDelta = 0.0;
        var avgLightnessChange = 0.0;
        var allPreserved = true;

        var activeRefs = references.Where(r => r.Protected && r.Confidence >= 0.65).ToList();
        if (activeRefs.Count == 0)
        {
            return new SceneColorValidationMetrics();
        }

        foreach (var sceneRef in activeRefs)
        {
            using var oCrop = Crop(originalBgr, sceneRef.Region);
            using var rCrop = Crop(restoredBgr, sceneRef.Region);
            if (oCrop.Empty() || rCrop.Empty() || oCrop.Rows < 5 || oCrop.Cols < 5) continue;

            using var oLab = new Mat();
            using var rLab = new Mat();
            Cv2.CvtColor(oCrop, oLab, ColorConversionCodes.BGR2Lab);
            Cv2.CvtColor(rCrop, rLab, ColorConversionCodes.BGR2Lab);

            var (oL, oA, oB) = ComputeTrimmedMeanLab(oLab);
            var (rL, rA, rB) = ComputeTrimmedMeanLab(rLab);

            var dL = rL - oL;
            var da = rA - oA;
            var db = rB - oB;
            var deltaE = Math.Sqrt(dL * dL + da * da + db * db);

            var chromaOrig = Math.Sqrt(oA * oA + oB * oB);
            var chromaRest = Math.Sqrt(rA * rA + rB * rB);
            var chromaRatio = chromaRest / Math.Max(0.5, chromaOrig);

            var hueOrig = (Math.Atan2(oB, oA) * (180.0 / Math.PI) + 360.0) % 360.0;
            var hueRest = (Math.Atan2(rB, rA) * (180.0 / Math.PI) + 360.0) % 360.0;
            var hueDiff = Math.Abs(hueRest - hueOrig);
            if (hueDiff > 180.0) hueDiff = 360.0 - hueDiff;

            var biasOrig = oB - oA;
            var biasRest = rB - rA;
            var biasDelta = biasRest - biasOrig;

            maxDeltaE = Math.Max(maxDeltaE, deltaE);
            maxHueShift = Math.Max(maxHueShift, hueDiff);
            minChromaRatio = Math.Min(minChromaRatio, chromaRatio);
            maxBiasDelta = Math.Max(maxBiasDelta, Math.Abs(biasDelta));
            avgLightnessChange = dL;

            // Policy evaluation:
            // 1. If original surface is Warm (b* >= 5.0, Chroma >= 5.0, such as warm beige/taupe wall):
            //    - It is PERMITTED to become brighter (dL > 0).
            //    - It is FORBIDDEN to be neutralized to flat grey/white (chroma collapse < 45% or b* <= 3.5 while L increases).
            if (sceneRef.WarmCoolBias == "Warm" && chromaOrig >= 1.2 && sceneRef.ProhibitNeutralization)
            {
                var neutralized = chromaRatio < 0.45 || (rB <= 1.0 && chromaRest <= 1.5 && dL > 0);
                var radicalHueShift = hueDiff > 45.0 && chromaRest >= 2.5;

                if (neutralized || radicalHueShift)
                {
                    allPreserved = false;
                    var failureReason = neutralized
                        ? $"bleached/neutralized from warm {sceneRef.HueFamily} (b*={oB:F1}, Chroma={chromaOrig:F1}) to flat grey/white (b*={rB:F1}, Chroma={chromaRest:F1}, ChromaRatio={chromaRatio:P0})"
                        : $"shifted hue family radically ({hueDiff:F1}° shift from original {sceneRef.HueFamily})";

                    evidences.Add(new ValidationEvidence
                    {
                        Category = ValidationEvidenceCategory.Diagnostic,
                        Type = "PermanentSceneColorIdentityChanged",
                        Region = sceneRef.RegionId,
                        Score = chromaRatio,
                        Threshold = 0.45,
                        Severity = ValidationSeverity.Critical,
                        Passed = false,
                        Message = $"Permanent scene surface '{sceneRef.RegionId}' ({sceneRef.Role}) color identity changed: {failureReason}. Scene color fidelity violated."
                    });
                }
            }
        }

        return new SceneColorValidationMetrics
        {
            DeltaE = Math.Round(maxDeltaE, 2),
            HueShift = Math.Round(maxHueShift, 2),
            ChromaRatio = Math.Round(minChromaRatio, 3),
            WarmCoolBiasDelta = Math.Round(maxBiasDelta, 2),
            LightnessChange = Math.Round(avgLightnessChange, 2),
            ColorIdentityPreserved = allPreserved,
            Evidences = evidences
        };
    }

    private static (double L, double A, double B) ComputeTrimmedMeanLab(Mat labMat)
    {
        // Sample points, sort L to trim top/bottom 10% (specular highlights & deep shadows)
        var points = new List<(double L, double A, double B)>();
        var rows = labMat.Rows;
        var cols = labMat.Cols;
        var step = Math.Max(1, (rows * cols) / 2000);

        for (var y = 0; y < rows; y += 2)
        {
            for (var x = 0; x < cols; x += 2)
            {
                var vec = labMat.At<Vec3b>(y, x);
                var lVal = vec.Item0 * 100.0 / 255.0;
                var aVal = vec.Item1 - 128.0;
                var bVal = vec.Item2 - 128.0;
                points.Add((lVal, aVal, bVal));
            }
        }

        if (points.Count == 0) return (50, 0, 0);

        points.Sort((p1, p2) => p1.L.CompareTo(p2.L));
        var trimCount = (int)(points.Count * 0.10);
        var trimmed = points.Skip(trimCount).Take(points.Count - 2 * trimCount).ToList();
        if (trimmed.Count == 0) trimmed = points;

        return (
            trimmed.Average(p => p.L),
            trimmed.Average(p => p.A),
            trimmed.Average(p => p.B)
        );
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
