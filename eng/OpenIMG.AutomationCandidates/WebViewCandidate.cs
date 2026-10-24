using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using OpenIMG.App.Browser;
using OpenIMG.Core.Enums;

namespace OpenIMG.AutomationCandidates;

internal static class WebViewCandidate
{
    public static async Task<CaseResult> RunAsync(string scenario)
    {
        var fixture = new OperationFixture();
        if (scenario == "existing-profile")
        {
            await using var priming = await OfflineWebView.StartAsync(fixture.Root, scenario);
            await priming.ExecuteAsync("localStorage.fixtureSession='authenticated'", CancellationToken.None);
        }
        await using var browser = await OfflineWebView.StartAsync(fixture.Root, scenario);
        browser.InterruptDownload = scenario == "download-interrupted";
        var client = new GptWebClient(Microsoft.Extensions.Logging.Abstractions.NullLogger<GptWebClient>.Instance);
        client.AttachWebView(browser.View);
        await browser.NavigateAsync("https://chatgpt.com/");
        var sends = 0;var downloaded = false;string? error = null;
        var generationRegression = scenario is "thinking-zero" or "thinking-long" or "thinking-stabilization" or "route-only" or "provisional-route" or "provisional-route-encoded";
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(generationRegression ? 45 : 15));
        var ct = limit.Token;
        try
        {
            await client.BindOperationAsync(fixture.Lifecycle, false, ct);
            if (scenario == "existing-profile" &&
                await browser.ExecuteAsync("localStorage.fixtureSession", ct) != "\"authenticated\"")
                throw new InvalidOperationException("Persistent WebView profile marker was not retained.");
            var readiness = await client.AssessReadinessAsync(ct);
            if (scenario == "sign-in-with-visible-composer")
            {
                if (readiness.IsReady || readiness.FailureStage != "AuthenticationRequired")
                    throw new InvalidOperationException("A visible sign-in action must block readiness even when a composer is present.");
                return new(scenario, true, false, 0, browser.Downloads, "AuthenticationRequired", null);
            }
            if (!readiness.IsReady) throw new InvalidOperationException("Fixture readiness failed: " + readiness.FailureStage);
            if (!await client.StartNewChatAsync(ct)) throw new InvalidOperationException("Fresh chat failed.");
            await fixture.Lifecycle.AdvanceAsync(ProviderOperationState.Ready, ct);
            var upload = await client.UploadSingleImageAsync(fixture.SourcePath, ct);
            if (!upload.AttachmentReady) throw new InvalidOperationException("Fixture upload failed: " + upload.FailureStage);
            await fixture.Lifecycle.AdvanceAsync(ProviderOperationState.AttachmentsUploaded, ct);
            if (!(await client.InsertAndVerifyPromptAsync(Program.Prompt, ct)).PromptContentVerified) throw new InvalidOperationException("Prompt verification failed.");
            await fixture.Lifecycle.AdvanceAsync(ProviderOperationState.PromptVerified, ct);
            await fixture.Lifecycle.AdvanceAsync(ProviderOperationState.GenerationMayExist, ct);
            var postSendNavigations = 0;
            browser.View.CoreWebView2.NavigationStarting += (_, _) => postSendNavigations++;
            var submission = await client.SubmitPromptOnceAsync(Program.Prompt, 0, 0, TimeSpan.FromSeconds(3), ct);
            sends = submission.SendClickedExactlyOnce ? 1 : 0;
            if (!submission.SubmissionAcknowledged) throw new InvalidOperationException("Submission not acknowledged.");
            if (scenario == "duplicate-send")
            {
                var sentAgain = false;
                try { sentAgain = (await client.SubmitPromptOnceAsync(Program.Prompt, 0, 0, TimeSpan.FromSeconds(1), ct)).SendClickedExactlyOnce; }
                catch (InvalidOperationException) { }
                if (sentAgain) throw new InvalidOperationException("Duplicate submission was accepted.");
            }
            if (scenario == "restart-viewer-open")
            {
                await client.WaitForCurrentResponseImageAsync(0, TimeSpan.FromSeconds(3), ct);
                await browser.ExecuteAsync("document.querySelector('[data-testid=generated-image-gallery] img').click()", ct);
            }
            if (scenario == "restart-after-generation")
            {
                var generated = await client.WaitForCurrentResponseImageAsync(0, TimeSpan.FromSeconds(3), ct);
                if (!generated.GenerationCompleted) throw new InvalidOperationException("Generation failed before restart.");
            }
            if (scenario is "restart-after-submit" or "restart-after-generation" or "app-restart" or "restart-viewer-open" or "failed-route-recovery")
            {
                browser.FailRecovery = scenario == "failed-route-recovery";
                fixture.Restore();
                await client.BindOperationAsync(fixture.Lifecycle, false, ct);
                await client.RecoverGeneratedImageAsync(fixture.Context.ConversationPathAfterSubmit!, 0, Program.Prompt,
                    fixture.Context.GenerationOperationId, fixture.Destination, TimeSpan.FromSeconds(5), ct);
            }
            else
            {
                var waiting = client.WaitForCurrentResponseImageAsync(0, TimeSpan.FromSeconds(generationRegression ? 35 : 3), ct);
                if (generationRegression)
                {
                    var observeUntil = DateTimeOffset.UtcNow.AddSeconds(scenario == "thinking-long" ? 22 : 0.8);
                    while (DateTimeOffset.UtcNow < observeUntil)
                    {
                        await Task.Delay(150, ct);
                        if (waiting.IsCompleted)
                            throw new InvalidOperationException("Generation wait ended early: " + (await waiting).FailureReason);
                        if (fixture.Context.OperationState == ProviderOperationState.NeedsRecovery || browser.Downloads != 0 || postSendNavigations != 0 ||
                            await browser.ExecuteAsync("String(sessionStorage.opens||0)", ct) != "\"0\"" ||
                            await browser.ExecuteAsync("location.pathname", ct) is not ("\"/\"" or "\"/c/operation\"" or "\"/c/local-chatgpt:fixture\"" or "\"/c/local-chatgpt%3Afixture\""))
                            throw new InvalidOperationException("Generation wait was interrupted, navigated, or opened/downloaded early.");
                    }
                    var checkpoint = JsonSerializer.Deserialize<OpenIMG.Application.Restoration.GenerationRecoveryCheckpoint>(File.ReadAllText(fixture.CheckpointPath))!;
                    if (!checkpoint.SendClicked || checkpoint.OperationId != fixture.Context.GenerationOperationId || string.IsNullOrEmpty(checkpoint.PromptSha256) || checkpoint.ConversationPath != "/c/operation")
                        throw new InvalidOperationException("Submission and late conversation binding were not checkpointed before completion.");
                    if (scenario != "route-only" && fixture.Context.OperationState != ProviderOperationState.Generating)
                        throw new InvalidOperationException("Active provider must stay Generating even without messages/images.");
                }
                var image = await waiting;
                if (!image.GenerationCompleted) throw new InvalidOperationException(image.FailureReason);
                await client.DownloadLatestGeneratedImageAsync(fixture.Context.GenerationOperationId, fixture.Destination, TimeSpan.FromSeconds(4), ct);
            }
            await fixture.VerifyFileAsync();downloaded = true;
            if (generationRegression && (postSendNavigations != 0 || await browser.ExecuteAsync("String(sessionStorage.sends)", ct) != "\"1\""))
                throw new InvalidOperationException("Generation continuation navigated or duplicated Send.");
        }
        catch (Exception ex)
        {
            error = ex.GetBaseException().Message;
            if (sends > 0) await fixture.Lifecycle.AdvanceAsync(ProviderOperationState.NeedsRecovery, CancellationToken.None);
        }
        finally { client.DetachAndCancel(); }
        var expected = Program.ExpectedDownload(scenario);
        var expectedSends = Program.ExpectedSends(scenario);
        return new(scenario, downloaded == expected && sends == expectedSends && browser.Downloads <= 1 &&
            (expected ? browser.Downloads == 1 : !File.Exists(fixture.Destination)), downloaded, sends, browser.Downloads, fixture.Context.OperationState.ToString(), error);
    }
}

internal sealed class OfflineWebView : IAsyncDisposable
{
    public WebView2 View { get; } = new();
    public int Downloads { get; private set; }
    public bool InterruptDownload { get; set; }
    public bool FailRecovery { get; set; }
    private readonly SemaphoreSlim _changes = new(0,1);
    private Window? _window;
    public static async Task<OfflineWebView> StartAsync(string root, string scenario)
    {
        var browser = new OfflineWebView();
        browser._window = new Window { Content = browser.View, Width = 950, Height = 750, Left = -20000, Top = -20000, ShowInTaskbar = false, ShowActivated = false };
        browser._window.Show();
        var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: Path.Combine(root, "webview-profile"));
        await browser.View.EnsureCoreWebView2Async(environment);
        var core = browser.View.CoreWebView2;
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += (_, args) =>
        {
            var svg = new Uri(args.Request.Uri).AbsolutePath.EndsWith(".svg", StringComparison.Ordinal);
            args.Response = environment.CreateWebResourceResponse(new MemoryStream(Encoding.UTF8.GetBytes(svg
                ? "<svg xmlns='http://www.w3.org/2000/svg' width='400' height='300'><rect width='400' height='300' fill='#176c9d'/></svg>"
                : browser.FailRecovery ? Program.Page(scenario).Replace("if(sessionStorage.prompt)render();", "history.replaceState(null,'','/');", StringComparison.Ordinal)
                    : Program.Page(scenario))), 200, "OK", "Content-Type: " + (svg ? "image/svg+xml" : "text/html"));
        };
        core.WebMessageReceived += (_, _) => { if(browser._changes.CurrentCount==0)browser._changes.Release(); };
        core.DownloadStarting += (_, args) => { browser.Downloads++;if(browser.InterruptDownload)args.DownloadOperation.Cancel(); };
        await browser.NavigateAsync("https://chatgpt.com/");return browser;
    }
    public async Task NavigateAsync(string url)
    {
        var ready = new TaskCompletionSource<bool>();
        void Completed(object? _, CoreWebView2NavigationCompletedEventArgs e) => ready.TrySetResult(e.IsSuccess);
        View.CoreWebView2.NavigationCompleted += Completed;
        try { View.CoreWebView2.Navigate(url);if(!await ready.Task.WaitAsync(TimeSpan.FromSeconds(8)))throw new IOException("Offline navigation failed."); }
        finally { View.CoreWebView2.NavigationCompleted -= Completed; }
    }
    public Task<string> ExecuteAsync(string script, CancellationToken ct) => View.CoreWebView2.ExecuteScriptAsync(script).WaitAsync(TimeSpan.FromSeconds(5), ct);
    public async Task WaitForChangeAsync(TimeSpan timeout, CancellationToken ct) => await _changes.WaitAsync(timeout, ct);
    public async Task UploadAsync(string source)
    {
        var core=View.CoreWebView2;
        using var dom=JsonDocument.Parse(await core.CallDevToolsProtocolMethodAsync("DOM.getDocument","{}"));
        using var node=JsonDocument.Parse(await core.CallDevToolsProtocolMethodAsync("DOM.querySelector",JsonSerializer.Serialize(new{nodeId=dom.RootElement.GetProperty("root").GetProperty("nodeId").GetInt32(),selector="input[type=file]"})));
        await core.CallDevToolsProtocolMethodAsync("DOM.setFileInputFiles",JsonSerializer.Serialize(new{nodeId=node.RootElement.GetProperty("nodeId").GetInt32(),files=new[]{source}}));
    }
    public ValueTask DisposeAsync(){View.Dispose();_window?.Close();return ValueTask.CompletedTask;}
}
