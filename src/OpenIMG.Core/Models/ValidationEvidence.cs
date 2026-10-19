using OpenIMG.Core.Enums;

namespace OpenIMG.Core.Models;

public record ValidationEvidence
{
    public ValidationEvidenceCategory Category { get; init; } = ValidationEvidenceCategory.Diagnostic;
    public string Type { get; init; } = string.Empty;
    public double Score { get; init; }
    public double? Threshold { get; init; }
    public ValidationSeverity Severity { get; init; } = ValidationSeverity.Info;
    public string Message { get; init; } = string.Empty;
    public string? Region { get; init; }
    public bool Passed { get; init; }
}
