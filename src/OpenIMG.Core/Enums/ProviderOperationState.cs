namespace OpenIMG.Core.Enums;

/// <summary>
/// Checkpoint state for a provider-backed restoration attempt. The state is
/// intentionally provider-neutral so a paused GPT correction can resume
/// without replaying a completed Gemini operation.
/// </summary>
public enum ProviderOperationState
{
    Pending = 0,
    SourceVerified = 1,
    AnalysisCompleted = 2,
    GeminiPending = 3,
    GeminiCompleted = 4,
    GptCorrectionPending = 5,
    GptCorrectionCompleted = 6,
    Barrier3Pending = 7,
    Barrier3Completed = 8,
    Finalized = 9,
    Failed = 10,
    GenerationPrepared = 11,
    GenerationMayExist = 12,
    GenerationSubmitted = 13,
    CandidateDownloaded = 14,
    ValidationPending = 15,
    NeedsRecovery = 16,
    Ready = 17,
    AttachmentsUploaded = 18,
    PromptVerified = 19,
    ConversationBound = 20,
    Generating = 21,
    CandidateDetected = 22,
    CandidateOwned = 23,
    ViewerOpened = 24,
    DownloadStarted = 25,
    FileVerified = 26
}
