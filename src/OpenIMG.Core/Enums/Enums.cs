namespace OpenIMG.Core.Enums;

public enum ImageProcessingStatus
{
    Pending,
    Analyzing,
    AnalysisCompleted,
    Restoring,
    RestorationCompleted,
    LocalValidation,
    RemoteValidation,
    Accepted,
    NeedsReview,
    Failed,
    Paused,
    Cancelled
}

public enum ValidationDecision
{
    Accepted,
    Rejected,
    NeedsReview
}

public enum RestorationFailureReason
{
    BrowserUnavailable,
    AuthenticationRequired,
    GeminiLimitReached,
    ProviderUsageLimitReached,
    UploadFailed,
    AnalysisFailed,
    InvalidAnalysisResponse,
    RestorationFailed,
    DownloadFailed,
    LocalValidationFailed,
    RemoteValidationFailed,
    ImageRegistrationFailed,
    SourceIntegrityFailed,
    BrowserChanged,
    Cancelled,
    Unknown,
    OperationRecoveryRequired
}

public enum ValidationSeverity
{
    Info,
    Warning,
    Critical
}

public enum CorrectiveRestorationRoute
{
    DeltaCorrection,
    TargetedStructuralCorrection,
    NeedsReview
}

public enum RegistrationModel
{
    None,
    Translation,
    Similarity,
    Affine,
    Homography
}

// Backward compatibility alias for RegistrationModel
public enum RegistrationModelType
{
    None = 0,
    Translation = 1,
    Similarity = 2,
    Affine = 3,
    Homography = 4
}

public enum RestorationRetryReason
{
    ConfirmedColorModification,
    ConfirmedStructuralModification,
    ConfirmedComponentModification,
    ConfirmedMaterialModification,
    ConfirmedUnexpectedObject,
    ConfirmedDecorativeModification,
    ValidatorUncertain,
    RegistrationUncertain,
    MetricConflict,
    RemoteUncertain,
    BrowserFailure,
    GenerationFailure,
    Unknown
}

public enum ValidationEvidenceCategory
{
    Structural,
    Color,
    Geometry,
    Silhouette,
    Registration,
    ProtectedRegion,
    Component,
    ImageQuality,
    Diagnostic
}
