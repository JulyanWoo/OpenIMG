using OpenIMG.Core.Enums;

namespace OpenIMG.Core.Models;

public record FallbackReasonEntry
{
    public FallbackReasonSeverity Severity { get; init; }
    public string Code { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
}
