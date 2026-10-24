using System.Text.Json;
using OpenIMG.App.Browser;

namespace OpenIMG.Tests;

[Trait("Category", TestCategories.Unit)]
public sealed class GptOperationDiagnosticsTests
{
    [Theory]
    [InlineData("/c/private%2Did", true)]
    [InlineData("/c/private%252Did", false)]
    [InlineData("/c/local%3Aprivate-id", true)]
    public void EncodedCheckpointReportsWhetherDecodingProducesAnAllowedIdentifier(string path, bool valid)
    {
        var checkpoint = JsonSerializer.Serialize(new { Format = "OpenIMG.GptRecovery.v1", ConversationPath = path });
        var report = JsonSerializer.Serialize(GptOperationDiagnostics.DescribeSavedCheckpoint(checkpoint));
        using var document = JsonDocument.Parse(report);
        var route = document.RootElement.GetProperty("conversationRoute");
        Assert.Equal(valid, route.GetProperty("validForRecovery").GetBoolean());
        Assert.Equal(valid, route.GetProperty("percentEncodedIdentifierAccepted").GetBoolean());
        Assert.DoesNotContain("private", report);
    }

    [Theory]
    [InlineData("/c/", "MissingIdentifier")]
    [InlineData("/c/private-id/", "TrailingSlash")]
    [InlineData("/c/private-id/view", "AdditionalPathSegments")]
    [InlineData("/c/private%2Fid", "UnsupportedIdentifierCharacters")]
    public void PreviouslySavedCheckpointCanBeDiagnosedWithoutRestoringOrSubmitting(string path, string reason)
    {
        var saved = JsonSerializer.Serialize(new
        {
            Format = "OpenIMG.GptRecovery.v1", ConversationPath = path,
            OperationId = "PRIVATE_OPERATION", SourceSha256 = "PRIVATE_SOURCE", PromptSha256 = "PRIVATE_PROMPT"
        });
        var report = JsonSerializer.Serialize(GptOperationDiagnostics.DescribeSavedCheckpoint(saved));
        using var document = JsonDocument.Parse(report);
        Assert.Equal("Recognized", document.RootElement.GetProperty("state").GetString());
        var route = document.RootElement.GetProperty("conversationRoute");
        Assert.Equal(reason, route.GetProperty("validationReason").GetString());
        Assert.False(route.GetProperty("validForRecovery").GetBoolean());
        Assert.DoesNotContain("private-id", report);
        Assert.DoesNotContain("private_id", report);
        Assert.DoesNotContain("PRIVATE_OPERATION", report);
        Assert.DoesNotContain("PRIVATE_SOURCE", report);
        Assert.DoesNotContain("PRIVATE_PROMPT", report);
    }

    [Theory]
    [InlineData(null, "Missing")]
    [InlineData("not-json PRIVATE_SECRET", "InvalidJson")]
    [InlineData("{\"Format\":\"PRIVATE_FORMAT\"}", "Unrecognized")]
    public void MissingOrMalformedHistoryDoesNotExposeStoredContent(string? saved, string state)
    {
        var report = JsonSerializer.Serialize(GptOperationDiagnostics.DescribeSavedCheckpoint(saved));
        Assert.Equal("{\"state\":\"" + state + "\"}", report);
    }

    [Theory]
    [InlineData(null, "Missing", false)]
    [InlineData("/c/", "MissingIdentifier", false)]
    [InlineData("/c/private-id", "Valid", true)]
    [InlineData("/c/private-id/", "TrailingSlash", false)]
    [InlineData("/c/private-id/view", "AdditionalPathSegments", false)]
    [InlineData("/c/private%2Fid", "UnsupportedIdentifierCharacters", false)]
    [InlineData("/c/private-id?token=PRIVATE_SECRET", "QueryOrFragment", false)]
    public void RouteEvidenceContainsOnlyShapeAndValidationReason(string? path, string reason, bool valid)
    {
        var report = JsonSerializer.Serialize(GptOperationDiagnostics.DescribeConversationRoute(path));
        using var document = JsonDocument.Parse(report);
        Assert.Equal(reason, document.RootElement.GetProperty("validationReason").GetString());
        Assert.Equal(valid, document.RootElement.GetProperty("validForRecovery").GetBoolean());
        Assert.DoesNotContain("private-id", report);
        Assert.DoesNotContain("private_id", report);
        Assert.DoesNotContain("PRIVATE_SECRET", report);
        if (path is not null) Assert.DoesNotContain(path, report);
    }

    [Theory]
    [InlineData("/c/", "MissingIdentifier")]
    [InlineData("/c/PRIVATE_CONVERSATION/", "TrailingSlash")]
    [InlineData("/c/PRIVATE_CONVERSATION/view", "AdditionalPathSegments")]
    [InlineData("/c/PRIVATE%2FCONVERSATION", "UnsupportedIdentifierCharacters")]
    public async Task InvalidSavedRouteExplainsRejectionBeforeNavigationWithoutLeakingTheRoute(string path, string reason)
    {
        var session = new GptGeneratedImageDownloadSession(path, 0, "prompt");
        var navigated = false;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => session.RestoreConversationViewIfNeededAsync(
            (_, _) => Task.FromResult("{\"state\":\"ImageOnlyView\"}"),
            (_, _) => { navigated = true; return Task.CompletedTask; }, CancellationToken.None));
        Assert.Contains("InvalidSavedConversationRoute", error.Message);
        Assert.Contains(reason, error.Message);
        Assert.DoesNotContain("PRIVATE_CONVERSATION", error.Message);
        Assert.False(navigated);
    }

    [Fact]
    public void ResponseEvidenceOnlyRetainsAllowlistedCountersAndFlags()
    {
        using var state = JsonDocument.Parse("""{"responseCount":1,"currentResponseImageCount":2,"currentResponseOwnedImage":true,"prompt":"PRIVATE_PROMPT","url":"https://private.invalid/token"}""");
        var diagnostics = new GptOperationDiagnostics();
        diagnostics.Stage("WaitingForGeneratedImage");
        diagnostics.ObserveResponse(state.RootElement);
        var report = JsonSerializer.Serialize(diagnostics.Snapshot());
        Assert.Contains("WaitingForGeneratedImage", report);
        Assert.Contains("\"ownedImageCount\":2", report);
        Assert.DoesNotContain("PRIVATE_PROMPT", report);
        Assert.DoesNotContain("private.invalid", report);
    }
}
