using System.Text.Json;
using OpenIMG.Application.Restoration;
using OpenIMG.Core.Enums;

namespace OpenIMG.App.Browser;

/// <summary>One owned candidate survives its viewer; restart uses a new session and re-verification.</summary>
internal sealed class GptAutomationSession(
    GptOperationLifecycle lifecycle,
    string prompt,
    Func<string, CancellationToken, Task<string>> execute,
    Func<TimeSpan, CancellationToken, Task> waitForChange)
{
    private readonly string _token = Guid.NewGuid().ToString("N");
    private bool _viewerOpened;
    private int _candidateCount;
    public GptOperationLifecycle Lifecycle => lifecycle;
    public bool ViewerOpened => _viewerOpened;
    public int CandidateCount => _candidateCount;
    public bool CandidateOwned { get; private set; }

    public async Task BeginAsync(bool recovery, CancellationToken ct)
    {
        await execute(GptDomAdapter.HelperScript, ct);
        await execute(GptDomAdapter.OperationScript, ct);
        var result = await execute(Call("begin", _token, lifecycle.Context.ConversationPathAfterSubmit,
            lifecycle.Context.BaselineAssistantResponseCount ?? 0, prompt, recovery), ct);
        if (result != "true") throw new InvalidOperationException("Operation DOM contract could not be initialized.");
    }

    public async Task AcknowledgeAsync(string? path, CancellationToken ct)
    {
        await lifecycle.BindConversationAsync(path, ct);
        if (await execute(Call("submitted", _token, lifecycle.Context.ConversationPathAfterSubmit), ct) != "true")
            throw new InvalidOperationException("SubmissionCheckpointBug: acknowledged Send lost its browser operation.");
    }

    public async Task WaitForOwnedCandidateAsync(TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var snapshot = await ProbeAsync("check", ct);
            var state = Text(snapshot, "state");
            ThrowIfStopped(snapshot);
            if (snapshot.TryGetProperty("conversationPath", out var route))
                await lifecycle.BindConversationAsync(route.GetString(), ct);
            if (state == "CandidateOwned")
            {
                _candidateCount = snapshot.GetProperty("candidateCount").GetInt32();
                await lifecycle.OwnCandidateAsync(0, snapshot.GetProperty("width").GetInt32(), snapshot.GetProperty("height").GetInt32(), Text(snapshot, "source")!, ct);
                CandidateOwned = true;
                return;
            }
            if (state == "Generating" && lifecycle.Context.OperationState is ProviderOperationState.ConversationBound or ProviderOperationState.GenerationSubmitted)
                await lifecycle.AdvanceAsync(ProviderOperationState.Generating, ct);
            await WaitAsync(deadline, ct);
        }
        throw new TimeoutException("Candidate detection reached its bounded deadline; the submission remains recoverable.");
    }

    public async Task<string> PrepareDownloadAsync(TimeSpan timeout, CancellationToken ct)
    {
        if (!CandidateOwned) throw new InvalidOperationException("An operation-owned candidate is required.");
        if (!_viewerOpened && await execute(Call("open", _token), ct) != "true")
            throw new InvalidOperationException("The owned candidate changed before its viewer opened.");
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var snapshot = await ProbeAsync("downloadControl", ct);
            ThrowIfStopped(snapshot);
            if (Text(snapshot, "state") is "ViewerOpened" or "DownloadControlPending" or "DownloadReady")
            {
                if (!_viewerOpened)
                {
                    _viewerOpened = true;
                    await lifecycle.AdvanceAsync(ProviderOperationState.ViewerOpened, ct);
                }
                if (Text(snapshot, "state") == "DownloadReady") return Text(snapshot, "token")!;
            }
            await WaitAsync(deadline, ct);
        }
        throw new TimeoutException("The owned viewer did not expose one usable download control before the deadline.");
    }

    public async Task ClickDownloadAsync(string token, CancellationToken ct)
    {
        await lifecycle.AdvanceAsync(ProviderOperationState.DownloadStarted, ct);
        if (await execute(Call("click", _token, token), ct) != "true")
            throw new InvalidOperationException("The owned viewer changed before its one download click.");
    }

    public Task<string> GetSummaryAsync(CancellationToken ct) => execute("JSON.stringify(" + Call("summary", _token) + ")", ct);
    public bool MatchesSignal(string? token) => token == _token;
    private async Task<JsonElement> ProbeAsync(string probe, CancellationToken ct)
    {
        var raw = await execute("JSON.stringify(window.__openimg_operation_dom ? " + Call(probe, _token) +
            " : {state:'RecoveryRequired',reason:'DocumentClaimMissing'})", ct);
        using var encoded = JsonDocument.Parse(raw);
        using var parsed = JsonDocument.Parse(encoded.RootElement.ValueKind == JsonValueKind.String ? encoded.RootElement.GetString()! : raw);
        return parsed.RootElement.Clone();
    }
    private static string Call(string method, params object?[] args) =>
        "window.__openimg_operation_dom." + method + "(" + string.Join(",", args.Select(value => JsonSerializer.Serialize(value))) + ")";
    private static string? Text(JsonElement value, string name) => value.TryGetProperty(name, out var property) ? property.GetString() : null;
    private static void ThrowIfStopped(JsonElement snapshot)
    {
        if (Text(snapshot, "state") is "RecoveryRequired" or "OwnershipFailed" or "RateLimited" or "ProviderWarning" or "ProviderUnavailable")
            throw new InvalidOperationException(Text(snapshot, "state") + ": " + (Text(snapshot, "reason") ?? "Provider stopped the operation."));
    }
    private async Task WaitAsync(DateTimeOffset deadline, CancellationToken ct)
    {
        var remaining = deadline - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero) return;
        await waitForChange(remaining < TimeSpan.FromMilliseconds(350) ? remaining : TimeSpan.FromMilliseconds(350), ct);
    }
}
