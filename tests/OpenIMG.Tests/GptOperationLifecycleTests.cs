using OpenIMG.Application.Restoration;
using OpenIMG.Core.Enums;
using OpenIMG.Core.Models;

namespace OpenIMG.Tests;

[Trait("Category", TestCategories.Unit)]
public sealed class GptOperationLifecycleTests
{
    private static GenerationOwnershipContext Context() => new()
    {
        GenerationOperationId = "fixture-operation", AssetId = "fixture-asset",
        ExpectedSourceSha256 = "SOURCE", OperationState = ProviderOperationState.GenerationPrepared
    };

    [Fact]
    public async Task SendBoundaryRequiresDurablePersistence()
    {
        var lifecycle = new GptOperationLifecycle(Context(), "fixture prompt");
        await Assert.ThrowsAsync<InvalidOperationException>(() => lifecycle.AdvanceAsync(ProviderOperationState.GenerationMayExist, default));
    }

    [Fact]
    public async Task CheckpointExistsBeforeSendAndConversationIsPersistedImmediately()
    {
        var context = Context();
        var checkpoints = new List<GenerationRecoveryCheckpoint>();
        context.PersistOperationStateAsync = (_, _) =>
        {
            checkpoints.Add(System.Text.Json.JsonSerializer.Deserialize<GenerationRecoveryCheckpoint>(GenerationRecoveryCheckpoint.Capture(context, "fixture prompt"))!);
            return Task.CompletedTask;
        };
        var lifecycle = new GptOperationLifecycle(context, "fixture prompt");
        await lifecycle.AdvanceAsync(ProviderOperationState.Ready, default);
        await lifecycle.AdvanceAsync(ProviderOperationState.AttachmentsUploaded, default);
        await lifecycle.AdvanceAsync(ProviderOperationState.PromptVerified, default);
        checkpoints.Clear();
        await lifecycle.AdvanceAsync(ProviderOperationState.GenerationMayExist, default);
        Assert.Single(checkpoints);
        Assert.False(checkpoints[0].SendClicked);
        context.SendClickedExactlyOnce = true;
        await lifecycle.AdvanceAsync(ProviderOperationState.GenerationSubmitted, default);
        await lifecycle.BindConversationAsync("/c/fixture%2Did", default);
        Assert.Equal("/c/fixture-id", checkpoints[^1].ConversationPath);
        Assert.True(checkpoints[^1].SendClicked);
        await Assert.ThrowsAsync<InvalidOperationException>(() => lifecycle.AdvanceAsync(ProviderOperationState.Ready, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => lifecycle.BindConversationAsync("/c/unrelated", default));
    }

    [Fact]
    public async Task OwnedViewerRetainsCandidateAndCannotFinalizeWithoutVerifiedFile()
    {
        var context = Context();
        context.PersistOperationStateAsync = (_, _) => Task.CompletedTask;
        var lifecycle = new GptOperationLifecycle(context, "fixture prompt");
        context.OperationState = ProviderOperationState.GenerationMayExist;
        context.SendClickedExactlyOnce = true;
        await lifecycle.AdvanceAsync(ProviderOperationState.GenerationSubmitted, default);
        await lifecycle.BindConversationAsync("/c/fixture", default);
        await lifecycle.OwnCandidateAsync(0, 400, 300, "synthetic-image-source", default);
        var evidence = context.CandidateEvidence;
        await lifecycle.AdvanceAsync(ProviderOperationState.ViewerOpened, default);
        await lifecycle.AdvanceAsync(ProviderOperationState.DownloadStarted, default);
        Assert.Same(evidence, context.CandidateEvidence);
        await Assert.ThrowsAsync<InvalidOperationException>(() => lifecycle.AdvanceAsync(ProviderOperationState.Finalized, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => lifecycle.AdvanceAsync(ProviderOperationState.FileVerified, default));
    }

    [Fact]
    public async Task ViewerCannotOpenWithoutOwnedCandidate()
    {
        var lifecycle = new GptOperationLifecycle(Context(), "fixture prompt");
        await Assert.ThrowsAsync<InvalidOperationException>(() => lifecycle.AdvanceAsync(ProviderOperationState.ViewerOpened, default));
    }

    [Fact]
    public async Task StateCannotSkipOwnershipOrReturnFromDownloadedToGenerating()
    {
        var context = Context();
        var lifecycle = new GptOperationLifecycle(context, "fixture prompt");
        await Assert.ThrowsAsync<InvalidOperationException>(() => lifecycle.AdvanceAsync(ProviderOperationState.CandidateDetected, default));
        context.OperationState = ProviderOperationState.FileVerified;
        await Assert.ThrowsAsync<InvalidOperationException>(() => lifecycle.AdvanceAsync(ProviderOperationState.Generating, default));
    }
}
