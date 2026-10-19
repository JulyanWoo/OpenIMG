namespace OpenIMG.Core.Enums;

public enum RemoteValidationReason
{
    Barrier3NeedsReview,
    HardwareAmbiguous,
    HardwareComplexityHigh,
    HardwareCardinalityAmbiguous,
    ProtectedHardwarePresent,
    RegistrationUncertain,
    RegistrationNearUncertaintyBoundary,
    AffineRegistrationUsed,
    ComplexGlass,
    GlassOrReflectionComplexity,
    MaterialSensitive,
    MaterialSensitiveSurfaceHigh,
    ColorIdentityUncertain,
    ColorIdentityNearBoundary,
    ProtectedAbsenceUncertain,
    ProtectedAbsenceNearBoundary,
    PersistentDetailUncertain,
    PersistentSceneDetailRisk,
    ComponentCoverageInsufficient,
    MetricConflict,
    LowSemanticConfidence,
    UncertainRegionIntrusion,
    HighSceneComplexity,
    HighRiskAccepted,
    LowRiskAccepted,
    LocalRejected,
    SceneCleanupIncomplete,
    UnsupportedPhysicalAdditionUncertain,
    ProtectedNegativeSpaceUncertain
}
