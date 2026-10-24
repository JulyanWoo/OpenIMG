using System.Text.Json;
using OpenIMG.Application.Restoration;

namespace OpenIMG.App.Browser;

/// <summary>Locates an owned response option on the current page without reloading the conversation.</summary>
internal sealed class GptGeneratedImageDownloadSession(string? conversationPath, int baselineResponseCount, string prompt)
{
    private readonly string _claimId = Guid.NewGuid().ToString("N");
    private bool _conversationViewRestoreAttempted;
    public string? ConversationPath { get; private set; } = NormalizeOrRetainRejectedPath(conversationPath);
    public int CandidateCount { get; private set; }
    public int BaselineResponseCount => baselineResponseCount;
    public bool ConversationViewRestoreAttempted => _conversationViewRestoreAttempted;
    public GptGeneratedImageViewerGate Viewer { get; } = new();
    public bool DownloadOwnershipVerified { get; private set; }
    public bool ConversationRecoveryRequired { get; set; }
    public bool ViewerBoundDownloadCandidate { get; private set; }

    public void BindSubmittedConversation(string? path) => ConversationPath = NormalizeOrRetainRejectedPath(path);

    private static string? NormalizeOrRetainRejectedPath(string? path) => GenerationRecoveryCheckpoint.NormalizeConversationPath(path)
        ?? (path?.StartsWith("/c/", StringComparison.Ordinal) == true ? path : null);

    public Task ObserveViewerAsync(Func<string, CancellationToken, Task<string>> execute, CancellationToken ct) =>
        Viewer.ObserveAndOpenAsync(execute, ConversationPath, baselineResponseCount, prompt, ct);

    public async Task<GptDownloadControlCandidate?> FindLiveViewerDownloadControlAsync(
        Func<string, CancellationToken, Task<string>> execute, CancellationToken cancellationToken)
    {
        if (!Viewer.CanAuthorizeCurrentViewerDownload ||
            ConversationPath is not null && !GenerationRecoveryCheckpoint.IsConversationPath(ConversationPath)) return null;
        using var snapshot = JsonDocument.Parse(Decode(await execute(Viewer.GetViewerDownloadSnapshotScript(), cancellationToken)));
        return snapshot.RootElement.GetProperty("state").GetString() == "Ready"
            ? GptDownloadControlPolicy.SelectSnapshot(snapshot.RootElement.GetProperty("controls").GetRawText())
            : null;
    }

    public async Task<bool> RestoreConversationViewIfNeededAsync(
        Func<string, CancellationToken, Task<string>> execute,
        Func<string, CancellationToken, Task> navigate,
        CancellationToken cancellationToken)
    {
        var raw = await execute("JSON.stringify((" + ConversationViewFunction + ")(" +
            JsonSerializer.Serialize(ConversationPath) + "," + baselineResponseCount + "," +
            JsonSerializer.Serialize(GptPromptTextNormalizer.Canonicalize(prompt)) + "," + (Viewer.Opened ? "true" : "false") + "))", cancellationToken);
        using var snapshot = JsonDocument.Parse(Decode(raw));
        var state = snapshot.RootElement.GetProperty("state").GetString();
        if (state == "ProviderBlocked")
            throw new InvalidOperationException("ChatGPT displayed a provider warning or usage limit. Conversation recovery was stopped.");
        if (state == "OwnershipFailed")
            throw new InvalidOperationException("OwnershipFailed: the visible conversation does not match the submitted GPT operation. No image was downloaded or prompt resubmitted.");
        if (state != "ImageOnlyView") return false;
        if (!GenerationRecoveryCheckpoint.IsConversationPath(ConversationPath))
            throw new InvalidOperationException($"OwnershipFailed: InvalidSavedConversationRoute ({GptOperationDiagnostics.ConversationRouteValidationReason(ConversationPath)}). No navigation, download or new submission was attempted. Manual recovery is required.");
        if (_conversationViewRestoreAttempted)
            throw new InvalidOperationException("OwnershipFailed: the saved GPT conversation still exposes only an image view after recovery. The operation remains recoverable; no prompt was resubmitted.");

        // A gallery in a modal is not ownership evidence. Return to the saved
        // operation, then let the normal user-turn/image checks establish it.
        _conversationViewRestoreAttempted = true;
        await navigate(ConversationPath!, cancellationToken);
        return true;
    }

    public async Task<GptDownloadControlCandidate> PrepareAsync(
        Func<string, CancellationToken, Task<string>> execute,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        Action<string>? stageChanged = null,
        Func<string, CancellationToken, Task>? restoreConversation = null)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        async Task<string> RunScriptAsync(string script)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero) throw new TimeoutException("GPT download DOM inspection reached its preparation deadline.");
            return await execute(script, cancellationToken).WaitAsync(remaining, cancellationToken);
        }
        var lastState = "ImagePending";
        string? previousState = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await RunScriptAsync(GptDomAdapter.HelperScript);
            var viewerControl = await FindLiveViewerDownloadControlAsync((script, _) => RunScriptAsync(script), cancellationToken);
            if (viewerControl is not null)
            {
                ViewerBoundDownloadCandidate = true;
                DownloadOwnershipVerified = true;
                CandidateCount = Viewer.CandidateCount;
                return viewerControl;
            }
            if (restoreConversation is not null && await RestoreConversationViewIfNeededAsync(
                (script, _) => RunScriptAsync(script), async (path, ct) =>
                {
                    stageChanged?.Invoke("RestoringSubmittedConversation");
                    var remaining = deadline - DateTimeOffset.UtcNow;
                    if (remaining <= TimeSpan.Zero) throw new TimeoutException("GPT conversation recovery reached the download deadline.");
                    await restoreConversation(path, ct).WaitAsync(remaining, ct);
                }, cancellationToken)) continue;
            using var snapshot = JsonDocument.Parse(Decode(await RunScriptAsync(GetCaptureScript())));
            var root = snapshot.RootElement;
            lastState = root.GetProperty("state").GetString() ?? "ImagePending";
            if (previousState != lastState) stageChanged?.Invoke("DownloadPreparation/" + lastState);
            previousState = lastState;
            if (lastState == "ProviderBlocked")
                throw new InvalidOperationException("ChatGPT displayed a provider warning or usage limit. The operation remains recoverable and the queue will pause.");
            if (lastState == "OwnershipFailed")
                throw new InvalidOperationException("The visible conversation/user turn does not match the submitted GPT operation. No image was downloaded.");
            if (lastState == "Ready")
            {
                DownloadOwnershipVerified = true;
                ConversationPath ??= root.GetProperty("path").GetString();
                CandidateCount = root.GetProperty("candidateCount").GetInt32();
                var direct = GptDownloadControlPolicy.SelectSnapshot(root.GetProperty("controls").GetRawText());
                if (direct is not null)
                    return direct;

                // Open only the selected option. The first completed option in
                // DOM order is deterministic; no quality preference is inferred.
                stageChanged?.Invoke("OpeningGeneratedImageViewer");
                var opened = await RunScriptAsync(GetOpenScript());
                if (!string.Equals(opened.Trim(), "true", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("The owned generated image changed before opening its download controls.");
                while (DateTimeOffset.UtcNow < deadline)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var currentPath = Decode(await RunScriptAsync("window.__openimg_gpt.canonicalConversationPath(location.pathname)"));
                    if (!string.Equals(currentPath, ConversationPath, StringComparison.Ordinal))
                        throw new InvalidOperationException("The GPT conversation changed while preparing the owned download.");
                    stageChanged?.Invoke("WaitingForDownloadControl");
                    var controls = Decode(await RunScriptAsync(GptDomAdapter.DownloadControlSnapshotScript));
                    var control = GptDownloadControlPolicy.SelectSnapshot(controls);
                    if (control is not null)
                        return control;
                    await Task.Delay(200, cancellationToken);
                }
                lastState = "DownloadControlUnavailable";
                break;
            }
            await Task.Delay(250, cancellationToken);
        }
        throw new TimeoutException($"GPT download preparation timed out ({lastState}). Restore the conversation and use Reintentar foto to recover this operation without another submission.");
    }

    public string GetOwnedClickScript(string elementToken) => ViewerBoundDownloadCandidate
        ? Viewer.GetViewerBoundClickScript(elementToken)
        : GetClickScript(elementToken);

    public string GetOwnershipDiagnosticScript() => "JSON.stringify((function(path,expected,canonicalPath){var users=Array.from(document.querySelectorAll('[data-message-author-role=\\\"user\\\"]'));var last=users.length?users[users.length-1]:null;var actual=(last?(last.innerText||last.textContent):'').normalize('NFKC').replace(/[\\u200B\\u200C\\u200D\\uFEFF]/g,'').replace(/\\s+/gu,' ').trim();var canonical=canonicalPath(path);return {expectedConversationKnown:!!path,conversationMatches:path?!!canonical&&canonicalPath(location.pathname)===canonical:null,userMessageCount:users.length,lastUserPromptMatches:!!last&&actual===expected,lastUserCanonicalLength:actual.length,expectedPromptCanonicalLength:expected.length};})("
        + JsonSerializer.Serialize(ConversationPath) + "," + JsonSerializer.Serialize(GptPromptTextNormalizer.Canonicalize(prompt)) + "," + GptDomAdapter.CanonicalConversationPathFunction + "))";

    private string GetCaptureScript() =>
        "JSON.stringify((" + CaptureFunction + ")(" +
        JsonSerializer.Serialize(ConversationPath) + "," + baselineResponseCount + "," +
        JsonSerializer.Serialize(GptPromptTextNormalizer.Canonicalize(prompt)) + "," + JsonSerializer.Serialize(_claimId) + "))";

    private string GetOpenScript() => "(function(){var c=window.__openimgDownloadClaim;if(!c || c.id!==" +
        JsonSerializer.Serialize(_claimId) + ")return false;if(location.pathname!==c.path || !c.image.isConnected || (c.image.currentSrc||c.image.src)!==c.source)return false;c.image.scrollIntoView({block:'center'});c.image.click();return true;})()";

    public static string GetClickScript(string elementToken) => "(function(token){"
        + "var matches=Array.from(document.querySelectorAll('button,a,[role=\\\"button\\\"]')).filter(function(el){return el.__openimgDownloadControlToken===token;});"
        + "if(matches.length!==1)return false;var el=matches[0],r=el.getBoundingClientRect(),s=getComputedStyle(el),hit=document.elementFromPoint(r.x+r.width/2,r.y+r.height/2);"
        + "var label=[el.getAttribute('aria-label'),el.title,el.innerText,el.getAttribute('data-testid')].filter(Boolean).join(' ');"
        + "if(!el.isConnected||el.disabled||el.getAttribute('aria-disabled')==='true'||r.width<=0||r.height<=0||s.display==='none'||s.visibility==='hidden'||Number(s.opacity)<=0||!hit||!(hit===el||el.contains(hit))||!/download|descargar|guardar imagen|save image/i.test(label))return false;"
        + "el.click();return true;})(" + JsonSerializer.Serialize(elementToken) + ")";

    private static string Decode(string raw)
    {
        using var document = JsonDocument.Parse(raw);
        return document.RootElement.ValueKind == JsonValueKind.String ? document.RootElement.GetString() ?? "" : raw;
    }

    private const string ConversationViewFunction = """
    function(expectedPath,baseline,expectedPrompt,viewerOpened) {
      var provider=window.__openimg_gpt.currentAssistantImageState(baseline);
      if(provider.rateLimited || provider.generationUnavailable || provider.providerUnavailable || provider.providerWarning) return {state:'ProviderBlocked'};
      var users=Array.from(document.querySelectorAll('[data-message-author-role="user"]'));
      if(users.length) {
        var actual=(users[users.length-1].innerText||users[users.length-1].textContent||'').normalize('NFKC').replace(/[\u200B\u200C\u200D\uFEFF]/g,'').replace(/\s+/gu,' ').trim();
        return {state:(expectedPath && window.__openimg_gpt.canonicalConversationPath(location.pathname)!==expectedPath) || (baseline===0 && users.length!==1) || actual!==expectedPrompt ? 'OwnershipFailed' : 'Conversation'};
      }
      var gallery=document.querySelector('[data-testid="generated-image-gallery"]');
      var dialog=document.querySelector('[role="dialog"]');
      var assistants=document.querySelector('[data-message-author-role="assistant"]');
      var loaded=gallery && Array.from(gallery.querySelectorAll('img')).some(function(img){var r=img.getBoundingClientRect();return img.complete && img.naturalWidth>120 && img.naturalHeight>120 && r.width>0 && r.height>0;});
      if(!loaded && viewerOpened && dialog) loaded=Array.from(dialog.querySelectorAll('img')).some(function(img){var r=img.getBoundingClientRect();return img.complete&&img.naturalWidth>120&&img.naturalHeight>120&&r.width>120&&r.height>120;});
      return {state:loaded && dialog && !assistants && !provider.generationActive ? 'ImageOnlyView' : 'Pending'};
    }
    """;

    private const string CaptureFunction = """
    function(expectedPath,baseline,expectedPrompt,claimId) {
      function canonical(text){return (text||'').normalize('NFKC').replace(/[\u200B\u200C\u200D\uFEFF]/g,'').replace(/\s+/gu,' ').trim();}
      var canonicalPath=window.__openimg_gpt.canonicalConversationPath(location.pathname);
      if(expectedPath && canonicalPath!==expectedPath) return {state:'OwnershipFailed'};
      if(!canonicalPath) return {state:'ConversationUnavailable'};
      var provider=window.__openimg_gpt.currentAssistantImageState(baseline);
      if(provider.rateLimited || provider.generationUnavailable || provider.providerUnavailable || provider.providerWarning) return {state:'ProviderBlocked'};
      var users=Array.from(document.querySelectorAll('[data-message-author-role="user"]'));
      if(!users.length) return {state:'ConversationUnavailable'};
      if((baseline===0 && users.length!==1) || canonical(users[users.length-1].innerText||users[users.length-1].textContent)!==expectedPrompt)
        return {state:'OwnershipFailed'};
      var images=window.__openimg_gpt.currentAssistantImages(baseline);
      if(!images.length) return {state:'ImagePending'};
      var image=images[0];
      window.__openimgDownloadClaim={id:claimId,path:location.pathname,image:image,source:image.currentSrc||image.src};
      image.scrollIntoView({block:'center'});
      var card=image.closest('[data-testid="generated-image-gallery"],figure');
      var controls=[];
      if(card && card.querySelectorAll('img').length===1) {
        controls=Array.from(card.querySelectorAll('button,a,[role="button"]')).filter(function(el){
          return /download|descargar|guardar imagen|save image/i.test([el.getAttribute('aria-label'),el.title,el.innerText,el.getAttribute('data-testid')].filter(Boolean).join(' '));
        }).map(function(el){var r=el.getBoundingClientRect(),s=getComputedStyle(el),hit=document.elementFromPoint(r.x+r.width/2,r.y+r.height/2);if(!el.__openimgDownloadControlToken)el.__openimgDownloadControlToken=(window.crypto&&crypto.randomUUID)?crypto.randomUUID():'openimg-'+Date.now()+'-'+Math.random().toString(36).slice(2);return {
          ariaLabel:el.getAttribute('aria-label'),title:el.title,text:(el.innerText||'').slice(0,80),testId:el.getAttribute('data-testid'),
          elementToken:el.__openimgDownloadControlToken,visible:r.width>0&&r.height>0&&s.visibility!=='hidden'&&s.display!=='none'&&Number(s.opacity)>0&&!!hit&&(hit===el||el.contains(hit)),
          inDialog:!!el.closest('[role="dialog"]'),x:r.x+r.width/2,y:r.y+r.height/2};});
      }
      return {state:'Ready',path:canonicalPath,candidateCount:images.length,selectedIndex:0,controls:controls};
    }
    """;
}
