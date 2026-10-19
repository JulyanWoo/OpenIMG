using OpenIMG.Core.Enums;

namespace OpenIMG.Core.Models;

public record CleanupTargetRecord
{
    public string CleanupTargetId { get; init; } = string.Empty;
    public NormalizedRectangle Region { get; init; } = new(0, 0, 0, 0);
    public ConstraintAuthority Authority { get; init; } = ConstraintAuthority.StrictCleanupAllowed;
    public bool ExpectedRemoval { get; init; } = true;
    public bool ObservedAfterRestoration { get; init; }
    public CleanupCompletionStatus CleanupStatus { get; init; } = CleanupCompletionStatus.Complete;
    public double RemainingPixelsRatio { get; init; }
    public string Details { get; init; } = string.Empty;
}
