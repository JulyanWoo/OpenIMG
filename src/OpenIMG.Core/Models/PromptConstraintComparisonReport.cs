namespace OpenIMG.Core.Models;

public record PromptConstraintComparisonReport
{
    public string CalibrationId { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;
    public bool FurnitureBoundsProtection { get; init; }
    public bool StructuralRegionProtection { get; init; }
    public bool ColorProtection { get; init; }
    public bool AccentColorProtection { get; init; }
    public bool HardwareProtection { get; init; }
    public bool MaterialProtection { get; init; }
    public bool SceneProtection { get; init; }
    public bool FramingProtection { get; init; }

    // Phase 4C.1 Prompt Coverage Categories
    public bool QualityEnhancement { get; init; }
    public bool WhiteBalanceCorrection { get; init; }
    public bool SurfaceColorRestoration { get; init; }
    public bool IlluminationColorPreservation { get; init; }
    public bool PerspectiveCorrectionPolicy { get; init; }

    // Phase 4C.1 Descriptive Specificity Counts
    public int ExplicitComponentCount { get; init; }
    public int ExplicitProtectedRegionCount { get; init; }
    public int ExplicitColorConstraintCount { get; init; }
    public int ExplicitHardwareConstraintCount { get; init; }
    public int GenericStructuralConstraintCount { get; init; }
    public int UncertainConstraintCount { get; init; }

    public double OverallConstraintAgreement { get; init; }
    public int LocalPromptLength { get; init; }
    public int GeminiPromptLength { get; init; }
    public string Notes { get; init; } = string.Empty;
}
