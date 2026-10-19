namespace OpenIMG.Core.Models;

public enum HumanReviewStatus
{
    Pending,
    Approved,
    Rejected,
    Uncertain
}

/// <summary>
/// Product review state. It is intentionally independent from automated
/// Barrier 3/4 decisions and from calibration human-ground-truth records.
/// </summary>
public record HumanReviewAssessment
{
    public HumanReviewStatus Status { get; init; } = HumanReviewStatus.Pending;
    public string? Fidelity { get; init; }
    public string? PhotographicImprovement { get; init; }
    public string? Cleanup { get; init; }
    public bool? Publishable { get; init; }
    public string? Notes { get; init; }
    public DateTimeOffset? ReviewedAt { get; init; }
}
