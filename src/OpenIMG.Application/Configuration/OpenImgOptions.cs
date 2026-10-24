using OpenIMG.Core.Enums;

namespace OpenIMG.Application.Configuration;

public class OpenImgOptions
{
    public const string SectionName = "OpenIMG";
    public const int CurrentSettingsSchemaVersion = 3;

    public int SettingsSchemaVersion { get; set; } = CurrentSettingsSchemaVersion;

    public string DatabasePath { get; set; } = string.Empty;
    public string WebView2ProfilePath { get; set; } = string.Empty;
    public string GptWebView2ProfilePath { get; set; } = string.Empty;
    public string DefaultOutputDirectory { get; set; } = string.Empty;
    public bool AutomaticResultCopyEnabled { get; set; }
    public string AutomaticResultCopyDirectory { get; set; } = string.Empty;
    public string LogsDirectory { get; set; } = string.Empty;
    public string SettingsPath { get; set; } = string.Empty;
    public string OperationsDirectory { get; set; } = string.Empty;
    public bool EnableCalibrationFeatures { get; set; } = false;
    public bool ProviderTraceEnabled { get; set; } = false;

    public const string DefaultAnalysisPromptVersion = "analysis-v4";
    public const string DefaultRestorationPromptVersion = "restoration-v4";
    public const string DefaultValidationPromptVersion = "validation-v4";

    public int MaxAutomaticAttempts { get; set; } = 1;
    public bool RunRemoteValidationForCalibration { get; set; } = true;

    // Legacy settings retained for one-time migration and historical calibration.
    // Current product behavior is controlled by the typed settings below.
    public string RestorationProfile { get; set; } = "Conservative";
    public bool AllowSceneCleanup { get; set; } = false;
    public bool AllowPersonRemoval { get; set; } = false;
    public bool AllowReflectionCleanup { get; set; } = false;
    public bool AllowZoomOut { get; set; } = false;
    public bool AllowOutpainting { get; set; } = false;
    public int ProcessingConcurrency { get; set; } = 1;
    public bool AutoTuneThresholds { get; set; } = false;

    // Phase 4E.2 Decoupled Profile Dimensions
    public StructuralFidelityProfile StructuralFidelityProfile { get; set; } = StructuralFidelityProfile.VeryStrict;
    public PhotographicEnhancementProfile PhotographicEnhancementProfile { get; set; } = PhotographicEnhancementProfile.Strong;
    public SceneCleanupMode SceneCleanupMode { get; set; } = SceneCleanupMode.SafeTemporaryOnly;
    public PersonRemovalMode PersonRemovalMode { get; set; } = PersonRemovalMode.Disabled;

    // Local OpenCV validation thresholds
    public double MinStructuralSimilarity { get; set; } = 0.70;
    public double MinEdgeSimilarity { get; set; } = 0.65;
    public double EdgeMatchTolerancePixels { get; set; } = 3.0;
    public double MinSilhouetteSimilarity { get; set; } = 0.75;
    public double MaxAllowedGlobalColorDifference { get; set; } = 0.35;
    public double MaxAllowedNormalizedColorDistributionDifference { get; set; } = 0.30;
    public double MaxAllowedChangedAreaPercentage { get; set; } = 0.20;
    public double MaxAllowedLargestChangedRegionPercentage { get; set; } = 0.08;
    public double MaxAllowedProtectedAreaChangedPercentage { get; set; } = 0.05;
    public double MinImageRegistrationConfidence { get; set; } = 0.50;
    public double MaxAspectRatioDifference { get; set; } = 0.05;
    public int MinimumUnexpectedEdgeClusterPixels { get; set; } = 20;
    public double MaxAllowedUnexpectedEdgeRatio { get; set; } = 0.25;
    public double MaxAllowedUnexpectedClusterRatio { get; set; } = 0.05;

    // Phase 3.6 Region-Aware & Reframing Thresholds
    public double MaxAllowedFurnitureStructuralColorDifference { get; set; } = 0.25;
    public double MaxAllowedMaterialSensitiveColorDifference { get; set; } = 0.20;
    public double MinAccentColorRegionPreservation { get; set; } = 0.90;
    public double MaxAllowedSceneCleanupIntrusionPercentage { get; set; } = 0.03;
    public double MinBoundaryContinuityScore { get; set; } = 0.85;
    public double MaxAllowedFurnitureScaleShiftWithinOverlap { get; set; } = 0.10;
    public double MinFurnitureAspectConsistency { get; set; } = 0.92;
    public double MinFurniturePerspectiveConsistency { get; set; } = 0.90;

    // Browser automation timeouts (in seconds)
    public int PageLoadTimeoutSeconds { get; set; } = 45;
    public int ResponseTimeoutSeconds { get; set; } = 120;
    public int GptGenerationTimeoutSeconds { get; set; } = 600;
    public int SubmissionAcknowledgementTimeoutSeconds { get; set; } = 15;
    public int DownloadTimeoutSeconds { get; set; } = 60;

    // Prompt templates & versions
    public string AnalysisPromptVersion { get; set; } = "analysis-v4";
    public string RestorationPromptVersion { get; set; } = "restoration-v4";
    public string ValidationPromptVersion { get; set; } = "validation-v4";

    public string LogLevel { get; set; } = "Information";

    // Phase 4A Local Analysis Engine
    public LocalAnalysisMode LocalAnalysisMode { get; set; } = LocalAnalysisMode.Preferred;
    public string ModelsDirectory { get; set; } = "models";
    public int OnnxIntraOpThreads { get; set; } = 2;
    public int OnnxInterOpThreads { get; set; } = 1;
    public int LocalAnalysisMaxDimension { get; set; } = 1280;
    public double LocalAnalysisConfidenceThreshold { get; set; } = 0.65;

    // Phase 4D Conditional Remote Comparative Validation (Barrier 4)
    public ComparativeValidationMode ComparativeValidationMode { get; set; } = ComparativeValidationMode.Shadow;
    public bool RequireRemoteValidationForHighRiskAccepted { get; set; } = true;

    // Historical configuration container retained for settings compatibility.
    // The product runtime uses the GPT ceiling only; Gemini is not registered.
    public DualModelRestorationOptions DualModelRestoration { get; set; } = new();
}
