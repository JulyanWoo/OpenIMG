using System.Security.Cryptography;
using System.Text;
using OpenIMG.Application.Policies;
using OpenIMG.Core.Enums;
using OpenIMG.Core.Models;

namespace OpenIMG.Application.Restoration;

/// <summary>The attempt context is the single authoritative state; browser/UI stages are projections.</summary>
public sealed class GptOperationLifecycle(GenerationOwnershipContext context, string prompt)
{
    private readonly SemaphoreSlim _transitionGate = new(1, 1);
    public GenerationOwnershipContext Context { get; } = context;
    public string PromptSha256 { get; } = Hash(GptPromptTextNormalizer.Canonicalize(prompt));
    public Action<ProviderOperationState>? StatePersisted { get; set; }

    public async Task AdvanceAsync(ProviderOperationState state, CancellationToken ct)
    {
        await _transitionGate.WaitAsync(ct);
        try
        {
            if (Context.OperationState == ProviderOperationState.Finalized && state != ProviderOperationState.Finalized)
                throw new InvalidOperationException("A finalized operation cannot resume browser automation.");
            if (state == ProviderOperationState.GenerationMayExist && Context.PersistOperationStateAsync is null)
                throw new InvalidOperationException("CheckpointPersistenceRequired: Send requires a durable persistence boundary.");
            if (state == ProviderOperationState.GenerationSubmitted && (!Context.SendClickedExactlyOnce || Context.PersistOperationStateAsync is null))
                throw new InvalidOperationException("SubmissionCheckpointBug: acknowledged submission requires a recorded Send and durable persistence.");
            if (Context.SendClickedExactlyOnce && state is ProviderOperationState.Ready or ProviderOperationState.AttachmentsUploaded or ProviderOperationState.PromptVerified or ProviderOperationState.GenerationPrepared)
                throw new InvalidOperationException("An accepted or ambiguous operation cannot return to a pre-submission state.");
            if (state is ProviderOperationState.CandidateOwned or ProviderOperationState.ViewerOpened or ProviderOperationState.DownloadStarted &&
                Context.CandidateEvidence is null)
                throw new InvalidOperationException("Candidate ownership evidence is required before opening or downloading.");
            if (state is ProviderOperationState.CandidateDownloaded or ProviderOperationState.FileVerified &&
                (string.IsNullOrWhiteSpace(Context.DownloadedTempPath) || string.IsNullOrWhiteSpace(Context.DownloadedCandidateSha256)))
                throw new InvalidOperationException("Download completion requires a path and SHA-256 checkpoint.");
            if (state == ProviderOperationState.Finalized && ProviderOperationRecoveryPolicy.MayHaveSubmittedGeneration(Context.OperationState) &&
                string.IsNullOrWhiteSpace(Context.DownloadedCandidateSha256))
                throw new InvalidOperationException("An unresolved submitted candidate cannot be finalized.");
            if (!CanTransition(Context.OperationState, state))
                throw new InvalidOperationException($"Invalid provider lifecycle transition: {Context.OperationState} -> {state}.");
            Context.OperationState = state;
            if (Context.PersistOperationStateAsync is not null)
                await Context.PersistOperationStateAsync(state, ct);
            StatePersisted?.Invoke(state);
        }
        finally { _transitionGate.Release(); }
    }

    public async Task BindConversationAsync(string? path, CancellationToken ct)
    {
        var canonical = GenerationRecoveryCheckpoint.NormalizeConversationPath(path);
        if (canonical is null) return;
        var existing = GenerationRecoveryCheckpoint.NormalizeConversationPath(Context.ConversationPathAfterSubmit);
        if (existing is not null && !string.Equals(existing, canonical, StringComparison.Ordinal))
            throw new InvalidOperationException("OwnershipFailed: conversation identity changed.");
        Context.ConversationPathAfterSubmit = canonical;
        if (Context.OperationState == ProviderOperationState.Generating)
        {
            if (Context.PersistOperationStateAsync is not null)
                await Context.PersistOperationStateAsync(Context.OperationState, ct);
            return;
        }
        if (existing == canonical && Context.OperationState is not ProviderOperationState.GenerationSubmitted and not ProviderOperationState.NeedsRecovery) return;
        await AdvanceAsync(ProviderOperationState.ConversationBound, ct);
    }

    public async Task OwnCandidateAsync(int ordinal, int width, int height, string source, CancellationToken ct)
    {
        if (Context.ConversationPathAfterSubmit is null)
            throw new InvalidOperationException("Candidate ownership requires a bound conversation.");
        if (Context.CandidateEvidence is not null && Context.OperationState is ProviderOperationState.CandidateOwned or ProviderOperationState.ViewerOpened or ProviderOperationState.DownloadStarted)
            return;
        await AdvanceAsync(ProviderOperationState.CandidateDetected, ct);
        Context.CandidateEvidence = new(Context.GenerationOperationId, Context.ExpectedSourceSha256, PromptSha256,
            Context.ConversationPathAfterSubmit, Context.BaselineAssistantResponseCount ?? 0, ordinal, width, height, Hash(source));
        await AdvanceAsync(ProviderOperationState.CandidateOwned, ct);
    }

    public static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static bool CanTransition(ProviderOperationState from, ProviderOperationState to) => from == to ||
        to == ProviderOperationState.NeedsRecovery && from != ProviderOperationState.Finalized || (from, to) switch
        {
            (ProviderOperationState.Pending or ProviderOperationState.SourceVerified or ProviderOperationState.AnalysisCompleted or ProviderOperationState.GenerationPrepared, ProviderOperationState.Ready) => true,
            (ProviderOperationState.Ready, ProviderOperationState.AttachmentsUploaded) => true,
            (ProviderOperationState.AttachmentsUploaded, ProviderOperationState.PromptVerified) => true,
            (ProviderOperationState.PromptVerified, ProviderOperationState.GenerationMayExist) => true,
            (ProviderOperationState.GenerationMayExist, ProviderOperationState.GenerationSubmitted) => true,
            (ProviderOperationState.GenerationSubmitted, ProviderOperationState.ConversationBound or ProviderOperationState.Generating) => true,
            (ProviderOperationState.ConversationBound, ProviderOperationState.Generating or ProviderOperationState.CandidateDetected) => true,
            (ProviderOperationState.Generating, ProviderOperationState.ConversationBound or ProviderOperationState.CandidateDetected) => true,
            (ProviderOperationState.CandidateDetected, ProviderOperationState.CandidateOwned) => true,
            (ProviderOperationState.CandidateOwned, ProviderOperationState.ViewerOpened) => true,
            (ProviderOperationState.ViewerOpened, ProviderOperationState.DownloadStarted) => true,
            (ProviderOperationState.DownloadStarted, ProviderOperationState.CandidateDownloaded) => true,
            (ProviderOperationState.CandidateDownloaded, ProviderOperationState.FileVerified) => true,
            (ProviderOperationState.FileVerified, ProviderOperationState.ValidationPending or ProviderOperationState.Finalized) => true,
            (ProviderOperationState.NeedsRecovery, ProviderOperationState.ConversationBound or ProviderOperationState.CandidateDetected or ProviderOperationState.FileVerified) => true,
            _ => false
        };
}
