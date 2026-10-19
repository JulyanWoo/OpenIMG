using OpenIMG.Core.Enums;

namespace OpenIMG.Core.Models;

public record PhotographicRestorationIntent
{
    public RestorationIntensity DeblurStrength { get; init; } = RestorationIntensity.None;
    public RestorationIntensity DenoiseStrength { get; init; } = RestorationIntensity.None;
    public RestorationIntensity DehazeStrength { get; init; } = RestorationIntensity.None;
    public RestorationIntensity ContrastRecovery { get; init; } = RestorationIntensity.None;
    public RestorationIntensity ColorRecovery { get; init; } = RestorationIntensity.None;
    public RestorationIntensity ExposureRecovery { get; init; } = RestorationIntensity.None;
    public string PerspectiveCorrection { get; init; } = "None";
    public SceneCleanupMode SceneCleanup { get; init; } = SceneCleanupMode.SafeTemporaryOnly;

    public bool HasAnyEnhancementRequested =>
        DeblurStrength != RestorationIntensity.None ||
        DenoiseStrength != RestorationIntensity.None ||
        DehazeStrength != RestorationIntensity.None ||
        ContrastRecovery != RestorationIntensity.None ||
        ColorRecovery != RestorationIntensity.None ||
        ExposureRecovery != RestorationIntensity.None ||
        PerspectiveCorrection != "None";
}
