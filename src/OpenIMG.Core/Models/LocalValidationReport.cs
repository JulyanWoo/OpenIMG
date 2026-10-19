using OpenIMG.Core.Enums;

namespace OpenIMG.Core.Models;

public record LocalValidationReport
{
    public bool Passed { get; init; }
    public RegistrationModel RegistrationModel { get; init; } = RegistrationModel.None;
    public double RegistrationConfidence { get; init; }
    public double StructuralSimilarity { get; init; }
    public double EdgeSimilarity { get; init; }
    public double EdgePrecision { get; init; }
    public double EdgeRecall { get; init; }
    public double EdgeF1 { get; init; }
    public double CoarseEdgeF1 { get; init; }
    public double MediumEdgeF1 { get; init; }
    public double MeanEdgeDistance { get; init; }
    public double SilhouetteSimilarity { get; init; }
    public double RawColorDifference { get; init; }
    public double NormalizedColorDistributionDifference { get; init; }
    public double ChangedAreaPercentage { get; init; }
    public double LargestChangedRegionPercentage { get; init; }
    public double ProtectedAreaChangedPercentage { get; init; }
    public double AspectRatioDifference { get; init; }
    public double UnexpectedEdgeRatio { get; init; }
    public double MissingEdgeRatio { get; init; }
    public int UnexpectedEdgePixelCount { get; init; }
    public int MissingEdgePixelCount { get; init; }
    public int UnexpectedEdgeClusterCount { get; init; }
    public int LargestUnexpectedEdgeClusterPixels { get; init; }
    public double LargestUnexpectedEdgeClusterRatio { get; init; }
    public double CoarseUnexpectedEdgeRatio { get; init; }
    public double MediumUnexpectedEdgeRatio { get; init; }

    // Phase 3.6 Explicit Mask Metrics
    public double FurnitureSsim { get; init; }
    public double FurnitureMaskSsim => FurnitureSsim;
    public double FurnitureEdgeSimilarity { get; init; }
    public double FurnitureEdgePrecision { get; init; }
    public double FurnitureEdgeRecall { get; init; }
    public double FurnitureColorDifference { get; init; }
    public double FurnitureChangedAreaPercentage { get; init; }
    public double SceneCleanupAreaPercentage { get; init; }
    public double SceneCleanupIntrusionPercentage { get; init; }
    public double BoundaryContinuityScore { get; init; } = 1.0;
    public bool GlassBoundariesPreserved { get; init; } = true;
    public bool HardwarePreserved { get; init; } = true;

    // Phase 3.6 Region-Aware & Material-Sensitive Color Metrics
    public double GlobalLightingNormalizedColorDifference { get; init; }
    public double FurnitureStructuralRegionColorDifference { get; init; }
    public double MaterialSensitiveColorDifference { get; init; }
    public double AccentColorRegionPreservation { get; init; } = 1.0;

    // Phase 3.6 Overlap-Aware Reframing Metrics
    public double OriginalFrameCoverage { get; init; } = 1.0;
    public double ExpandedCanvasPercentage { get; init; } = 0.0;
    public double FurnitureScaleWithinOverlap { get; init; } = 1.0;
    public double FurnitureAspectConsistency { get; init; } = 1.0;
    public double FurnitureRelativeDimensionConsistency { get; init; } = 1.0;
    public double FurniturePerspectiveConsistency { get; init; } = 1.0;
    public double OutpaintAreaPercentage { get; init; } = 0.0;
    public bool BoundaryExpansionOccurred { get; init; }
    public string ReframingSeverity { get; init; } = "None";
    public bool IsZoomOutAccepted { get; init; } = true;

    // Phase 4C.1 & 4E.2 Photo Quality & Gain Metrics
    public double SharpnessBefore { get; init; }
    public double SharpnessAfter { get; init; }
    public double NoiseEstimateBefore { get; init; }
    public double NoiseEstimateAfter { get; init; }
    public double LocalContrastBefore { get; init; }
    public double LocalContrastAfter { get; init; }
    public double GlobalContrastBefore { get; init; }
    public double GlobalContrastAfter { get; init; }
    public double DynamicRangeBefore { get; init; }
    public double DynamicRangeAfter { get; init; }
    public double ExposureBefore { get; init; }
    public double ExposureAfter { get; init; }
    public double ExposureShift { get; init; }
    public double HazeMetricBefore { get; init; }
    public double HazeMetricAfter { get; init; }
    public double ColorCastMetricBefore { get; init; }
    public double ColorCastMetricAfter { get; init; }
    public double WhiteBalanceShift { get; init; }

    // Phase 4E.2 Quality Gains
    public double SharpnessGain { get; init; }
    public double ContrastGain { get; init; }
    public double DynamicRangeGain { get; init; }
    public double HazeReduction { get; init; }
    public double ColorCastReduction { get; init; }
    public PhotographicImprovementAssessment PhotographicImprovement { get; init; } = PhotographicImprovementAssessment.Moderate;

    // Phase 4C.1 Perspective & Straightening Metrics
    public bool PerspectiveCorrectionDetected { get; init; }
    public double RotationCorrectionDegrees { get; init; }
    public double PerspectiveTransformMagnitude { get; init; }
    public double CropPercentageAfterCorrection { get; init; }
    public bool FurnitureGeometryPreservedAfterRectification { get; init; } = true;

    // Phase 4C.3 Hardware Absence & Scene Color Identity Metrics
    public bool HardwareAbsencePreserved { get; init; } = true;
    public double PermanentSceneColorDeltaE { get; init; }
    public double PermanentSceneHueShift { get; init; }
    public double PermanentSceneChromaRatio { get; init; } = 1.0;
    public double PermanentSceneWarmCoolBiasDelta { get; init; }
    public double PermanentSceneLightnessChange { get; init; }
    public bool PermanentSceneColorIdentityPreserved { get; init; } = true;

    // Phase 4C.4 Decomposed Component & Protected Area Metrics
    public double ProtectedInteriorAreaChangedPercentage { get; init; }
    public double ProtectedEdgeAlignedAreaChangedPercentage { get; init; }
    public bool PhotographicEdgeEnhancementDetected { get; init; }

    public List<ComponentValidationResult> ComponentResults { get; init; } = new();
    public List<ValidationEvidence> Evidences { get; init; } = new();
    public List<string> FailureReasons { get; init; } = new();
    public Dictionary<string, string> DiagnosticImagePaths { get; init; } = new();
    public string? Notes { get; init; }

    // Phase 4E.3 Decision Explanation & Evidence Decomposition
    public List<string> PrimaryDecisionReasons { get; set; } = new();
    public List<string> SupportingWarnings { get; set; } = new();
    public List<string> NonBlockingPhotographicDifferences { get; set; } = new();
    public ValidationDecision Decision { get; set; } = ValidationDecision.NeedsReview;

    // Phase 4F.3 Negative Space, Physical Addition & Cleanup Separation
    public List<ObservedNegativeSpaceRegion> NegativeSpaceRegions { get; init; } = new();
    public List<ProtectedNegativeSpaceViolationResult> NegativeSpaceViolations { get; init; } = new();
    public List<UnsupportedPhysicalAdditionResult> UnsupportedAdditions { get; init; } = new();
    public List<CleanupTargetRecord> CleanupTargets { get; init; } = new();
    public CleanupCompletionStatus CleanupCompletionStatus { get; init; } = CleanupCompletionStatus.NotApplicable;
    public bool HasUnsupportedPhysicalAddition => UnsupportedAdditions.Any(a => a.Severity == ValidationSeverity.Critical && a.IsViolation);
    public bool HasProtectedNegativeSpaceViolation => NegativeSpaceViolations.Any(v => v.IsViolation);
    public bool HasCleanupIncomplete => CleanupCompletionStatus == CleanupCompletionStatus.Incomplete;
    public bool IsCleanupIncomplete => HasCleanupIncomplete;
    public bool HasProtectedNegativeSpaceViolations => NegativeSpaceViolations.Any(v => v.ProtectionLevel == ConstraintAuthority.Strict && v.IsViolation);
    public bool HasUnsupportedPhysicalAdditions => HasUnsupportedPhysicalAddition;
    public int CriticalUnsupportedPhysicalAdditionCount => UnsupportedAdditions.Count(a => a.Severity == ValidationSeverity.Critical && a.IsViolation);
    public int PermanentSceneAdditionCount => UnsupportedAdditions.Count(a => a.Category == PhysicalAdditionCategory.PermanentScenePhysicalAddition && a.IsViolation);
    public int MinorSceneDetailCount => UnsupportedAdditions.Count(a => a.Category == PhysicalAdditionCategory.MinorSceneDetailAddition);
    public bool HasMaterialColorIdentityViolation { get; set; }
}
