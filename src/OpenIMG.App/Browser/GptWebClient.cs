using System.Security.Cryptography;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using OpenIMG.Application.Abstractions.AI;
using OpenIMG.Application.Restoration;
using OpenIMG.App.Helpers;

namespace OpenIMG.App.Browser;

/// <summary>
/// ChatGPT WebView2 client hosted by OpenIMG. It exposes attachment/download
/// evidence and refuses to continue when the DOM contract is not satisfied.
/// </summary>
public sealed partial class GptWebClient : IGptWebClient
{
    private static readonly SemaphoreSlim BrowserSemaphore = new(1, 1);
    private readonly ILogger<GptWebClient> _logger;
    private WebView2? _webView;
    private Dispatcher? _webViewDispatcher;
    private string? _activeOperationId;
    private string? _pendingDownloadPath;
    private TaskCompletionSource<string?>? _downloadTcs;
    private readonly GptOperationDiagnostics _diagnostics = new();
    private GptWebReadinessResult? _lastReadiness;
    private bool _navigationHandlerAttached;
    private DateTimeOffset? _lastNavigationCompletedAt;
    private bool? _lastNavigationSucceeded;
    public event Action<string>? ResultStageChanged;

    public GptWebClient(ILogger<GptWebClient> logger) => _logger = logger;

    public void AttachWebView(WebView2 webView)
    {
        ArgumentNullException.ThrowIfNull(webView);
        if (ReferenceEquals(_webView, webView)) return;
        DetachCoreEvents();
        _webView = webView;
        _webViewDispatcher = webView.Dispatcher;
        _webView.CoreWebView2InitializationCompleted += OnCoreInitialized;
        if (_webView.CoreWebView2 != null)
            AttachCoreEvents(_webView.CoreWebView2);
    }

    public void DetachAndCancel()
    {
        _automationSession = null;
        if (_operationLifecycle is not null) _operationLifecycle.StatePersisted -= OnOperationStatePersisted;
        _operationLifecycle = null;
        _downloadTcs?.TrySetCanceled();
        _pendingDownloadPath = null;
        _activeOperationId = null;
        var webView = _webView;
        DetachCoreEvents();
        if (webView != null)
            webView.CoreWebView2InitializationCompleted -= OnCoreInitialized;
        _webView = null;
        _webViewDispatcher = null;
        _lastReadiness = null;
    }

    public async Task<IDisposable> AcquireBrowserLockAsync(CancellationToken cancellationToken = default)
    {
        await BrowserSemaphore.WaitAsync(cancellationToken);
        return new Releaser(BrowserSemaphore);
    }

    public async Task<GptWebReadinessResult> AssessReadinessAsync(CancellationToken cancellationToken = default)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(90);
        GptWebReadinessResult last = new()
        {
            FailureStage = "Activation",
            FailureReason = "GPT WebView2 has not been attached."
        };

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var state = await ReadWebViewStateAsync();
            if (!state.IsAttached)
            {
                last = new GptWebReadinessResult { FailureStage = "Activation", FailureReason = "GPT WebView2 has not been attached." };
                break;
            }

            if (!state.IsVisible || !state.IsLoaded || state.Width <= 0 || state.Height <= 0)
            {
                last = ToResult(state, "ProviderNotVisible", "GPT provider panel is not visible and rendered.");
                await Task.Delay(250, cancellationToken);
                continue;
            }

            if (!state.CoreInitialized)
            {
                last = ToResult(state, "DomUnavailable", "GPT CoreWebView2 is not initialized.");
                await Task.Delay(250, cancellationToken);
                continue;
            }

            if (GptWebNavigationPolicy.ShouldNavigateToChatGpt(state.CurrentUrl))
            {
                await NavigateToChatGptAsync(cancellationToken);
                last = ToResult(state, "Navigation", "Navigating GPT WebView2 to chatgpt.com.");
                await Task.Delay(250, cancellationToken);
                continue;
            }

            try
            {
                if (GptWebNavigationPolicy.IsChatGptUrl(state.CurrentUrl))
                    await ExecuteAsync(GptDomAdapter.HelperScript);
                var snapshot = await ReadDomSnapshotAsync(cancellationToken);
                var result = ToResult(state, snapshot);
                _lastReadiness = result;
                if (result.IsReady)
                    return result;

                // A login/OAuth page is a valid user-facing state. Return it
                // immediately and never reset it to the ChatGPT root route.
                if (string.Equals(result.FailureStage, "AuthenticationRequired", StringComparison.Ordinal) ||
                    !GptWebNavigationPolicy.IsChatGptUrl(snapshot.Url ?? state.CurrentUrl))
                    return result;

                last = result;
            }
            catch (Exception ex)
            {
                last = ToResult(state, "ScriptExecutionFailed", ex.Message);
            }

            await Task.Delay(500, cancellationToken);
        }

        _lastReadiness = last with
        {
            FailureStage = string.Equals(last.FailureStage, "Completed", StringComparison.Ordinal) ? "DomUnavailable" : last.FailureStage,
            FailureReason = last.FailureReason ?? "GPT readiness timed out before its DOM predicate completed."
        };
        return _lastReadiness;
    }

    public async Task<bool> EnsureReadyAsync(CancellationToken cancellationToken = default) =>
        (await AssessReadinessAsync(cancellationToken)).IsReady;

    // Read-only recovery of a conversation created by a prior one-click
    // operation. Never starts a chat, uploads, submits, or downloads.
    public Task<string> InspectExistingConversationAsync(string conversationPath, string screenshotPath, CancellationToken cancellationToken = default) =>
        InvokeOnWebViewDispatcherAsync(
            () => InspectExistingConversationOnUiThreadAsync(conversationPath, screenshotPath, cancellationToken),
            cancellationToken);

    private async Task<string> InspectExistingConversationOnUiThreadAsync(string conversationPath, string screenshotPath, CancellationToken cancellationToken)
    {
        if (!conversationPath.StartsWith("/c/", StringComparison.Ordinal) ||
            conversationPath.Contains('?') || conversationPath.Contains('#') ||
            conversationPath.Any(char.IsWhiteSpace))
            throw new ArgumentException("Expected a plain ChatGPT conversation path.", nameof(conversationPath));
        if (!(await AssessReadinessAsync(cancellationToken)).IsReady)
            throw new InvalidOperationException("GPT WebView2 is not ready for read-only conversation inspection.");
        await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            _webView!.CoreWebView2!.Navigate("https://chatgpt.com" + conversationPath));
        await System.Windows.Application.Current.Dispatcher.InvokeAsync(() => _webView!.Focus());
        var deadline = DateTimeOffset.UtcNow.AddSeconds(60);
        string? snapshot = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await InjectAsync();
            snapshot = DecodeJavaScriptString(await ExecuteAsync(@"JSON.stringify((function(){
              var messages=Array.from(document.querySelectorAll('[data-message-author-role]'));
              var images=Array.from(document.querySelectorAll('main img')).filter(function(i){var r=i.getBoundingClientRect();return r.width>120&&r.height>120;});
              var media=Array.from(document.querySelectorAll('main img,main canvas,main video,main picture,main [role=img],main [style*=background-image]'));
              var controls=Array.from(document.querySelectorAll('main button,main a')).map(function(el){return {tag:el.tagName,aria:el.getAttribute('aria-label')||'',title:el.getAttribute('title')||'',testId:el.getAttribute('data-testid')||'',text:(el.innerText||'').trim().slice(0,60)};}).filter(function(c){return c.aria||c.title||c.testId||c.text;});
              var editImage=Array.from(document.querySelectorAll('button')).find(function(b){return /editar imagen generada/i.test(b.getAttribute('aria-label')||'');});
              var imageCardAncestors=[];for(var p=editImage,n=0;p&&n<8;p=p.parentElement,n++){var r=p.getBoundingClientRect();imageCardAncestors.push({tag:p.tagName,role:p.getAttribute('role')||'',testId:p.getAttribute('data-testid')||'',className:typeof p.className==='string'?p.className.slice(0,100):'',textLength:(p.innerText||'').length,images:p.querySelectorAll('img').length,canvases:p.querySelectorAll('canvas').length,iframes:p.querySelectorAll('iframe').length,width:r.width,height:r.height});}
              var gallery=editImage?.closest('[data-testid=generated-image-gallery]');
              var galleryNodes=gallery?Array.from(gallery.querySelectorAll('*')).slice(0,35).map(function(el){var r=el.getBoundingClientRect(),bg=getComputedStyle(el).backgroundImage;return {tag:el.tagName,role:el.getAttribute('role')||'',testId:el.getAttribute('data-testid')||'',className:typeof el.className==='string'?el.className.slice(0,80):'',styleLength:(el.getAttribute('style')||'').length,backgroundImage:!!bg&&bg!=='none',width:r.width,height:r.height};}):[];
              var galleryButtons=gallery?Array.from(gallery.querySelectorAll('button')).map(function(b){return {aria:b.getAttribute('aria-label')||'',title:b.getAttribute('title')||'',text:(b.innerText||'').trim().slice(0,50)};}):[];
              return {path:location.pathname,readyState:document.readyState,bodyTextLength:document.body?(document.body.innerText||'').length:0,
                messages:messages.map(function(m){return {role:m.getAttribute('data-message-author-role'),textLength:(m.innerText||'').length,imageCount:m.querySelectorAll('img').length};}),
                conversationTurnCount:document.querySelectorAll('[data-testid*=conversation-turn i]').length,
                generatedGalleryCount:document.querySelectorAll('[data-testid=generated-image-gallery]').length,
                galleryImageCount:document.querySelectorAll('[data-testid=generated-image-gallery] img').length,
                galleryLoadedImageCount:Array.from(document.querySelectorAll('[data-testid=generated-image-gallery] img')).filter(function(i){return i.naturalWidth>120&&i.naturalHeight>120;}).length,
                galleryPlaceholderCount:document.querySelectorAll('[data-testid=generated-image-gallery] [class*=Placeholder]').length,
                largeImageCount:images.length,
                largeImages:images.map(function(i){var src=i.getAttribute('src')||'';return {width:i.naturalWidth,height:i.naturalHeight,alt:i.alt||'',srcScheme:src.split(':')[0],parentRole:i.closest('[data-message-author-role]')?.getAttribute('data-message-author-role')||null};}),
                media:media.slice(0,30).map(function(el){var r=el.getBoundingClientRect();return {tag:el.tagName,role:el.getAttribute('role')||'',testId:el.getAttribute('data-testid')||'',className:typeof el.className==='string'?el.className.slice(0,100):'',width:r.width,height:r.height,visible:r.width>0&&r.height>0};}),
                controls:controls.slice(-35),
                imageCardAncestors:imageCardAncestors,
                galleryNodes:galleryNodes,
                galleryButtons:galleryButtons,
                responseState:window.__openimg_gpt.currentAssistantImageState(0),
                alertCount:document.querySelectorAll('[role=alert],[data-sonner-toast]').length};
            })())"));
            using var document = JsonDocument.Parse(snapshot);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                await Task.Delay(250, cancellationToken);
                continue;
            }
            if (root.GetProperty("generatedGalleryCount").GetInt32() == 1 &&
                root.GetProperty("galleryImageCount").GetInt32() == 0)
                await ExecuteAsync("document.querySelector('[data-testid=generated-image-gallery]')?.scrollIntoView({block:'center'})");
            if (root.GetProperty("path").GetString() == conversationPath &&
                root.GetProperty("readyState").GetString() == "complete" &&
                root.GetProperty("generatedGalleryCount").GetInt32() == 1 &&
                root.GetProperty("galleryLoadedImageCount").GetInt32() == 1)
            {
                await using var stream = File.Create(screenshotPath);
                await _webView!.CoreWebView2!.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream);
                return snapshot;
            }
            await Task.Delay(250, cancellationToken);
        }
        await using (var stream = File.Create(screenshotPath))
            await _webView!.CoreWebView2!.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream);
        return snapshot ?? "{}";
    }

    public Task<string> InspectExistingGeneratedImageViewerAsync(string conversationPath, string screenshotPath, CancellationToken cancellationToken = default) =>
        InvokeOnWebViewDispatcherAsync(
            () => InspectExistingGeneratedImageViewerOnUiThreadAsync(conversationPath, screenshotPath, cancellationToken),
            cancellationToken);

    private async Task<string> InspectExistingGeneratedImageViewerOnUiThreadAsync(string conversationPath, string screenshotPath, CancellationToken cancellationToken)
    {
        var conversation = await InspectExistingConversationAsync(conversationPath, screenshotPath, cancellationToken);
        using (var document = JsonDocument.Parse(conversation))
        {
            var root = document.RootElement;
            if (root.GetProperty("generatedGalleryCount").GetInt32() != 1 || root.GetProperty("galleryImageCount").GetInt32() != 1)
                throw new InvalidOperationException("Existing conversation does not contain exactly one loaded generated image.");
        }
        var targetJson = DecodeJavaScriptString(await ExecuteAsync(@"JSON.stringify((function(){var b=document.querySelector('[data-testid=generated-image-gallery] button[aria-label^=""Imagen generada""]');if(!b)return null;var r=b.getBoundingClientRect();return {x:r.x+r.width/2,y:r.y+r.height/2};})())"));
        using var targetDocument = JsonDocument.Parse(targetJson);
        if (targetDocument.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("Generated-image viewer control was unavailable.");
        var x = targetDocument.RootElement.GetProperty("x").GetDouble();
        var y = targetDocument.RootElement.GetProperty("y").GetDouble();
        var core = _webView!.CoreWebView2!;
        await core.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", JsonSerializer.Serialize(new { type = "mousePressed", x, y, button = "left", clickCount = 1 }));
        await core.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", JsonSerializer.Serialize(new { type = "mouseReleased", x, y, button = "left", clickCount = 1 }));
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        string? snapshot = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            snapshot = DecodeJavaScriptString(await ExecuteAsync(GptDomAdapter.DownloadControlSnapshotScript));
            if (GptDownloadControlPolicy.SelectSnapshot(snapshot) is not null) break;
            await Task.Delay(200, cancellationToken);
        }
        await using (var stream = File.Create(screenshotPath))
            await _webView!.CoreWebView2!.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream);
        return snapshot ?? "{}";
    }

    public Task<string> RecoverExistingGeneratedImageAsync(string conversationPath, string operationId, string destinationPath, CancellationToken cancellationToken = default) =>
        InvokeOnWebViewDispatcherAsync(
            () => RecoverExistingGeneratedImageOnUiThreadAsync(conversationPath, operationId, destinationPath, cancellationToken),
            cancellationToken);

    private async Task<string> RecoverExistingGeneratedImageOnUiThreadAsync(string conversationPath, string operationId, string destinationPath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        var absoluteDestinationPath = Path.GetFullPath(destinationPath);
        if (File.Exists(absoluteDestinationPath))
            throw new IOException("Recovery destination already exists; the prior candidate must not be overwritten.");
        Directory.CreateDirectory(Path.GetDirectoryName(absoluteDestinationPath)!);
        var viewerSnapshotPath = Path.Combine(Path.GetDirectoryName(absoluteDestinationPath)!, "recovery_viewer.png");
        using var browserLock = await AcquireBrowserLockAsync(cancellationToken);
        var viewer = await InspectExistingGeneratedImageViewerAsync(conversationPath, viewerSnapshotPath, cancellationToken);
        if (GptDownloadControlPolicy.SelectSnapshot(viewer) is null)
            throw new InvalidOperationException("Existing generated image viewer did not expose one unambiguous visible download control.");
        _activeOperationId = operationId;
        _pendingDownloadPath = absoluteDestinationPath;
        _downloadTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var downloadControl = await FindVisibleDownloadControlAsync(cancellationToken)
                ?? throw new InvalidOperationException("The active generated-image viewer no longer has one unambiguous visible download control.");
            var core = _webView!.CoreWebView2!;
            await ClickDownloadControlAsync(core, downloadControl, cancellationToken);
            var completed = await Task.WhenAny(_downloadTcs.Task, Task.Delay(TimeSpan.FromSeconds(45), cancellationToken));
            if (completed != _downloadTcs.Task)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw new TimeoutException("Owned download did not complete within the bounded recovery window.");
            }
            var downloaded = await _downloadTcs.Task;
            if (!string.Equals(downloaded, absoluteDestinationPath, StringComparison.OrdinalIgnoreCase) || !File.Exists(absoluteDestinationPath))
                throw new InvalidOperationException("Owned download was not saved at the scoped operation path.");
            return absoluteDestinationPath;
        }
        finally
        {
            _pendingDownloadPath = null;
            _downloadTcs = null;
            _activeOperationId = null;
        }
    }

    public async Task<bool> StartNewChatAsync(CancellationToken cancellationToken = default)
    {
        _diagnostics.Reset("StartingConversation");
        _automationSession = null;
        await InjectAsync();
        var result = await ExecuteAsync("(function(){var b=window.__openimg_gpt.findNewChatButton(); if(!b) return false; b.click(); return true;})();");
        if (!IsTrue(result))
        {
            // Some layouts hide the explicit new-chat button. Navigating to
            // the provider's root route is the normal browser equivalent of
            // starting a new empty conversation; no DOM nodes are removed.
            await NavigateToChatGptAsync(cancellationToken);
        }
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var composer = await ExecuteAsync("!!window.__openimg_gpt.findComposer()");
            var atConversationRoot = await ExecuteAsync("window.location.pathname === '/'");
            var attachments = await GetAttachmentCountAsync(cancellationToken);
            if (IsTrue(composer) && IsTrue(atConversationRoot) && attachments == 0) return true;
            await Task.Delay(250, cancellationToken);
        }
        return false;
    }

    public async Task<int> GetAttachmentCountAsync(CancellationToken cancellationToken = default)
    {
        await InjectAsync();
        var raw = await ExecuteAsync("window.__openimg_gpt.countAttachments()");
        return int.TryParse(raw.Trim('"'), out var count) ? count : 0;
    }

    public async Task<IReadOnlyList<string>> GetAttachmentDiagnosticsAsync(CancellationToken cancellationToken = default)
    {
        await InjectAsync();
        var raw = await ExecuteAsync("JSON.stringify(window.__openimg_gpt.attachmentDiagnostics())");
        try
        {
            using var doc = JsonDocument.Parse(DecodeJavaScriptString(raw));
            return doc.RootElement.EnumerateArray().Select(element => element.ToString()).ToArray();
        }
        catch { return Array.Empty<string>(); }
    }

    public async Task<bool> UploadImagesAsync(IReadOnlyList<string> imagePaths, CancellationToken cancellationToken = default)
    {
        if (imagePaths is null || imagePaths.Count != 2) return false;
        var result = await UploadImagesSequentiallyAsync(imagePaths[0], imagePaths[1], cancellationToken);
        return result.Attachment1?.AttachmentReady == true && result.Attachment1.ObservedAttachmentCount == 1 &&
            result.Attachment2?.AttachmentReady == true && result.Attachment2.ObservedAttachmentCount == 2;
    }

    public async Task<GptAttachmentUploadSpikeResult> RunSingleImageAttachmentUploadSpikeAsync(
        string imagePath,
        CancellationToken cancellationToken = default)
        => await UploadSingleImageAsync(imagePath, cancellationToken);

    public async Task<GptAttachmentUploadSpikeResult> UploadSingleImageAsync(
        string imagePath,
        CancellationToken cancellationToken = default)
        => await RunImageAttachmentUploadSpikeAsync(imagePath, expectedInitialCount: 0, cancellationToken);

    public async Task<GptTwoAttachmentUploadSpikeResult> RunTwoImageAttachmentUploadSpikeAsync(
        string originalPath,
        string geminiDraftPath,
        CancellationToken cancellationToken = default)
        => await UploadImagesSequentiallyAsync(originalPath, geminiDraftPath, cancellationToken);

    public async Task<GptTwoAttachmentUploadSpikeResult> UploadImagesSequentiallyAsync(
        string originalPath,
        string geminiDraftPath,
        CancellationToken cancellationToken = default)
    {
        var first = await RunImageAttachmentUploadSpikeAsync(originalPath, expectedInitialCount: 0, cancellationToken);
        if (!first.AttachmentReady || first.ObservedAttachmentCount != 1)
            return new GptTwoAttachmentUploadSpikeResult
            {
                ReadinessPassed = first.ReadinessPassed,
                InitialAttachmentCount = first.InitialAttachmentCount,
                Attachment1 = first,
                ObservedAttachmentCount = first.ObservedAttachmentCount,
                FailureStage = first.FailureStage,
                FailureReason = first.FailureReason ?? "The first attachment was not visibly confirmed as the only attachment."
            };

        // A second independent UI activation and chooser event resolve a fresh
        // backendNodeId; the first identifier is deliberately not reused.
        var second = await RunImageAttachmentUploadSpikeAsync(geminiDraftPath, expectedInitialCount: 1, cancellationToken);
        var success = second.AttachmentReady && second.ObservedAttachmentCount == 2;
        return new GptTwoAttachmentUploadSpikeResult
        {
            ReadinessPassed = first.ReadinessPassed && second.ReadinessPassed,
            InitialAttachmentCount = first.InitialAttachmentCount,
            Attachment1 = first,
            Attachment2 = second,
            ObservedAttachmentCount = second.ObservedAttachmentCount,
            // Attachment previews currently expose no stable filename/order
            // contract, so sequential upload is known but DOM order is not asserted.
            AttachmentOrderVerified = false,
            AttachmentOwnershipVerified = first.TestFileReadable == true && second.TestFileReadable == true &&
                first.SetFileInputFilesInvoked && second.SetFileInputFilesInvoked &&
                first.ObservedAttachmentCount == 1 && second.ObservedAttachmentCount == 2,
            FailureStage = success ? "Completed" : second.FailureStage,
            FailureReason = success ? null : second.FailureReason ?? "The second attachment was not visibly confirmed as attachment number two."
        };
    }

    private Task<GptAttachmentUploadSpikeResult> RunImageAttachmentUploadSpikeAsync(
        string imagePath,
        int expectedInitialCount,
        CancellationToken cancellationToken) =>
        InvokeOnWebViewDispatcherAsync(
            () => RunImageAttachmentUploadSpikeOnUiThreadAsync(imagePath, expectedInitialCount, cancellationToken),
            cancellationToken);

    private async Task<GptAttachmentUploadSpikeResult> RunImageAttachmentUploadSpikeOnUiThreadAsync(
        string imagePath,
        int expectedInitialCount,
        CancellationToken cancellationToken)
    {
        _diagnostics.Stage("UploadingAttachment");
        var result = new GptAttachmentUploadSpikeResult
        {
            TestFilePath = imagePath,
            TestFileExists = !string.IsNullOrWhiteSpace(imagePath) && File.Exists(imagePath),
            TestFileExtension = Path.GetExtension(imagePath ?? string.Empty)
        };

        if (string.IsNullOrWhiteSpace(imagePath) || !Path.IsPathFullyQualified(imagePath))
            return result with { FailureStage = "FileValidation", FailureReason = "Test image path must be absolute." };

        if (!File.Exists(imagePath))
            return result with { FailureStage = "FileValidation", FailureReason = "Test image does not exist." };

        var extension = Path.GetExtension(imagePath);
        if (!new[] { ".jpg", ".jpeg", ".png", ".webp" }.Contains(extension, StringComparer.OrdinalIgnoreCase))
            return result with { FailureStage = "FileValidation", FailureReason = "Test image extension is not a supported image format." };

        var fullPath = Path.GetFullPath(imagePath);
        long fileSize;
        try
        {
            await using var stream = File.OpenRead(fullPath);
            fileSize = stream.Length;
            if (fileSize <= 0) throw new InvalidDataException("Test image is empty.");
            _ = await stream.ReadAsync(new byte[1], cancellationToken);
        }
        catch (Exception ex)
        {
            return result with
            {
                TestFilePath = fullPath,
                TestFileSizeBytes = File.Exists(fullPath) ? new FileInfo(fullPath).Length : null,
                TestFileReadable = false,
                FailureStage = "FileValidation",
                FailureReason = ex.ToString()
            };
        }

        result = result with
        {
            TestFilePath = fullPath,
            TestFileExists = true,
            TestFileSizeBytes = fileSize,
            TestFileExtension = extension,
            TestFileReadable = true
        };

        var readiness = await AssessReadinessAsync(cancellationToken);
        result = result with { ReadinessPassed = readiness.IsReady };
        if (!readiness.IsReady)
            return result with { FailureStage = readiness.FailureStage, FailureReason = readiness.FailureReason };

        // Attachment inspection begins only after the shared readiness gate proved
        // an authenticated session and a visible composer.
        var initialCount = await GetAttachmentCountAsync(cancellationToken);
        result = result with { InitialAttachmentCount = initialCount };
        if (initialCount != expectedInitialCount)
            return result with { FailureStage = "PreUpload", FailureReason = $"Expected {expectedInitialCount} existing attachment(s), found {initialCount}." };

        var before = await ReadAttachmentUiSnapshotAsync(cancellationToken);
        var frames = await ReadFrameDiagnosticsAsync(cancellationToken);
        result = result with
        {
            FileInputCountBefore = before.Inputs.Count,
            FileInputsBefore = before.Inputs,
            AttachmentButtonCandidates = before.Buttons,
            FrameDiagnostics = frames
        };

        var addButton = before.Buttons
            .OrderByDescending(button => ScoreAttachmentButton(button))
            .FirstOrDefault();
        if (addButton == null || ScoreAttachmentButton(addButton) <= 0)
            return result with { FailureStage = "AttachmentUi", FailureReason = "No visible ChatGPT attachment/add button was identified." };

        var core = _webView?.CoreWebView2;
        if (core == null)
            return result with { FailureStage = "ProviderNotVisible", FailureReason = "GPT CoreWebView2 detached before opening attachment UI." };

        var fileChooserOpened = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var receiver = core.GetDevToolsProtocolEventReceiver("Page.fileChooserOpened");
        EventHandler<CoreWebView2DevToolsProtocolEventReceivedEventArgs> fileChooserHandler = (_, args) =>
            fileChooserOpened.TrySetResult(args.ParameterObjectAsJson);
        receiver.DevToolsProtocolEventReceived += fileChooserHandler;

        var interceptionEnabled = false;
        try
        {
            await core.CallDevToolsProtocolMethodAsync("Page.enable", "{}");
            await core.CallDevToolsProtocolMethodAsync("Page.setInterceptFileChooserDialog", "{\"enabled\":true}");
            interceptionEnabled = true;

            // Dispatch a real pointer click at the visible ChatGPT control. This
            // activates its ordinary menu/file-input flow without creating DOM.
            await core.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", JsonSerializer.Serialize(new
            {
                type = "mousePressed", x = addButton.X + addButton.Width / 2,
                y = addButton.Y + addButton.Height / 2, button = "left", clickCount = 1
            }));
            await core.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", JsonSerializer.Serialize(new
            {
                type = "mouseReleased", x = addButton.X + addButton.Width / 2,
                y = addButton.Y + addButton.Height / 2, button = "left", clickCount = 1
            }));

            var afterButtonClick = await ReadAttachmentUiSnapshotAsync(cancellationToken);
            // ChatGPT renders the attachment menu asynchronously. The click can
            // be accepted before the upload choice exists in the DOM.
            var menuDeadline = DateTimeOffset.UtcNow.AddSeconds(3);
            while (!fileChooserOpened.Task.IsCompleted &&
                   !afterButtonClick.MenuItems.Any(IsUploadMenuChoice) &&
                   DateTimeOffset.UtcNow < menuDeadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(100, cancellationToken);
                afterButtonClick = await ReadAttachmentUiSnapshotAsync(cancellationToken);
            }
            result = result with
            {
                AttachmentUiActivated = afterButtonClick.MenuItems.Count > 0 || afterButtonClick.Inputs.Count > before.Inputs.Count || fileChooserOpened.Task.IsCompleted,
                FileInputCountAfter = afterButtonClick.Inputs.Count,
                FileInputsAfter = afterButtonClick.Inputs
            };

            var uploadChoiceClicked = false;
            if (!fileChooserOpened.Task.IsCompleted && afterButtonClick.MenuItems.Count > 0)
            {
                var uploadChoice = afterButtonClick.MenuItems
                    .Where(IsUploadMenuChoice)
                    .OrderByDescending(ScoreUploadMenuChoice)
                    .FirstOrDefault();
                if (uploadChoice != null)
                {
                    await core.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", JsonSerializer.Serialize(new
                    {
                        type = "mousePressed", x = uploadChoice.X + uploadChoice.Width / 2,
                        y = uploadChoice.Y + uploadChoice.Height / 2, button = "left", clickCount = 1
                    }));
                    await core.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", JsonSerializer.Serialize(new
                    {
                        type = "mouseReleased", x = uploadChoice.X + uploadChoice.Width / 2,
                        y = uploadChoice.Y + uploadChoice.Height / 2, button = "left", clickCount = 1
                    }));
                    uploadChoiceClicked = true;
                }
            }

            long backendNodeId;
            string? frameId;
            string? mode;
            try
            {
                var chooserEvent = await fileChooserOpened.Task.WaitAsync(TimeSpan.FromSeconds(8), cancellationToken);
                using var chooserDocument = JsonDocument.Parse(chooserEvent);
                var chooserRoot = chooserDocument.RootElement;
                backendNodeId = chooserRoot.TryGetProperty("backendNodeId", out var backendNodeElement) ? backendNodeElement.GetInt64() : 0;
                frameId = chooserRoot.TryGetProperty("frameId", out var frameIdElement) ? frameIdElement.GetString() : null;
                mode = chooserRoot.TryGetProperty("mode", out var modeElement) ? modeElement.GetString() : null;
            }
            catch (TimeoutException)
            {
                var finalSnapshot = await ReadAttachmentUiSnapshotAsync(cancellationToken);
                var fallbackInput = SelectUniqueFallbackFileInput(finalSnapshot.Inputs);
                var inputAppearedAfterAction = finalSnapshot.Inputs.Count > before.Inputs.Count;
                if ((!uploadChoiceClicked && !inputAppearedAfterAction) || fallbackInput is null)
                {
                    return result with
                    {
                        AttachmentUiActivated = result.AttachmentUiActivated || finalSnapshot.MenuItems.Count > 0,
                        FileInputCountAfter = finalSnapshot.Inputs.Count,
                        FileInputsAfter = finalSnapshot.Inputs,
                        FailureStage = "AttachmentUi",
                        FailureReason = fallbackInput is null
                            ? "The upload menu opened, but no unique connected, enabled image input could be identified safely."
                            : "The upload action was not confirmed; OpenIMG did not assign a file to the page."
                    };
                }

                // WebView2 may omit Page.fileChooserOpened for a page-owned
                // upload input. Only continue after the actual upload menu was
                // clicked (or a new live input appeared), and only when one
                // unambiguous connected input remains. The normal preview check
                // below is still required before this upload is accepted.
                try
                {
                    backendNodeId = await ResolveFileInputBackendNodeIdAsync(core, fallbackInput, cancellationToken);
                }
                catch (Exception ex) when (ex is InvalidOperationException or JsonException)
                {
                    return result with
                    {
                        AttachmentUiActivated = true,
                        FileInputCountAfter = finalSnapshot.Inputs.Count,
                        FileInputsAfter = finalSnapshot.Inputs,
                        SelectedInputIndex = fallbackInput.Index,
                        SelectedInputAccept = fallbackInput.Accept,
                        SelectedInputMultiple = fallbackInput.Multiple,
                        SelectedInputFrame = fallbackInput.OwnerFrame,
                        FailureStage = "InputIdentification",
                        FailureReason = "The unique live upload input changed before it could be verified."
                    };
                }

                frameId = fallbackInput.OwnerFrame;
                mode = "unique-live-input-fallback";
                result = result with
                {
                    AttachmentUiActivated = true,
                    FileInputCountAfter = finalSnapshot.Inputs.Count,
                    FileInputsAfter = finalSnapshot.Inputs,
                    SelectedInputIndex = fallbackInput.Index,
                    SelectedInputAccept = fallbackInput.Accept,
                    SelectedInputMultiple = fallbackInput.Multiple,
                    SelectedInputFrame = fallbackInput.OwnerFrame,
                    NodeIdentifierType = "backendNodeId (unique live input fallback)",
                    NodeIdentifierValue = backendNodeId.ToString()
                };
                _logger.LogInformation("GPT upload used the unique image-input fallback after its chooser event timed out.");
            }

            result = result with { AttachmentUiActivated = true };
            if (backendNodeId <= 0)
                return result with { FailureStage = "InputIdentification", FailureReason = "The verified upload control did not resolve to a live browser input." };

            var descriptorJson = await core.CallDevToolsProtocolMethodAsync(
                "DOM.describeNode", JsonSerializer.Serialize(new { backendNodeId, depth = 0, pierce = true }));
            using var descriptorDocument = JsonDocument.Parse(descriptorJson);
            var node = descriptorDocument.RootElement.GetProperty("node");
            var nodeName = node.TryGetProperty("nodeName", out var nodeNameElement) ? nodeNameElement.GetString() : null;
            var attrs = ReadAttributes(node);
            var selectedAccept = attrs.TryGetValue("accept", out var accept) ? accept : null;
            var selectedMultiple = attrs.ContainsKey("multiple");
            var selectedDisabled = attrs.ContainsKey("disabled");
            var selectedType = attrs.TryGetValue("type", out var type) ? type : null;
            if (!string.Equals(nodeName, "INPUT", StringComparison.OrdinalIgnoreCase) || !string.Equals(selectedType, "file", StringComparison.OrdinalIgnoreCase))
                return result with
                {
                    AttachmentUiActivated = true,
                    SelectedInputAccept = selectedAccept,
                    SelectedInputMultiple = selectedMultiple,
                    SelectedInputFrame = frameId,
                    NodeIdentifierType = "backendNodeId",
                    NodeIdentifierValue = backendNodeId.ToString(),
                    FailureStage = "InputIdentification",
                    FailureReason = $"Live file chooser target was not an input[type=file]. nodeName={nodeName}; type={selectedType}; mode={mode}."
                };
            if (selectedDisabled)
                return result with { FailureStage = "InputIdentification", FailureReason = "The live file chooser input was disabled." };

            var after = await ReadAttachmentUiSnapshotAsync(cancellationToken);
            var matchingInput = after.Inputs.FirstOrDefault(input =>
                string.Equals(input.Accept ?? "", selectedAccept ?? "", StringComparison.OrdinalIgnoreCase) &&
                input.Multiple == selectedMultiple &&
                input.Connected && !input.Disabled);
            result = result with
            {
                FileInputCountAfter = after.Inputs.Count,
                FileInputsAfter = after.Inputs,
                SelectedInputIndex = matchingInput?.Index,
                SelectedInputAccept = selectedAccept,
                SelectedInputMultiple = selectedMultiple,
                SelectedInputFrame = string.IsNullOrWhiteSpace(frameId) ? matchingInput?.OwnerFrame : frameId,
                NodeIdentifierType = "backendNodeId",
                NodeIdentifierValue = backendNodeId.ToString()
            };

            // This is the only file-setting call in the spike. Its target comes
            // from the live chooser event and was verified with DOM.describeNode.
            string cdpResult;
            try
            {
                cdpResult = await core.CallDevToolsProtocolMethodAsync(
                    "DOM.setFileInputFiles", JsonSerializer.Serialize(new { files = new[] { fullPath }, backendNodeId }));
            }
            catch (Exception ex)
            {
                return result with
                {
                    SetFileInputFilesInvoked = true,
                    ExactCdpError = $"{ex.GetType().FullName}; HResult=0x{ex.HResult:X8}; {ex}",
                    FailureStage = "AttachmentUpload",
                    FailureReason = ex.Message
                };
            }

            result = result with { SetFileInputFilesInvoked = true, ExactCdpResult = cdpResult };
            var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
            while (DateTimeOffset.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = await GetAttachmentCountAsync(cancellationToken);
                var diagnostics = await GetAttachmentDiagnosticsAsync(cancellationToken);
                // The count routine also includes visible blob/data image previews.
                // Keep count evidence even if the detailed selector list is empty.
                var expectedCountAfterUpload = expectedInitialCount + 1;
                var previewDetected = diagnostics.Count > 0 || count == expectedCountAfterUpload;
                result = result with
                {
                    ObservedAttachmentCount = count,
                    AttachmentDiagnostics = diagnostics,
                    AttachmentPreviewDetected = previewDetected,
                    AttachmentPreviewEvidence = diagnostics.Count > 0
                        ? "A visible attachment preview element was returned by the detailed DOM diagnostics."
                        : count == 1
                            ? "ObservedAttachmentCount=1; the attachment counter includes visible attachment elements and blob/data image previews."
                            : null,
                    AttachmentReady = count == expectedCountAfterUpload && previewDetected
                };
                if (result.AttachmentReady)
                    return result with { FailureStage = "Completed" };
                if (count > expectedCountAfterUpload)
                    return result with { FailureStage = "AttachmentVerification", FailureReason = $"One upload produced {count} visible attachment previews; expected {expectedCountAfterUpload}." };
                await Task.Delay(250, cancellationToken);
            }

            return result with
            {
                FailureStage = "AttachmentVerification",
                FailureReason = "CDP accepted the file input, but one visible attachment preview did not appear before timeout."
            };
        }
        catch (Exception ex)
        {
            return result with
            {
                ExactCdpError = $"{ex.GetType().FullName}; HResult=0x{ex.HResult:X8}; {ex}",
                FailureStage = string.Equals(result.FailureStage, "NotStarted", StringComparison.Ordinal) ? "AttachmentUi" : result.FailureStage,
                FailureReason = ex.Message
            };
        }
        finally
        {
            receiver.DevToolsProtocolEventReceived -= fileChooserHandler;
            if (interceptionEnabled)
            {
                try { await core.CallDevToolsProtocolMethodAsync("Page.setInterceptFileChooserDialog", "{\"enabled\":false}"); }
                catch (Exception ex) { _logger.LogDebug(ex, "Could not disable GPT file chooser interception after the one-file spike."); }
            }
        }
    }

    public async Task<int> GetAssistantResponseCountAsync(CancellationToken cancellationToken = default)
    {
        await InjectAsync();
        var raw = await ExecuteAsync("window.__openimg_gpt.countAssistantResponses()");
        return int.TryParse(raw.Trim('"'), out var count) ? count : 0;
    }

    public async Task<int> GetUserMessageCountAsync(CancellationToken cancellationToken = default)
    {
        await InjectAsync();
        var raw = await ExecuteAsync("window.__openimg_gpt.countUserMessages()");
        return int.TryParse(raw.Trim('"'), out var count) ? count : 0;
    }

    public async Task<GptSubmissionStateResult> GetSubmissionStateAsync(string expectedPrompt, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedPrompt);
        await InjectAsync();
        var encodedPrompt = JsonSerializer.Serialize(expectedPrompt);
        var raw = await ExecuteAsync($"JSON.stringify(window.__openimg_gpt.submissionState({encodedPrompt}))");
        using var document = JsonDocument.Parse(DecodeJavaScriptString(raw));
        var root = document.RootElement;
        return new GptSubmissionStateResult
        {
            ConversationPath = GetString(root, "conversationPath") == "/" ? "/" : GenerationRecoveryCheckpoint.NormalizeConversationPath(GetString(root, "conversationPath")),
            UserMessageCount = GetInt(root, "userMessageCount"),
            LastUserMessageMatchesPrompt = GetBool(root, "lastUserMessageMatchesPrompt"),
            ComposerEmpty = GetBool(root, "composerEmpty"),
            ComposerAttachmentCount = GetInt(root, "composerAttachmentCount"),
            StopControlDetected = GetBool(root, "stopControlDetected"),
            GenerationActive = GetBool(root, "generationActive"),
            SendButtonDisabled = GetBool(root, "sendButtonDisabled"),
            Diagnostics = root.TryGetProperty("diagnostics", out var diagnostics) ? diagnostics.GetRawText() : null
        };
    }

    public async Task<int> GetGeneratedImageCountAsync(CancellationToken cancellationToken = default)
    {
        await InjectAsync();
        var raw = await ExecuteAsync("window.__openimg_gpt.countGeneratedImages()");
        return int.TryParse(raw.Trim('"'), out var count) ? count : 0;
    }

    public async Task<bool> SendPromptAsync(string prompt, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        var state = await GetSubmissionStateAsync(prompt, cancellationToken);
        var result = await SubmitPromptOnceAsync(prompt, state.UserMessageCount,
            _operationLifecycle?.Context.BaselineAssistantResponseCount ?? 0,
            TimeSpan.FromSeconds(20), cancellationToken);
        return result.SendClickedExactlyOnce && result.SubmissionAcknowledged;
    }

    private async Task<GptPromptSubmissionResult> SubmitPromptOnceCoreAsync(
        string prompt,
        int baselineUserMessageCount,
        int baselineAssistantResponseCount,
        TimeSpan acknowledgementTimeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        var verification = await InsertAndVerifyPromptAsync(prompt, cancellationToken);
        var verificationResult = new GptPromptSubmissionResult
        {
            ComposerDetected = verification.ComposerDetected,
            ComposerHasContent = verification.ComposerRawTextLength > 0,
            PromptLength = verification.CompiledPromptLength,
            CanonicalCompiledSha256 = verification.CanonicalCompiledSha256,
            CanonicalComposerSha256 = verification.CanonicalComposerSha256,
            CanonicalContentMatches = verification.CanonicalContentMatches,
            PromptContentVerified = verification.PromptContentVerified,
            InputEventObserved = verification.InputEventObserved,
            SendButtonDetected = verification.SendButtonDetected,
            SendButtonEnabled = verification.SendButtonEnabled,
            BaselineUserMessageCount = baselineUserMessageCount,
            BaselineAssistantResponseCount = baselineAssistantResponseCount
        };
        if (!verification.PromptContentVerified)
            return verificationResult with { FailureStage = verification.FailureStage, FailureReason = verification.FailureReason };
        if (!verification.SendButtonDetected || !verification.SendButtonEnabled)
            return verificationResult with { FailureStage = "SendControlUnavailable", FailureReason = "Verified prompt is present, but the normal send control is missing or disabled." };

        if (acknowledgementTimeout <= TimeSpan.Zero)
            return verificationResult with { FailureStage = "PromptSubmission", FailureReason = "Submission acknowledgement timeout must be positive." };

        var beforeClick = await GetSubmissionStateAsync(prompt, cancellationToken);
        var lifecycle = _operationLifecycle ?? throw new InvalidOperationException("OperationBindingRequired: bind a durable operation before Send.");
        if (lifecycle.Context.OperationState != OpenIMG.Core.Enums.ProviderOperationState.GenerationMayExist ||
            lifecycle.Context.PersistOperationStateAsync is null || lifecycle.Context.SendClickedExactlyOnce)
            throw new InvalidOperationException("Send requires a persisted pre-submission checkpoint and cannot be repeated.");
        var downloadSession = new GptAutomationSession(lifecycle, prompt, ExecuteForDownloadAsync, WaitForOperationDomChangeAsync);
        await downloadSession.BeginAsync(false, cancellationToken);
        _automationSession = downloadSession;
        // Exactly one click. Acknowledgement is about the user's message being
        // accepted; the generation watcher owns assistant/image start and finish.
        if (!IsTrue(await ExecuteAsync("window.__openimg_gpt.clickSend()")))
            return verificationResult with { FailureStage = "PromptSubmission", FailureReason = "GPT send control was unavailable." };
        lifecycle.Context.SendClickedExactlyOnce = true;
        lifecycle.Context.PromptSentAt = DateTimeOffset.UtcNow;
        await lifecycle.AdvanceAsync(OpenIMG.Core.Enums.ProviderOperationState.GenerationMayExist, cancellationToken);

        _diagnostics.Stage("WaitingForSubmissionAcknowledgement");
        var submittedAt = DateTimeOffset.UtcNow;
        var deadline = submittedAt + acknowledgementTimeout;
        GptSubmissionStateResult? lastState = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lastState = await GetSubmissionStateAsync(prompt, cancellationToken);
            var directUserMessageAck = lastState.UserMessageCount > baselineUserMessageCount && lastState.LastUserMessageMatchesPrompt;
            var clearedUiAck = lastState.ComposerEmpty && lastState.ComposerAttachmentCount == 0 &&
                (lastState.StopControlDetected || lastState.SendButtonDisabled);
            var newConversationAck = lastState.IndicatesNewConversationSubmissionFrom(beforeClick);
            if (lastState.GenerationActive || directUserMessageAck || clearedUiAck || newConversationAck)
            {
                var signal = lastState.GenerationActive ? "GenerationActiveAfterSend" : directUserMessageAck
                    ? "UserMessageAppearedAndMatchedPrompt"
                    : newConversationAck
                        ? "NewConversationRouteAndComposerCleared"
                        : lastState.StopControlDetected
                        ? "ComposerAndAttachmentsCleared_StopControlVisible"
                        : "ComposerAndAttachmentsCleared_SendDisabled";
                lifecycle.Context.SubmissionAcknowledgedAt = DateTimeOffset.UtcNow;
                await lifecycle.AdvanceAsync(OpenIMG.Core.Enums.ProviderOperationState.GenerationSubmitted, cancellationToken);
                await downloadSession.AcknowledgeAsync(lastState.ConversationPath, cancellationToken);
                return verificationResult with
                {
                    Submitted = true,
                    SendClickedExactlyOnce = true,
                    SubmissionAcknowledged = true,
                    SubmissionAcknowledgedAt = DateTimeOffset.UtcNow,
                    SubmissionAcknowledgementSignal = signal,
                    ConversationPathAfterSubmit = lastState.ConversationPath,
                    UserMessageCountAfterSubmit = lastState.UserMessageCount,
                    ComposerClearedAfterSubmit = lastState.ComposerEmpty,
                    AttachmentsClearedAfterSubmit = lastState.ComposerAttachmentCount == 0,
                    SubmissionDiagnostics = $"BeforeClick={beforeClick.Diagnostics};AfterClick={lastState.Diagnostics}",
                    SubmittedAt = submittedAt,
                    FailureStage = "Completed"
                };
            }
            await Task.Delay(250, cancellationToken);
        }

        lastState ??= await GetSubmissionStateAsync(prompt, cancellationToken);
        return verificationResult with
        {
            Submitted = true,
            SendClickedExactlyOnce = true,
            UserMessageCountAfterSubmit = lastState.UserMessageCount,
            ComposerClearedAfterSubmit = lastState.ComposerEmpty,
            AttachmentsClearedAfterSubmit = lastState.ComposerAttachmentCount == 0,
            SubmissionDiagnostics = $"BeforeClick={beforeClick.Diagnostics};AfterClick={lastState.Diagnostics}",
            ConversationPathAfterSubmit = lastState.ConversationPath,
            SubmittedAt = submittedAt,
            FailureStage = "PromptSubmission",
            FailureReason = "Send was clicked once, but no reliable user-submission acknowledgement appeared before the bounded timeout."
        };
    }

    public async Task<GptPromptInsertionVerificationResult> InsertAndVerifyPromptAsync(
        string compiledPrompt,
        CancellationToken cancellationToken = default)
    {
        _diagnostics.Stage("InsertingAndVerifyingPrompt");
        ArgumentException.ThrowIfNullOrWhiteSpace(compiledPrompt);
        cancellationToken.ThrowIfCancellationRequested();
        await InjectAsync();
        var encoded = JsonSerializer.Serialize(compiledPrompt);
        var raw = await ExecuteAsync($"JSON.stringify(window.__openimg_gpt.insertComposerText({encoded}))");
        using var document = JsonDocument.Parse(DecodeJavaScriptString(raw));
        var root = document.RootElement;
        var composerDetected = GetBool(root, "composerDetected");
        var promptInserted = GetBool(root, "inserted");
        var inputObserved = GetBool(root, "inputEventObserved");
        var composerRaw = GetString(root, "rawText") ?? string.Empty;
        var composerInnerText = GetString(root, "innerText") ?? string.Empty;
        var composerTextContent = GetString(root, "textContent") ?? string.Empty;
        var normalizedCompiled = GptPromptTextNormalizer.Canonicalize(compiledPrompt);
        var normalizedComposer = GptPromptTextNormalizer.Canonicalize(composerInnerText);
        var contentMatches = string.Equals(normalizedCompiled, normalizedComposer, StringComparison.Ordinal);
        var mismatch = contentMatches ? (int?)null : FindFirstMismatch(normalizedCompiled, normalizedComposer);
        var sendDetected = GetBool(root, "sendButtonDetected");
        var sendEnabled = GetBool(root, "sendButtonEnabled");
        if (composerDetected && promptInserted && inputObserved && contentMatches && (!sendDetected || !sendEnabled))
        {
            // Attachment previews and contenteditable frontend state can
            // settle asynchronously after insertion. Wait on the actual
            // verified-content + enabled-send predicate; never insert twice.
            var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
            while (DateTimeOffset.UtcNow < deadline && (!sendDetected || !sendEnabled))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var stateRaw = await ExecuteAsync(GptDomAdapter.GetPromptSubmissionStateScript(encoded));
                using var stateDocument = JsonDocument.Parse(DecodeJavaScriptString(stateRaw));
                var state = stateDocument.RootElement;
                composerDetected = GetBool(state, "composerDetected");
                contentMatches = GetBool(state, "normalizedContentMatches");
                composerInnerText = GetString(state, "composerInnerText") ?? string.Empty;
                composerTextContent = composerInnerText;
                composerRaw = composerInnerText;
                sendDetected = GetBool(state, "sendButtonDetected");
                sendEnabled = GetBool(state, "sendButtonEnabled");
                if (contentMatches && sendDetected && sendEnabled) break;
                await Task.Delay(150, cancellationToken);
            }
            normalizedComposer = GptPromptTextNormalizer.Canonicalize(composerInnerText);
            contentMatches = string.Equals(normalizedCompiled, normalizedComposer, StringComparison.Ordinal);
            mismatch = contentMatches ? null : FindFirstMismatch(normalizedCompiled, normalizedComposer);
        }
        var failureStage = !composerDetected ? "ComposerDetection" : !promptInserted || !inputObserved ? "PromptInsertion" :
            !contentMatches ? "PromptVerification" : !sendDetected || !sendEnabled ? "SendControlUnavailable" : "Completed";
        var failureReason = failureStage switch
        {
            "ComposerDetection" => "No composer was present for prompt insertion.",
            "PromptInsertion" => "The UI-compatible insertion did not complete or its input event was not observed.",
            "PromptVerification" => "Canonical composer content differs from the complete compiled prompt; nothing was submitted.",
            "SendControlUnavailable" => "Prompt content is verified, but the normal send button is missing or disabled.",
            _ => null
        };

        return new GptPromptInsertionVerificationResult
        {
            InsertionMethod = GetString(root, "method") ?? "Unknown",
            ComposerDetected = composerDetected,
            PromptInserted = promptInserted,
            InputEventObserved = inputObserved,
            CompiledPromptLength = compiledPrompt.Length,
            ComposerRawTextLength = composerRaw.Length,
            ComposerInnerTextLength = composerInnerText.Length,
            ComposerTextContentLength = composerTextContent.Length,
            ComposerInnerHtmlLength = GetInt(root, "innerHtmlLength"),
            ComposerNewlineRepresentation = DescribeNewlines(composerInnerText),
            CanonicalCompiledLength = normalizedCompiled.Length,
            CanonicalComposerLength = normalizedComposer.Length,
            CanonicalCompiledSha256 = HashText(normalizedCompiled),
            CanonicalComposerSha256 = HashText(normalizedComposer),
            CanonicalTokenCount = CountWords(normalizedCompiled),
            CanonicalContentMatches = contentMatches,
            PromptContentVerified = contentMatches && promptInserted && inputObserved,
            FirstMismatchIndex = mismatch,
            MismatchContext = mismatch.HasValue ? BuildMismatchContext(normalizedCompiled, normalizedComposer, mismatch.Value) : null,
            PrefixMatches = mismatch == null || mismatch == Math.Min(normalizedCompiled.Length, normalizedComposer.Length),
            SuffixMatches = contentMatches || HasMatchingSuffix(normalizedCompiled, normalizedComposer, mismatch ?? 0),
            CompiledLineCount = CountLines(normalizedCompiled),
            ComposerLineCount = CountLines(normalizedComposer),
            SendButtonDetected = sendDetected,
            SendButtonEnabled = sendEnabled,
            FailureStage = failureStage,
            FailureReason = failureReason
        };
    }


    public async Task<bool> WaitForGeneratedImageAsync(int baselineResponseCount, int baselineImageCount, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var responseCount = await GetAssistantResponseCountAsync(cancellationToken);
            var imageCount = await GetGeneratedImageCountAsync(cancellationToken);
            if (responseCount > baselineResponseCount && imageCount > baselineImageCount) return true;
            await Task.Delay(1000, cancellationToken);
        }
        return false;
    }


    private async Task<GptDownloadControlCandidate?> FindVisibleDownloadControlAsync(CancellationToken cancellationToken)
    {
        var raw = DecodeJavaScriptString(await ExecuteAsync(GptDomAdapter.DownloadControlSnapshotScript));
        return GptDownloadControlPolicy.SelectSnapshot(raw);
    }

    private Task<string> ExecuteForDownloadAsync(string script, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ExecuteAsync(script, cancellationToken);
    }

    private static async Task ClickDownloadControlAsync(CoreWebView2 core, GptDownloadControlCandidate control, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(control.ElementToken))
            throw new InvalidOperationException("The current GPT download control could not be safely re-resolved.");
        var clicked = await core.ExecuteScriptAsync(GptGeneratedImageDownloadSession.GetClickScript(control.ElementToken)).WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
        if (!string.Equals(clicked, "true", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The owned GPT download control changed or became unavailable before it was clicked.");
    }



    private async Task InjectAsync() => await ExecuteAsync(GptDomAdapter.HelperScript);

    private void ReportResultStage(string stage)
    {
        _diagnostics.Stage(stage);
        ResultStageChanged?.Invoke(stage);
    }

    private async Task<WebViewState> ReadWebViewStateAsync()
    {
        return await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var webView = _webView;
            return new WebViewState(
                webView != null,
                webView?.IsVisible ?? false,
                webView?.IsLoaded ?? false,
                webView?.ActualWidth ?? 0,
                webView?.ActualHeight ?? 0,
                webView?.CoreWebView2 != null,
                webView?.CoreWebView2?.Source);
        }).Task;
    }

    private async Task NavigateToChatGptAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
        {
            if (_webView?.CoreWebView2 == null) return;
            _lastNavigationSucceeded = null;
            _webView.Source = new Uri("https://chatgpt.com/");
        });
    }

    private async Task<DomSnapshot> ReadDomSnapshotAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var raw = await ExecuteAsync(GptDomAdapter.ReadinessSnapshotScript);
        var json = DecodeJavaScriptString(raw);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        return new DomSnapshot(
            root.TryGetProperty("readyState", out var readyState) ? readyState.GetString() : null,
            root.TryGetProperty("title", out var title) ? title.GetString() : null,
            root.TryGetProperty("url", out var url) ? url.GetString() : null,
            root.TryGetProperty("bodyAvailable", out var bodyAvailable) && bodyAvailable.GetBoolean(),
            root.TryGetProperty("bodyInnerTextLength", out var bodyLength) ? bodyLength.GetInt32() : 0,
            root.TryGetProperty("scriptExecutionHealthy", out var scriptHealthy) && scriptHealthy.GetBoolean(),
            root.TryGetProperty("authenticationState", out var authentication) ? authentication.GetString() ?? "Unknown" : "Unknown",
            root.TryGetProperty("composerCandidateCount", out var candidateCount) ? candidateCount.GetInt32() : 0,
            root.TryGetProperty("visibleComposerCandidateCount", out var visibleCandidateCount) ? visibleCandidateCount.GetInt32() : 0,
            root.TryGetProperty("composerDetected", out var composerDetected) && composerDetected.GetBoolean(),
            root.TryGetProperty("composerDiagnostics", out var composerDiagnostics) ? composerDiagnostics.GetString() : null,
            root.TryGetProperty("blocking", out var blocking) && blocking.GetBoolean());
    }

    private async Task<AttachmentUiSnapshot> ReadAttachmentUiSnapshotAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var raw = await ExecuteAsync(GptDomAdapter.AttachmentUiSnapshotScript);
        var json = DecodeJavaScriptString(raw);
        return JsonSerializer.Deserialize<AttachmentUiSnapshot>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? new AttachmentUiSnapshot();
    }

    private async Task<IReadOnlyList<string>> ReadFrameDiagnosticsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var raw = await InvokeOnWebViewDispatcherAsync(async () =>
        {
            var core = _webView?.CoreWebView2;
            return core == null
                ? null
                : await core.CallDevToolsProtocolMethodAsync("Page.getFrameTree", "{}");
        }, cancellationToken);
        if (raw == null) return [];
        using var document = JsonDocument.Parse(raw);
        var result = new List<string>();
        void Visit(JsonElement tree)
        {
            if (!tree.TryGetProperty("frame", out var frame)) return;
            var id = frame.TryGetProperty("id", out var idElement) ? idElement.GetString() : null;
            var url = frame.TryGetProperty("url", out var urlElement) ? SanitizeFrameUrl(urlElement.GetString()) : null;
            result.Add($"FrameId={id}; Url={url}");
            if (tree.TryGetProperty("childFrames", out var children) && children.ValueKind == JsonValueKind.Array)
                foreach (var child in children.EnumerateArray()) Visit(child);
        }
        Visit(document.RootElement.GetProperty("frameTree"));
        return result;
    }

    private static Dictionary<string, string> ReadAttributes(JsonElement node)
    {
        var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!node.TryGetProperty("attributes", out var values) || values.ValueKind != JsonValueKind.Array) return attributes;
        var items = values.EnumerateArray().Select(value => value.GetString() ?? "").ToArray();
        for (var i = 0; i + 1 < items.Length; i += 2) attributes[items[i]] = items[i + 1];
        return attributes;
    }

    private static GptFileInputDiagnostic? SelectUniqueFallbackFileInput(IReadOnlyCollection<GptFileInputDiagnostic> inputs)
    {
        var liveInputs = inputs.Where(input => input.Connected && !input.Disabled).ToArray();
        var imageInputs = liveInputs.Where(input => AcceptsImage(input.Accept)).ToArray();
        return imageInputs.Length == 1 ? imageInputs[0] : null;
    }

    private static bool AcceptsImage(string? accept) =>
        !string.IsNullOrWhiteSpace(accept) && accept.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Any(value => value.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ||
                value.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
                value.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                value.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
                value.Equals(".webp", StringComparison.OrdinalIgnoreCase) ||
                value.Equals(".bmp", StringComparison.OrdinalIgnoreCase) ||
                value.Equals(".tif", StringComparison.OrdinalIgnoreCase) ||
                value.Equals(".tiff", StringComparison.OrdinalIgnoreCase));

    private static async Task<long> ResolveFileInputBackendNodeIdAsync(
        CoreWebView2 core,
        GptFileInputDiagnostic expectedInput,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var evaluation = await core.CallDevToolsProtocolMethodAsync("Runtime.evaluate", JsonSerializer.Serialize(new
        {
            expression = GptDomAdapter.GetFileInputAtScript(expectedInput.Index),
            returnByValue = false,
            awaitPromise = false
        }));
        using var evaluationDocument = JsonDocument.Parse(evaluation);
        var evaluationRoot = evaluationDocument.RootElement;
        if (evaluationRoot.TryGetProperty("exceptionDetails", out _))
            throw new InvalidOperationException("The current page could not resolve the live file input.");
        if (!evaluationRoot.TryGetProperty("result", out var remoteObject) ||
            !remoteObject.TryGetProperty("objectId", out var objectIdElement) ||
            string.IsNullOrWhiteSpace(objectIdElement.GetString()))
            throw new InvalidOperationException("The live file input no longer exists.");

        var descriptor = await core.CallDevToolsProtocolMethodAsync("DOM.describeNode", JsonSerializer.Serialize(new
        {
            objectId = objectIdElement.GetString(),
            depth = 0,
            pierce = true
        }));
        using var descriptorDocument = JsonDocument.Parse(descriptor);
        var node = descriptorDocument.RootElement.GetProperty("node");
        var nodeName = node.TryGetProperty("nodeName", out var nodeNameElement) ? nodeNameElement.GetString() : null;
        var attributes = ReadAttributes(node);
        var type = attributes.TryGetValue("type", out var typeValue) ? typeValue : null;
        var accept = attributes.TryGetValue("accept", out var acceptValue) ? acceptValue : null;
        if (!string.Equals(nodeName, "INPUT", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(type, "file", StringComparison.OrdinalIgnoreCase) ||
            attributes.ContainsKey("disabled") ||
            !string.Equals(accept, expectedInput.Accept, StringComparison.OrdinalIgnoreCase) ||
            !node.TryGetProperty("backendNodeId", out var backendNodeIdElement) ||
            !backendNodeIdElement.TryGetInt64(out var backendNodeId) || backendNodeId <= 0)
            throw new InvalidOperationException("The current DOM node does not match the unique enabled file input observed in the page.");

        return backendNodeId;
    }

    private static int ScoreAttachmentButton(GptAttachmentButtonDiagnostic button)
    {
        var label = string.Join(" ", button.AriaLabel, button.Title, button.Text).ToLowerInvariant();
        if (label.Contains("adjuntar") || label.Contains("attach") || label.Contains("añadir archivos") || label.Contains("add files")) return 5;
        if (label.Contains("archivo") || label.Contains("file") || label.Contains("upload") || label.Contains("subir")) return 3;
        if (label.Contains("foto") || label.Contains("imagen") || label.Contains("photo") || label.Contains("image")) return 2;
        return 0;
    }

    private static bool IsUploadMenuChoice(GptAttachmentButtonDiagnostic item)
    {
        var label = string.Join(" ", item.AriaLabel, item.Title, item.Text);
        return System.Text.RegularExpressions.Regex.IsMatch(label, "upload|subir|cargar|choose file|select file|computer|equipo|ordenador|desde el dispositivo|archivo", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    private static int ScoreUploadMenuChoice(GptAttachmentButtonDiagnostic item)
    {
        var label = string.Join(" ", item.AriaLabel, item.Title, item.Text).ToLowerInvariant();
        if (label.Contains("computer") || label.Contains("equipo") || label.Contains("ordenador") || label.Contains("device")) return 5;
        if (label.Contains("upload") || label.Contains("subir") || label.Contains("cargar")) return 3;
        return 1;
    }

    private static string SanitizeFrameUrl(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return "unknown";
        return uri.GetLeftPart(UriPartial.Path);
    }

    private static bool GetBool(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
    private static int GetInt(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : 0;
    private static string? GetString(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static string HashText(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static int CountLines(string value) => string.IsNullOrEmpty(value) ? 0 : value.Count(character => character == '\n') + 1;

    private static int CountWords(string value) => string.IsNullOrWhiteSpace(value)
        ? 0
        : value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

    private static int FindFirstMismatch(string expected, string actual)
    {
        var sharedLength = Math.Min(expected.Length, actual.Length);
        for (var index = 0; index < sharedLength; index++)
            if (expected[index] != actual[index]) return index;
        return sharedLength;
    }

    private static bool HasMatchingSuffix(string expected, string actual, int mismatchIndex)
    {
        var suffixLength = Math.Min(24, Math.Min(expected.Length, actual.Length));
        return suffixLength > 0 && expected[^suffixLength..].SequenceEqual(actual[^suffixLength..]);
    }

    private static string BuildMismatchContext(string expected, string actual, int mismatchIndex)
    {
        const int radius = 28;
        var expectedStart = Math.Max(0, mismatchIndex - radius);
        var actualStart = Math.Max(0, mismatchIndex - radius);
        var expectedLength = Math.Min(radius * 2, expected.Length - expectedStart);
        var actualLength = Math.Min(radius * 2, actual.Length - actualStart);
        static string Escape(string value) => value.Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal).Replace("\t", "\\t", StringComparison.Ordinal);
        return $"expected='{Escape(expected.Substring(expectedStart, expectedLength))}'; actual='{Escape(actual.Substring(actualStart, actualLength))}'";
    }

    private static string DescribeNewlines(string value)
    {
        if (value.Contains("\r\n", StringComparison.Ordinal)) return "CRLF";
        if (value.Contains('\r')) return "CR";
        return value.Contains('\n') ? "LF" : "None";
    }

    private static string DecodeJavaScriptString(string raw)
    {
        using var document = JsonDocument.Parse(raw);
        return document.RootElement.ValueKind == JsonValueKind.String
            ? document.RootElement.GetString() ?? "{}"
            : document.RootElement.GetRawText();
    }

    private static GptWebReadinessResult ToResult(WebViewState state, string failureStage, string failureReason) => new()
    {
        PanelActivated = state.IsVisible,
        IsVisible = state.IsVisible,
        ActualWidth = state.Width,
        ActualHeight = state.Height,
        WebViewLoaded = state.IsLoaded,
        CoreWebView2Initialized = state.CoreInitialized,
        NavigationSucceeded = IsChatGptUrl(state.CurrentUrl),
        CurrentUrl = state.CurrentUrl,
        FailureStage = failureStage,
        FailureReason = failureReason
    };

    private static GptWebReadinessResult ToResult(WebViewState state, DomSnapshot snapshot)
    {
        var stage = DetermineFailureStage(state, snapshot, out var reason);
        return new GptWebReadinessResult
        {
            PanelActivated = state.IsVisible,
            IsVisible = state.IsVisible,
            ActualWidth = state.Width,
            ActualHeight = state.Height,
            WebViewLoaded = state.IsLoaded,
            CoreWebView2Initialized = state.CoreInitialized,
            NavigationSucceeded = IsChatGptUrl(snapshot.Url ?? state.CurrentUrl) && string.Equals(snapshot.ReadyState, "complete", StringComparison.OrdinalIgnoreCase),
            CurrentUrl = snapshot.Url ?? state.CurrentUrl,
            DocumentReadyState = snapshot.ReadyState,
            DocumentTitle = snapshot.Title,
            BodyAvailable = snapshot.BodyAvailable,
            BodyInnerTextLength = snapshot.BodyInnerTextLength,
            ScriptExecutionHealthy = snapshot.ScriptExecutionHealthy,
            AuthenticationState = GptWebNavigationPolicy.IsAuthenticationUrl(snapshot.Url ?? state.CurrentUrl)
                ? "AuthenticationRequired"
                : snapshot.AuthenticationState,
            ComposerCandidateCount = snapshot.ComposerCandidateCount,
            VisibleComposerCandidateCount = snapshot.VisibleComposerCandidateCount,
            ComposerDetected = snapshot.ComposerDetected,
            ComposerDiagnostics = snapshot.ComposerDiagnostics,
            FailureStage = stage,
            FailureReason = reason
        };
    }

    private static string DetermineFailureStage(WebViewState state, DomSnapshot snapshot, out string? reason)
    {
        if (!state.IsVisible || !state.IsLoaded || state.Width <= 0 || state.Height <= 0)
        {
            reason = "GPT provider panel is not visible and rendered.";
            return "ProviderNotVisible";
        }
        if (!state.CoreInitialized)
        {
            reason = "GPT CoreWebView2 is not initialized.";
            return "NavigationFailed";
        }
        if (GptWebNavigationPolicy.IsAuthenticationUrl(snapshot.Url ?? state.CurrentUrl) ||
            string.Equals(snapshot.AuthenticationState, "AuthenticationRequired", StringComparison.Ordinal))
        {
            reason = "A GPT sign-in or identity-provider page is open; complete authentication in the visible browser panel.";
            return "AuthenticationRequired";
        }
        if (!snapshot.ScriptExecutionHealthy || !snapshot.BodyAvailable)
        {
            reason = "GPT DOM script sanity checks did not complete.";
            return "ScriptExecutionFailed";
        }
        if (!IsChatGptUrl(snapshot.Url ?? state.CurrentUrl) || !string.Equals(snapshot.ReadyState, "complete", StringComparison.OrdinalIgnoreCase))
        {
            reason = "GPT navigation or document readiness has not completed.";
            return "NavigationFailed";
        }
        if (!snapshot.ComposerDetected)
        {
            reason = "No visible GPT composer was detected.";
            return "ComposerUnavailable";
        }
        if (snapshot.Blocking)
        {
            reason = "GPT reported a visible provider block or rate limit.";
            return "DomUnavailable";
        }
        reason = null;
        return "Completed";
    }

    private static bool IsChatGptUrl(string? url) =>
        GptWebNavigationPolicy.IsChatGptUrl(url);

    private async Task<string> ExecuteAsync(string script, CancellationToken cancellationToken = default)
    {
        return await InvokeOnWebViewDispatcherAsync(async () =>
        {
            var core = _webView?.CoreWebView2;
            if (core == null)
                throw new InvalidOperationException("GPT WebView2 is not initialized.");
            return await core.ExecuteScriptAsync(script).WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
        }, cancellationToken).WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
    }

    private async Task<TResult> InvokeOnWebViewDispatcherAsync<TResult>(
        Func<Task<TResult>> operation,
        CancellationToken cancellationToken = default)
    {
        var dispatcher = _webViewDispatcher;
        if (dispatcher == null)
            throw new InvalidOperationException("GPT WebView2 is not attached to its UI dispatcher.");

        cancellationToken.ThrowIfCancellationRequested();
        if (dispatcher.CheckAccess())
            return await operation();

        return await dispatcher
            .InvokeAsync(operation, DispatcherPriority.Normal, cancellationToken)
            .Task
            .Unwrap();
    }

    private void OnCoreInitialized(object? sender, CoreWebView2InitializationCompletedEventArgs args)
    {
        if (!args.IsSuccess || _webView?.CoreWebView2 == null) return;
        AttachCoreEvents(_webView.CoreWebView2);
    }

    private void AttachCoreEvents(CoreWebView2 core)
    {
        if (_navigationHandlerAttached) return;
        core.DownloadStarting += OnDownloadStarting;
        core.WebMessageReceived += OnOperationDomMessage;
        core.SourceChanged += OnOperationSourceChanged;
        core.NavigationCompleted += OnNavigationCompleted;
        _navigationHandlerAttached = true;
    }

    private void DetachCoreEvents()
    {
        if (!_navigationHandlerAttached || _webView?.CoreWebView2 == null) return;
        _webView.CoreWebView2.DownloadStarting -= OnDownloadStarting;
        _webView.CoreWebView2.WebMessageReceived -= OnOperationDomMessage;
        _webView.CoreWebView2.SourceChanged -= OnOperationSourceChanged;
        _webView.CoreWebView2.NavigationCompleted -= OnNavigationCompleted;
        _navigationHandlerAttached = false;
    }

    private async void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs navigation)
    {
        _lastNavigationCompletedAt = DateTimeOffset.UtcNow;
        _lastNavigationSucceeded = navigation.IsSuccess;
        if (navigation.IsSuccess)
        {
            try { await InjectAsync(); }
            catch (Exception ex) { _logger.LogDebug(ex, "GPT helper injection will be retried by readiness polling."); }
        }
    }

    private void OnDownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs args)
    {
        var path = _pendingDownloadPath;
        var tcs = _downloadTcs;
        var operationId = _activeOperationId;
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(operationId) || tcs is null)
        {
            args.Cancel = true;
            args.Handled = true;
            return;
        }
        args.ResultFilePath = path;
        args.Handled = true;
        var download = args.DownloadOperation;
        if (Interlocked.Increment(ref _nativeDownloadSeen) != 1)
        {
            args.Cancel = true;
            tcs.TrySetException(new IOException("An additional native download was rejected for this operation."));
            return;
        }
        _diagnostics.NativeDownload(download.State.ToString());
        _logger.LogInformation("[{OperationId}] Native GPT download started.", operationId);
        download.StateChanged += (_, _) =>
        {
            if (ReferenceEquals(_downloadTcs, tcs))
                _diagnostics.NativeDownload(download.State == CoreWebView2DownloadState.Interrupted ? "Interrupted/" + download.InterruptReason : download.State.ToString());
            _logger.LogInformation("[{OperationId}] Native GPT download state {State}; reason {Reason}.", operationId, download.State, download.InterruptReason);
            CompleteDownloadTask(download, path, tcs);
        };
        CompleteDownloadTask(download, path, tcs);
    }

    private static void CompleteDownloadTask(
        CoreWebView2DownloadOperation download,
        string destinationPath,
        TaskCompletionSource<string?> completion)
    {
        if (download.State == CoreWebView2DownloadState.Completed)
            completion.TrySetResult(destinationPath);
        else if (download.State == CoreWebView2DownloadState.Interrupted)
            completion.TrySetException(new IOException("WebView2 interrupted the operation-owned image download: " + download.InterruptReason));
    }

    private static bool IsTrue(string raw) => string.Equals(raw.Trim(), "true", StringComparison.OrdinalIgnoreCase);

    private sealed record WebViewState(
        bool IsAttached,
        bool IsVisible,
        bool IsLoaded,
        double Width,
        double Height,
        bool CoreInitialized,
        string? CurrentUrl);

    private sealed record DomSnapshot(
        string? ReadyState,
        string? Title,
        string? Url,
        bool BodyAvailable,
        int BodyInnerTextLength,
        bool ScriptExecutionHealthy,
        string AuthenticationState,
        int ComposerCandidateCount,
        int VisibleComposerCandidateCount,
        bool ComposerDetected,
        string? ComposerDiagnostics,
        bool Blocking);

    private sealed record AttachmentUiSnapshot
    {
        public string? Url { get; init; }
        public List<GptAttachmentButtonDiagnostic> Buttons { get; init; } = [];
        public List<GptFileInputDiagnostic> Inputs { get; init; } = [];
        public List<GptAttachmentButtonDiagnostic> MenuItems { get; init; } = [];
    }

    private sealed class Releaser(SemaphoreSlim semaphore) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) semaphore.Release();
        }
    }
}
