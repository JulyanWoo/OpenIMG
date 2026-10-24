using OpenIMG.Application.Restoration;
using OpenIMG.Core.Entities;
using OpenIMG.Core.Enums;
using OpenIMG.Core.Models;

namespace OpenIMG.Tests;

[Trait("Category", TestCategories.Unit)]
public sealed class GenerationRecoveryCheckpointTests
{
    [Theory]
    [InlineData("/c/local-chatgpt:fixture")]
    [InlineData("/c/local-chatgpt%3Afixture")]
    public async Task ProvisionalChatGptRouteCannotBindBeforeTheServerConversation(string provisionalPath)
    {
        var persistedRoutes = new List<string?>();
        var context = new GenerationOwnershipContext
        {
            OperationState = ProviderOperationState.GenerationSubmitted, SendClickedExactlyOnce = true
        };
        context.PersistOperationStateAsync = (_, _) =>
        {
            persistedRoutes.Add(context.ConversationPathAfterSubmit);
            return Task.CompletedTask;
        };
        var lifecycle = new GptOperationLifecycle(context, "Restore source");
        Assert.False(GenerationRecoveryCheckpoint.IsConversationPath(provisionalPath));
        await lifecycle.BindConversationAsync(provisionalPath, default);
        Assert.Null(context.ConversationPathAfterSubmit);
        await lifecycle.AdvanceAsync(ProviderOperationState.Generating, default);
        await lifecycle.BindConversationAsync("/c/server-operation", default);
        Assert.Equal("/c/server-operation", context.ConversationPathAfterSubmit);
        Assert.Equal(ProviderOperationState.Generating, context.OperationState);
        Assert.Equal("/c/server-operation", persistedRoutes[^1]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => lifecycle.BindConversationAsync("/c/unrelated", default));
    }

    [Theory]
    [InlineData("/c/conv%65rsation%2D123", "/c/conversation-123")]
    [InlineData("/c/conv%65rsation%2d123", "/c/conversation-123")]
    [InlineData("/c/private%5Fid", "/c/private_id")]
    [InlineData("/c/%7Eprivate.name", "/c/~private.name")]
    [InlineData("/c/local%3Aprivate-id", "/c/local:private-id")]
    [InlineData("/c/local:private-id", "/c/local:private-id")]
    [InlineData("/c/%36%39%33%30%61%62%63%64-abcd-abcd-abcd-123456789abc", "/c/6930abcd-abcd-abcd-abcd-123456789abc")]
    public void EncodedAsciiConversationCheckpointRestoresExistingOperationWithoutChangingHistory(string encoded, string canonical)
    {
        var asset = new ImageAsset { OriginalFilePath = "source.png" };
        var context = new GenerationOwnershipContext
        {
            GenerationOperationId = "operation-123", AssetId = asset.Id.ToString(), ExpectedSourceSha256 = "SOURCE",
            ConversationPathAfterSubmit = canonical
        };
        var saved = System.Text.Json.JsonSerializer.Deserialize<GenerationRecoveryCheckpoint>(GenerationRecoveryCheckpoint.Capture(context, "Restore source"))!;
        var legacyJson = System.Text.Json.JsonSerializer.Serialize(saved with { ConversationPath = encoded });
        var attempt = new RestorationAttempt { ProviderOperationId = "operation-123", RestorationPrompt = "Restore source", RawResponse = legacyJson, ProviderOperationState = ProviderOperationState.NeedsRecovery };
        var restored = GenerationRecoveryCheckpoint.Restore(attempt, asset, "SOURCE");
        Assert.NotNull(restored);
        Assert.Equal(canonical, restored.ConversationPathAfterSubmit);
        Assert.Equal(legacyJson, attempt.RawResponse);
        Assert.Equal(ProviderOperationState.NeedsRecovery, attempt.ProviderOperationState);
        Assert.True(GenerationRecoveryCheckpoint.IsConversationPath(encoded));
        Assert.Null(GenerationRecoveryCheckpoint.Restore(attempt, asset, "DIFFERENT_SOURCE"));
        context.ConversationPathAfterSubmit = encoded;
        var newlyCaptured = System.Text.Json.JsonSerializer.Deserialize<GenerationRecoveryCheckpoint>(GenerationRecoveryCheckpoint.Capture(context, "Restore source"))!;
        Assert.Equal(canonical, newlyCaptured.ConversationPath);
    }

    [Fact]
    public void RoundTripKeepsOperationSourcePromptAndConversationBound()
    {
        var asset = new ImageAsset { OriginalFilePath = "source.png" };
        var context = new GenerationOwnershipContext
        {
            GenerationOperationId = "operation-123", AssetId = asset.Id.ToString(), ExpectedSourceSha256 = "SOURCE",
            ConversationPathAfterSubmit = "/c/conversation-123", BaselineAssistantResponseCount = 0
        };
        var attempt = new RestorationAttempt
        {
            ProviderOperationId = context.GenerationOperationId, RestorationPrompt = "Restore source",
            RawResponse = GenerationRecoveryCheckpoint.Capture(context, "Restore source")
        };

        var restored = GenerationRecoveryCheckpoint.Restore(attempt, asset, "SOURCE");

        Assert.NotNull(restored);
        Assert.Equal(context.GenerationOperationId, restored.GenerationOperationId);
        Assert.Equal(context.ConversationPathAfterSubmit, restored.ConversationPathAfterSubmit);
        Assert.Equal(ProviderOperationState.NeedsRecovery, restored.OperationState);
        Assert.Null(GenerationRecoveryCheckpoint.Restore(attempt, asset, "DIFFERENT"));
        attempt.RestorationPrompt = "Different prompt";
        Assert.Null(GenerationRecoveryCheckpoint.Restore(attempt, asset, "SOURCE"));
    }

    [Theory]
    [InlineData("/c/../../private")]
    [InlineData("/c/conversation?token=secret")]
    [InlineData("//another-host/c/id")]
    [InlineData("/c/")]
    [InlineData("/c/%2E%2E")]
    [InlineData("/c/%2E")]
    [InlineData("/c/..")]
    [InlineData("/c/id%2Fother")]
    [InlineData("/c/id%5Cother")]
    [InlineData("/c/id%3Ftoken")]
    [InlineData("/c/id%23fragment")]
    [InlineData("/c/id%00")]
    [InlineData("/c/id%20")]
    [InlineData("/c/id%252Dother")]
    [InlineData("/c/%")]
    [InlineData("/c/%2")]
    [InlineData("/c/%GG")]
    [InlineData("/c/%C3%A9")]
    [InlineData("/c/%5BconversationId%5D")]
    public void RejectsNonConversationRoutes(string path) => Assert.False(GenerationRecoveryCheckpoint.IsConversationPath(path));
}
