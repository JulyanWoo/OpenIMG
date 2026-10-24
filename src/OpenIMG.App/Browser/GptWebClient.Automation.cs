using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Web.WebView2.Core;
using OpenIMG.App.Helpers;
using OpenIMG.Application.Abstractions.AI;
using OpenIMG.Application.Restoration;
using OpenIMG.Core.Enums;

namespace OpenIMG.App.Browser;

public sealed partial class GptWebClient
{
    private GptOperationLifecycle? _operationLifecycle;
    private GptAutomationSession? _automationSession;
    private readonly SemaphoreSlim _operationChanges = new(0, 1);
    private bool _providerTraceEnabled;
    private int _nativeDownloadSeen;
    private readonly SemaphoreSlim _submissionGate = new(1, 1);

    public async Task<GptPromptSubmissionResult> SubmitPromptOnceAsync(string prompt, int baselineUserMessageCount,
        int baselineAssistantResponseCount, TimeSpan acknowledgementTimeout, CancellationToken cancellationToken = default)
    {
        await _submissionGate.WaitAsync(cancellationToken);
        try { return await SubmitPromptOnceCoreAsync(prompt, baselineUserMessageCount, baselineAssistantResponseCount, acknowledgementTimeout, cancellationToken); }
        finally { _submissionGate.Release(); }
    }

    public Task BindOperationAsync(GptOperationLifecycle lifecycle, bool traceEnabled = false, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_operationLifecycle is not null) _operationLifecycle.StatePersisted -= OnOperationStatePersisted;
        _operationLifecycle = lifecycle;
        _automationSession = null;
        _providerTraceEnabled = traceEnabled;
        lifecycle.StatePersisted += OnOperationStatePersisted;
        _diagnostics.Reset(lifecycle.Context.OperationState.ToString());
        return Task.CompletedTask;
    }

    private void OnOperationStatePersisted(ProviderOperationState state)
    {
        ReportResultStage(state.ToString());
        if (_providerTraceEnabled && _operationLifecycle is { } operation)
            _logger.LogInformation("GPT trace: {OperationId} state {State}; source {SourceSha256}; prompt {PromptSha256}; conversation fingerprint {ConversationFingerprint}; candidate fingerprint {CandidateFingerprint}.",
                operation.Context.GenerationOperationId, state, operation.Context.ExpectedSourceSha256, operation.PromptSha256,
                operation.Context.ConversationPathAfterSubmit is { } route ? GptOperationLifecycle.Hash(route) : null,
                operation.Context.CandidateEvidence?.Fingerprint);
    }

    private void OnOperationDomMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        try
        {
            using var message = JsonDocument.Parse(args.WebMessageAsJson);
            if (message.RootElement.TryGetProperty("type", out var kind) && kind.GetString() == "openimg-operation-dom" &&
                message.RootElement.TryGetProperty("token", out var token) && _automationSession?.MatchesSignal(token.GetString()) == true &&
                _operationChanges.CurrentCount == 0)
                _operationChanges.Release();
        }
        catch (JsonException) { }
    }
    private void OnOperationSourceChanged(object? sender, CoreWebView2SourceChangedEventArgs args)
    {
        if (_operationChanges.CurrentCount == 0) _operationChanges.Release();
        if (_providerTraceEnabled && _operationLifecycle is { } operation)
            _logger.LogInformation("GPT trace: {OperationId} browser source changed; new document {IsNewDocument}.",
                operation.Context.GenerationOperationId, args.IsNewDocument);
    }
    private async Task WaitForOperationDomChangeAsync(TimeSpan timeout, CancellationToken ct) =>
        await _operationChanges.WaitAsync(timeout, ct);

    public async Task<GptCurrentResponseImageResult> WaitForCurrentResponseImageAsync(int baselineResponseCount, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var operation = _operationLifecycle ?? throw new InvalidOperationException("OperationBindingRequired");
        var session = _automationSession ?? throw new InvalidOperationException("SubmissionCheckpointBug: no session after Send.");
        try
        {
            await session.WaitForOwnedCandidateAsync(timeout, cancellationToken);
            operation.Context.GenerationStartedAt ??= operation.Context.PromptSentAt;
            operation.Context.GenerationCompletedAt = DateTimeOffset.UtcNow;
            return new()
            {
                GenerationStarted = true, GenerationCompleted = true, GeneratedImageDetected = true,
                CurrentAssistantResponseOwned = true, GenerationStartedAt = operation.Context.GenerationStartedAt,
                GenerationCompletedAt = operation.Context.GenerationCompletedAt, AssistantResponseCount = baselineResponseCount + 1,
                CurrentResponseImageCount = session.CandidateCount, FailureStage = "Completed"
            };
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or IOException)
        {
            await operation.AdvanceAsync(ProviderOperationState.NeedsRecovery, CancellationToken.None);
            return new()
            {
                GenerationStarted = operation.Context.SendClickedExactlyOnce, GeneratedImageDetected = session.CandidateOwned,
                GenerationStartedAt = operation.Context.PromptSentAt, CurrentAssistantResponseOwned = false,
                FailureStage = ex.Message.StartsWith("RateLimited", StringComparison.Ordinal) ? "RateLimited" : "RecoveryRequired",
                FailureReason = ex.Message
            };
        }
    }

    public Task<string?> DownloadLatestGeneratedImageAsync(string operationId, string destinationPath, TimeSpan timeout, CancellationToken cancellationToken = default) =>
        InvokeOnWebViewDispatcherAsync(() => DownloadLatestGeneratedImageOnUiThreadAsync(operationId, destinationPath, timeout, cancellationToken), cancellationToken);

    public Task<string?> RecoverGeneratedImageAsync(string conversationPath, int baselineResponseCount, string prompt, string operationId,
        string destinationPath, TimeSpan timeout, CancellationToken cancellationToken = default) =>
        InvokeOnWebViewDispatcherAsync(async () =>
        {
            var lifecycle = _operationLifecycle ?? throw new InvalidOperationException("OperationBindingRequired");
            var canonical = GenerationRecoveryCheckpoint.NormalizeConversationPath(conversationPath);
            if (canonical is null || canonical != GenerationRecoveryCheckpoint.NormalizeConversationPath(lifecycle.Context.ConversationPathAfterSubmit))
                throw new InvalidOperationException("Recovery requires the operation's durable conversation identity.");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(timeout);
            await NavigateToOwnedConversationAsync(canonical, timeout, deadline.Token);
            var session = new GptAutomationSession(lifecycle, prompt, ExecuteForDownloadAsync, WaitForOperationDomChangeAsync);
            _automationSession = session;
            await session.BeginAsync(true, deadline.Token);
            await session.WaitForOwnedCandidateAsync(timeout, deadline.Token);
            return await DownloadLatestGeneratedImageOnUiThreadAsync(operationId, destinationPath, timeout, deadline.Token);
        }, cancellationToken);

    private Task NavigateToOwnedConversationAsync(string path, TimeSpan timeout, CancellationToken ct) =>
        InvokeOnWebViewDispatcherAsync(async () =>
        {
            var canonical = GenerationRecoveryCheckpoint.NormalizeConversationPath(path)
                ?? throw new InvalidOperationException("Invalid saved conversation identity.");
            var core = _webView?.CoreWebView2 ?? throw new InvalidOperationException("GPT browser is unavailable.");
            var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnCompleted(object? _, CoreWebView2NavigationCompletedEventArgs args) => completed.TrySetResult(args.IsSuccess);
            core.NavigationCompleted += OnCompleted;
            try
            {
                core.Navigate("https://chatgpt.com" + canonical);
                var bound = timeout < TimeSpan.FromSeconds(15) ? timeout : TimeSpan.FromSeconds(15);
                if (bound <= TimeSpan.Zero || !await completed.Task.WaitAsync(bound, ct)) throw new IOException("Conversation navigation failed.");
                var current = DecodeJavaScriptString(await ExecuteAsync("location.pathname", ct));
                if (GenerationRecoveryCheckpoint.NormalizeConversationPath(current) != canonical)
                    throw new InvalidOperationException("SavedConversationUnavailable: recovery returned a different page.");
            }
            finally { core.NavigationCompleted -= OnCompleted; }
            return true;
        }, ct);

    private async Task<string?> DownloadLatestGeneratedImageOnUiThreadAsync(string operationId, string destinationPath, TimeSpan timeout, CancellationToken ct)
    {
        var lifecycle = _operationLifecycle ?? throw new InvalidOperationException("OperationBindingRequired");
        var session = _automationSession ?? throw new InvalidOperationException("An owned operation is required.");
        if (operationId != lifecycle.Context.GenerationOperationId) throw new InvalidOperationException("Download operation identity mismatch.");
        var absolute = Path.GetFullPath(destinationPath);
        if (File.Exists(absolute) || string.Equals(absolute, lifecycle.Context.ExpectedSourcePath, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The candidate destination cannot overwrite an existing file or the source.");
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        var token = await session.PrepareDownloadAsync(timeout, deadline.Token);
        var pending = absolute + ".pending-" + Guid.NewGuid().ToString("N");
        _activeOperationId = operationId;
        _pendingDownloadPath = pending;
        _downloadTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _nativeDownloadSeen = 0;
        try
        {
            await session.ClickDownloadAsync(token, deadline.Token);
            var completed = await _downloadTcs.Task.WaitAsync(deadline.Token);
            if (completed != pending || !File.Exists(pending) || new FileInfo(pending).Length == 0)
                throw new IOException("Native completion did not produce the operation's image.");
            File.Move(pending, absolute, overwrite: false);
            return absolute;
        }
        catch
        {
            await lifecycle.AdvanceAsync(ProviderOperationState.NeedsRecovery, CancellationToken.None);
            throw;
        }
        finally { _pendingDownloadPath = null;_downloadTcs = null;_activeOperationId = null; }
    }

    public async Task<string> GetOperationDiagnosticsJsonAsync(CancellationToken cancellationToken = default)
    {
        JsonElement? semantic = null;
        string? failure = null;
        try
        {
            if (_automationSession is not null)
            {
                using var document = JsonDocument.Parse(DecodeJavaScriptString(await _automationSession.GetSummaryAsync(cancellationToken)));
                semantic = document.RootElement.Clone();
            }
        }
        catch (Exception ex) { failure = ex.GetType().Name; }
        var context = _operationLifecycle?.Context;
        return JsonSerializer.Serialize(new
        {
            version = OpenImgBuildIdentity.DisplayedVersion, build = OpenImgBuildIdentity.Snapshot(), capturedAt = DateTimeOffset.UtcNow,
            backend = "WebView2/stabilization-v1",
            operation = new { operationId = context?.GenerationOperationId, stage = context?.OperationState.ToString() ?? "Idle", candidateOwned = _automationSession?.CandidateOwned ?? false,
                viewerOpened = _automationSession?.ViewerOpened ?? false, transport = _diagnostics.Snapshot() },
            semantic, probeFailure = failure,
            savedConversationRoute = GptOperationDiagnostics.DescribeConversationRoute(context?.ConversationPathAfterSubmit)
        }, new JsonSerializerOptions { WriteIndented = true });
    }
}
