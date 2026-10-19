using OpenIMG.Core.Enums;
using OpenIMG.Core.Models;

namespace OpenIMG.Core.Entities;

public class RestorationAttempt
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProcessingJobId { get; set; }
    public int AttemptNumber { get; set; }
    public string PromptVersion { get; set; } = "v1.0";
    public string RestorationPrompt { get; set; } = string.Empty;
    public string? RawResponse { get; set; }
    public string? GeneratedImagePath { get; set; }
    public string? GeneratedSha256 { get; set; }
    public string? ProviderOperationId { get; set; }
    public ProviderOperationState ProviderOperationState { get; set; } = ProviderOperationState.Pending;
    public string? ProviderFailureCategory { get; set; }
    public string? HumanReviewJson { get; set; }
    public string? LocalValidationReportJson { get; set; }
    public string? GeminiValidationReportJson { get; set; }
    public string? CalibrationRemoteValidationJson { get; set; }
    public ValidationDecision? FinalDecision { get; set; }
    public bool ManualApproval { get; set; }
    public RestorationFailureReason? FailureReason { get; set; }
    public string? FailureDetails { get; set; }
    public AnalysisSource? AnalysisSource { get; set; }
    public bool GeminiStructuredAnalysisUsed { get; set; }
    public bool GeminiRestorationUsed { get; set; }
    public bool GeminiComparativeValidationUsed { get; set; }
    public int GeminiOperationsTotal { get; set; }
    public bool RemoteValidationSkipped { get; set; }
    public string? RemoteValidationSkipReason { get; set; }
    public string? RemoteValidationRequirement { get; set; }
    public string? RemoteValidationReasonsJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }

    public ProcessingJob? ProcessingJob { get; set; }
}
