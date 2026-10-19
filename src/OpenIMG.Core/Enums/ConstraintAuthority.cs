namespace OpenIMG.Core.Enums;

/// <summary>
/// Defines the level of authority assigned to a machine-vision derived restoration constraint.
/// Only high-confidence, corroborated evidence may become Strict.
/// </summary>
public enum ConstraintAuthority
{
    /// <summary>
    /// High-confidence verified constraint with sufficient corroboration. Absolute fidelity required.
    /// </summary>
    Strict,

    /// <summary>
    /// Medium-confidence observation or pattern. Advisory guidance for restoration.
    /// </summary>
    Advisory,

    /// <summary>
    /// Low-confidence, incomplete, or conflicting evidence. Must NOT be inferred or reconstructed.
    /// </summary>
    Uncertain,

    /// <summary>
    /// High-confidence temporary clutter unattached to furniture. Explicit removal permitted and requested.
    /// </summary>
    StrictCleanupAllowed,

    /// <summary>
    /// Medium-confidence candidate temporary object. Remove only if non-structural and low-risk.
    /// </summary>
    AdvisoryCleanupAllowed
}
