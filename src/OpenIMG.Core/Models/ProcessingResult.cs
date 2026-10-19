using OpenIMG.Core.Enums;

namespace OpenIMG.Core.Models;

public record ProcessingResult
{
    public bool Success { get; init; }
    public ValidationDecision Decision { get; init; }
    public ValidationDecision AutomatedDecision => Decision;
    public int AttemptNumber { get; init; }
    public string? OutputPath { get; init; }
    public string? MetadataPath { get; init; }
    public string? GeneratedSha256 { get; init; }
    public RestorationFailureReason? FailureReason { get; init; }
    public string? ErrorMessage { get; init; }
    public bool QueuePauseRequested { get; init; }
    public List<ValidationEvidence> Evidences { get; init; } = new();
    public HumanReviewAssessment HumanReview { get; init; } = new();
    public RemoteValidationRequirement? RemoteValidationRequirement { get; init; }
    public List<RemoteValidationReason> RemoteValidationReasons { get; init; } = new();
    public bool RemoteValidationSkipped { get; init; }
    public int GeminiOperationsTotal { get; init; }
}
