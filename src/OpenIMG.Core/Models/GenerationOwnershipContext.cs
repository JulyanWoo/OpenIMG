using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using OpenIMG.Core.Enums;

namespace OpenIMG.Core.Models;

/// <summary>
/// Strict ownership and tracking context for an individual AI restoration operation.
/// Enforces 1-to-1 association between source, job, chat session, prompt, generated candidate, and download.
/// </summary>
public class GenerationOwnershipContext
{
    public string GenerationOperationId { get; set; } = string.Empty;
    public string CalibrationId { get; set; } = string.Empty;
    public string AssetId { get; set; } = string.Empty;
    public string ExpectedSourcePath { get; set; } = string.Empty;
    public string ExpectedSourceSha256 { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? PromptSentAt { get; set; }
    public DateTimeOffset? SubmissionAcknowledgedAt { get; set; }
    public string? SubmissionAcknowledgementSignal { get; set; }
    public string? ConversationPathAfterSubmit { get; set; }
    public string? SubmissionDiagnostics { get; set; }
    public int? BaselineUserMessageCount { get; set; }
    public int? UserMessageCountAfterSubmit { get; set; }
    public int? BaselineAssistantResponseCount { get; set; }
    public bool? ComposerClearedAfterSubmit { get; set; }
    public bool? AttachmentsClearedAfterSubmit { get; set; }
    public bool SendClickedExactlyOnce { get; set; }
    public string? CanonicalCompiledPromptSha256 { get; set; }
    public string? CanonicalComposerPromptSha256 { get; set; }
    public bool? CanonicalPromptContentMatches { get; set; }
    public bool PromptContentVerified { get; set; }
    public bool PromptInputEventObserved { get; set; }
    public bool PromptSendButtonDetected { get; set; }
    public bool PromptSendButtonEnabled { get; set; }
    public int? GenerationTimeoutOverrideSeconds { get; set; }
    public DateTimeOffset? GenerationStartedAt { get; set; }
    public DateTimeOffset? GenerationCompletedAt { get; set; }
    public DateTimeOffset? DownloadStartedAt { get; set; }
    public DateTimeOffset? DownloadCompletedAt { get; set; }
    public bool ProviderReadinessConfirmed { get; set; }
    public string? ProviderReadinessFailureStage { get; set; }
    public string? ProviderFailureStage { get; set; }
    public ProviderOperationState OperationState { get; set; } = ProviderOperationState.GenerationPrepared;
    public GeneratedCandidateEvidence? CandidateEvidence { get; set; }

    [JsonIgnore]
    public Func<ProviderOperationState, CancellationToken, Task>? PersistOperationStateAsync { get; set; }

    // DOM & Chat Baselines
    public int ExistingAssistantResponseCount { get; set; }
    public int ExistingGeneratedImageCount { get; set; }
    public int AttachmentCountBeforeUpload { get; set; }
    public int AttachmentCountAfterUpload { get; set; }
    public int? AttachmentCountAfterOriginal { get; set; }
    public int? AttachmentCountAfterGeminiDraft { get; set; }
    public string? AttachmentUploadDiagnostics { get; set; }
    public List<string> ObservedAttachmentChips { get; set; } = new();

    // Ownership & Invariant Verifications
    public bool FreshChatConfirmed { get; set; }
    public bool NewResponseDetected { get; set; }
    public bool NewGeneratedImageDetected { get; set; }
    public bool GeneratedImageCreatedAfterPrompt { get; set; }
    public bool DownloadOwnedByCurrentOperation { get; set; }
    public string? DownloadedTempPath { get; set; }
    public string? PersistedCandidatePath { get; set; }
    public string? PersistedCandidateSha256 { get; set; }
    public string? PersistedCandidateOperationId { get; set; }

    // Corrective-provider ownership. Existing Gemini one-image invariants stay
    // unchanged; GPT uses the explicit expected attachment count and draft.
    public string ProviderName { get; set; } = "Gemini";
    public string? ExpectedSecondarySourcePath { get; set; }
    public string? ExpectedSecondarySourceSha256 { get; set; }
    public int ExpectedAttachmentCount { get; set; } = 1;
    public string? DownloadedCandidateSha256 { get; set; }

    public void AssertPrePromptInvariants()
    {
        if (AttachmentCountBeforeUpload != 0)
        {
            throw new InvalidOperationException(
                $"[GenerationOwnership] Invariant violated: FreshChatAttachmentCountBeforeUpload was {AttachmentCountBeforeUpload}, expected 0.");
        }

        if (AttachmentCountAfterUpload != 1)
        {
            throw new InvalidOperationException(
                $"[GenerationOwnership] Invariant violated: AttachmentCountAfterUpload was {AttachmentCountAfterUpload}, expected exactly 1.");
        }
    }

    public void AssertPostGenerationInvariants()
    {
        if (!NewResponseDetected)
        {
            throw new InvalidOperationException(
                "[GenerationOwnership] Invariant violated: NewResponseDetected is false. No new assistant response was created.");
        }

        if (!NewGeneratedImageDetected)
        {
            throw new InvalidOperationException(
                "[GenerationOwnership] Invariant violated: NewGeneratedImageDetected is false. No new generated image was created.");
        }

        if (!GeneratedImageCreatedAfterPrompt)
        {
            throw new InvalidOperationException(
                "[GenerationOwnership] Invariant violated: GeneratedImageCreatedAfterPrompt is false. Image detected predated the prompt submission.");
        }

        if (!DownloadOwnedByCurrentOperation)
        {
            throw new InvalidOperationException(
                "[GenerationOwnership] Invariant violated: DownloadOwnedByCurrentOperation is false. Intercepted download was not claimed by this operation.");
        }
    }

    public void AssertPersistenceOwnership(string candidatePath, string candidateSha256, string targetCalOrAssetId)
    {
        if (string.IsNullOrEmpty(PersistedCandidateOperationId) || PersistedCandidateOperationId != GenerationOperationId)
        {
            throw new InvalidOperationException(
                $"[GenerationOwnership] Invariant violated: PersistedCandidateOperationId '{PersistedCandidateOperationId}' does not match active GenerationOperationId '{GenerationOperationId}'.");
        }

        if (!string.IsNullOrEmpty(CalibrationId) && CalibrationId != targetCalOrAssetId && AssetId != targetCalOrAssetId)
        {
            throw new InvalidOperationException(
                $"[GenerationOwnership] Invariant violated: Target asset/calibration mismatch: Context is '{CalibrationId}/{AssetId}' but target is '{targetCalOrAssetId}'.");
        }
    }
}
