using OpenIMG.Core.Enums;

namespace OpenIMG.Core.Models;

public record LocalImageQualityAssessment
{
    public double SharpnessScore { get; init; }
    public string SharpnessLevel { get; init; } = "Moderate"; // "VeryLow", "Low", "Moderate", "Good"

    public double ContrastScore { get; init; }
    public string ContrastLevel { get; init; } = "Moderate"; // "Low", "Moderate", "Good"

    public double ExposureMean { get; init; }
    public string ExposureIssue { get; init; } = "None"; // "None", "Underexposed", "Overexposed"

    public double HazeScore { get; init; }
    public string HazeLevel { get; init; } = "None"; // "None", "Low", "Moderate", "High"

    public double ColorCastDelta { get; init; }
    public bool ColorCastDetected { get; init; }

    public double DynamicRangeSpan { get; init; }
    public string DynamicRangeLevel { get; init; } = "Moderate"; // "Low", "Moderate", "High"

    public double NoiseScore { get; init; }
    public string NoiseLevel { get; init; } = "Low"; // "Low", "Moderate", "High"

    public bool HasMeasurableDefects =>
        SharpnessLevel is "VeryLow" or "Low" ||
        ContrastLevel is "Low" ||
        HazeLevel is "Moderate" or "High" ||
        ExposureIssue is not "None" ||
        ColorCastDetected;

    public PhotographicRestorationIntent MapToRestorationIntent(
        PhotographicEnhancementProfile profile = PhotographicEnhancementProfile.Strong,
        SceneCleanupMode cleanupMode = SceneCleanupMode.SafeTemporaryOnly,
        bool safeTemporaryClutterPresent = false,
        bool hasPerspectiveTilt = false)
    {
        if (profile == PhotographicEnhancementProfile.Light)
        {
            return new PhotographicRestorationIntent
            {
                DeblurStrength = SharpnessLevel is "VeryLow" or "Low" ? RestorationIntensity.Light : RestorationIntensity.None,
                DenoiseStrength = NoiseLevel is "High" ? RestorationIntensity.Light : RestorationIntensity.None,
                DehazeStrength = HazeLevel is "High" ? RestorationIntensity.Light : RestorationIntensity.None,
                ContrastRecovery = ContrastLevel is "Low" ? RestorationIntensity.Light : RestorationIntensity.None,
                ColorRecovery = ColorCastDetected ? RestorationIntensity.Light : RestorationIntensity.None,
                ExposureRecovery = ExposureIssue is not "None" ? RestorationIntensity.Light : RestorationIntensity.None,
                PerspectiveCorrection = "None",
                SceneCleanup = cleanupMode
            };
        }

        if (profile == PhotographicEnhancementProfile.Moderate)
        {
            return new PhotographicRestorationIntent
            {
                DeblurStrength = SharpnessLevel is "VeryLow" ? RestorationIntensity.Moderate : (SharpnessLevel is "Low" ? RestorationIntensity.Light : RestorationIntensity.None),
                DenoiseStrength = NoiseLevel is "High" ? RestorationIntensity.Moderate : RestorationIntensity.Light,
                DehazeStrength = HazeLevel is "High" or "Moderate" ? RestorationIntensity.Moderate : RestorationIntensity.Light,
                ContrastRecovery = ContrastLevel is "Low" ? RestorationIntensity.Moderate : RestorationIntensity.Light,
                ColorRecovery = ColorCastDetected ? RestorationIntensity.Moderate : RestorationIntensity.Light,
                ExposureRecovery = ExposureIssue is not "None" ? RestorationIntensity.Moderate : RestorationIntensity.None,
                PerspectiveCorrection = hasPerspectiveTilt ? "SafeMild" : "None",
                SceneCleanup = cleanupMode
            };
        }

        // Strong Profile (Default / Target for Phase 4E.2)
        // Defect-aware: Strong intensity where defects exist, conservative where already good
        var deblur = SharpnessLevel switch
        {
            "VeryLow" => RestorationIntensity.Strong,
            "Low" => RestorationIntensity.Strong,
            "Moderate" => RestorationIntensity.Moderate,
            _ => RestorationIntensity.None // Good sharpness does not force deblur
        };

        var denoise = NoiseLevel switch
        {
            "High" => RestorationIntensity.Strong,
            "Moderate" => RestorationIntensity.Moderate,
            _ => RestorationIntensity.Light
        };

        var dehaze = HazeLevel switch
        {
            "High" => RestorationIntensity.Strong,
            "Moderate" => RestorationIntensity.Strong,
            "Low" => RestorationIntensity.Moderate,
            _ => RestorationIntensity.None
        };

        var contrast = (ContrastLevel, DynamicRangeLevel) switch
        {
            ("Low", _) => RestorationIntensity.Strong,
            (_, "Low") => RestorationIntensity.Strong,
            ("Moderate", _) => RestorationIntensity.Moderate,
            _ => RestorationIntensity.Light
        };

        var color = ColorCastDetected
            ? RestorationIntensity.Strong
            : (ContrastLevel == "Low" ? RestorationIntensity.Moderate : RestorationIntensity.Moderate);

        var exposure = ExposureIssue switch
        {
            "Underexposed" => RestorationIntensity.Strong,
            "Overexposed" => RestorationIntensity.Strong,
            _ => RestorationIntensity.Moderate
        };

        var perspective = hasPerspectiveTilt ? "SafeMild" : "SafeMild";

        return new PhotographicRestorationIntent
        {
            DeblurStrength = deblur,
            DenoiseStrength = denoise,
            DehazeStrength = dehaze,
            ContrastRecovery = contrast,
            ColorRecovery = color,
            ExposureRecovery = exposure,
            PerspectiveCorrection = perspective,
            SceneCleanup = cleanupMode
        };
    }
}
