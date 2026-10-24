using System.Text.Json;
using OpenIMG.Application.Restoration;

namespace OpenIMG.App.Browser;

/// <summary>Binds a new gallery and its exact viewer image to the live submission baseline.</summary>
internal sealed class GptGeneratedImageViewerGate
{
    private readonly string _id = Guid.NewGuid().ToString("N");
    private bool _baselineCaptured;
    public bool CandidateDetected { get; private set; }
    public int CandidateCount { get; private set; }
    public bool CandidateStable { get; private set; }
    public bool OpenAttempted { get; private set; }
    public bool Opened { get; private set; }
    public DateTimeOffset? OpenAttemptedAt { get; private set; }
    public bool CanAuthorizeCurrentViewerDownload => _baselineCaptured && CandidateDetected && CandidateStable && Opened && CandidateCount is > 0 and <= 2;

    public async Task CaptureBaselineAsync(Func<string, CancellationToken, Task<string>> execute, CancellationToken ct)
    {
        var result = await execute("(function(id){window.__openimgViewerGate={id:id,baseline:new Set(document.querySelectorAll('[data-testid=\"generated-image-gallery\"]')),samples:0};return true;})(" + JsonSerializer.Serialize(_id) + ")", ct);
        if (result.Trim() != "true") throw new InvalidOperationException("GPT gallery baseline could not be captured before submission.");
        _baselineCaptured = true;
    }

    public string GetViewerDownloadSnapshotScript() => "JSON.stringify((" + ViewerDownloadSnapshot + ")(" + JsonSerializer.Serialize(_id) + "))";

    public string GetViewerBoundClickScript(string elementToken) => """
        (function(id,token,probe){
          var snapshot=probe(id);
          var visible=snapshot.controls.filter(function(c){return c.visible;});
          if(snapshot.state!=='Ready'||visible.length!==1||visible[0].elementToken!==token)return false;
          var matches=Array.from(document.querySelectorAll('button,a,[role="button"]')).filter(function(el){return el.__openimgDownloadControlToken===token;});
          if(matches.length!==1)return false;
          matches[0].click();return true;
        })(
        """ + JsonSerializer.Serialize(_id) + "," + JsonSerializer.Serialize(elementToken) + "," + ViewerDownloadSnapshot + ")";

    public async Task ObserveAndOpenAsync(Func<string, CancellationToken, Task<string>> execute,
        string? expectedPath, int baseline, string prompt, CancellationToken ct)
    {
        var script = "JSON.stringify((" + Probe + ")(" + JsonSerializer.Serialize(_id) + "," +
            JsonSerializer.Serialize(GenerationRecoveryCheckpoint.NormalizeConversationPath(expectedPath)) + "," +
            baseline + "," + JsonSerializer.Serialize(GptPromptTextNormalizer.Canonicalize(prompt)) + "))";
        using var snapshot = JsonDocument.Parse(Decode(await execute(script, ct)));
        var root = snapshot.RootElement;
        CandidateDetected |= root.GetProperty("detected").GetBoolean();
        if (root.GetProperty("count").GetInt32() > 0) CandidateCount = root.GetProperty("count").GetInt32();
        CandidateStable = root.GetProperty("stable").GetBoolean() || OpenAttempted;
        Opened |= root.GetProperty("opened").GetBoolean();
        if (!root.GetProperty("eligible").GetBoolean() || !root.GetProperty("stable").GetBoolean() || OpenAttempted) return;
        OpenAttempted = true;
        OpenAttemptedAt = DateTimeOffset.UtcNow;
        var clicked = await execute("(function(id){var c=window.__openimgViewerGate;if(!c||c.id!==id||!c.image||!c.image.isConnected||location.pathname!==c.path||(c.image.currentSrc||c.image.src)!==c.source)return false;c.image.scrollIntoView({block:'center'});c.openedByGate=true;c.image.click();return true;})(" + JsonSerializer.Serialize(_id) + ")", ct);
        if (clicked.Trim() != "true") throw new InvalidOperationException("The generated viewer candidate changed before its normal UI click.");
    }

    private static string Decode(string raw)
    {
        using var document = JsonDocument.Parse(raw);
        return document.RootElement.ValueKind == JsonValueKind.String ? document.RootElement.GetString() ?? "{}" : raw;
    }

    private const string Probe = """
    function(id,expectedPath,baseline,expectedPrompt) {
      var result={detected:false,count:0,stable:false,eligible:false,opened:false};
      var provider=window.__openimg_gpt.currentAssistantImageState(baseline);
      if(provider.rateLimited||provider.providerWarning||provider.providerUnavailable||provider.generationUnavailable||provider.generationActive) return result;
      var c=window.__openimgViewerGate;
      if(!c||c.id!==id) c=window.__openimgViewerGate={id:id,baseline:null,samples:0};
      function visible(el){var r=el.getBoundingClientRect(),s=getComputedStyle(el);return r.width>0&&r.height>0&&s.display!=='none'&&s.visibility!=='hidden';}
      var users=Array.from(document.querySelectorAll('[data-message-author-role="user"]'));
      var actual=users.length?(users[users.length-1].innerText||users[users.length-1].textContent||'').normalize('NFKC').replace(/[\u200B\u200C\u200D\uFEFF]/g,'').replace(/\s+/gu,' ').trim():null;
      var canonicalPath=window.__openimg_gpt.canonicalConversationPath(location.pathname);
      if(users.length && (actual!==expectedPrompt || (expectedPath && canonicalPath!==expectedPath))) return result;
      if(!c.openedByGate)c.conversationVerified=!expectedPath||canonicalPath===expectedPath;
      // Recovery has no in-memory gallery baseline: require the saved route and
      // a fresh-turn baseline. Never infer ownership from this weaker gate.
      if(!c.baseline && (!expectedPath || canonicalPath!==expectedPath || baseline!==0)) return result;
      if(c.image) {
        var controls=Array.from(document.querySelectorAll('button,a,[role="button"]')).filter(function(el){return visible(el)&&/download|descargar|guardar imagen|save image/i.test([el.getAttribute('aria-label'),el.title,el.innerText,el.getAttribute('data-testid')].filter(Boolean).join(' '));});
        var viewerImages=Array.from(document.querySelectorAll('[role="dialog"] img')).filter(function(img){var r=img.getBoundingClientRect();return img.complete&&img.naturalWidth>120&&img.naturalHeight>120&&r.width>120&&r.height>120&&visible(img)&&(img.currentSrc||img.src)===c.source;});
        result.opened=viewerImages.length>0 && controls.length>0;
        if(result.opened&&c.openedByGate&&c.viewerPath===undefined)c.viewerPath=location.pathname;
      }
      var galleries=Array.from(document.querySelectorAll('[data-testid="generated-image-gallery"]')).filter(function(g){return !g.closest('[data-message-author-role="user"],form') && (!c.baseline||!c.baseline.has(g));});
      var seen=new Set(),images=[];
      galleries.forEach(function(g){Array.from(g.querySelectorAll('img')).forEach(function(img){var source=img.currentSrc||img.src||'',r=img.getBoundingClientRect();if(source&&!seen.has(source)&&img.complete&&img.naturalWidth>120&&img.naturalHeight>120&&r.width>120&&r.height>120&&visible(img)&&!img.closest('[data-message-author-role="user"],form')){seen.add(source);images.push(img);}});});
      result.detected=images.length>0;result.count=images.length;
      if(!images.length||images.length>2) return result;
      var image=images[0],source=image.currentSrc||image.src;
      var same=c.image===image&&c.source===source&&c.width===image.naturalWidth&&c.height===image.naturalHeight&&c.path===location.pathname;
      c.samples=same?c.samples+1:1;c.image=image;c.source=source;c.width=image.naturalWidth;c.height=image.naturalHeight;c.path=location.pathname;c.expectedPrompt=expectedPrompt;
      result.stable=c.samples>=2;result.eligible=true;
      return result;
    }
    """;

    private const string ViewerDownloadSnapshot = """
    function(id) {
      var result={state:'Unowned',controls:[]};
      var c=window.__openimgViewerGate;
      if(!c||c.id!==id||!(c.baseline instanceof Set)||!c.openedByGate||!c.conversationVerified||c.viewerPath!==location.pathname||c.samples<2||!c.image||!c.image.isConnected)return result;
      var gallery=c.image.closest('[data-testid="generated-image-gallery"]');
      if(!gallery||c.baseline.has(gallery)||(c.image.currentSrc||c.image.src)!==c.source||c.image.naturalWidth!==c.width||c.image.naturalHeight!==c.height)return result;
      var users=Array.from(document.querySelectorAll('[data-message-author-role="user"]'));
      if(users.length&&(users[users.length-1].innerText||users[users.length-1].textContent||'').normalize('NFKC').replace(/[\u200B\u200C\u200D\uFEFF]/g,'').replace(/\s+/gu,' ').trim()!==c.expectedPrompt)return result;
      var provider=window.__openimg_gpt.currentAssistantImageState(0);
      if(provider.rateLimited||provider.providerWarning||provider.providerUnavailable||provider.generationUnavailable||provider.generationActive)return result;
      function visible(el){var r=el.getBoundingClientRect(),s=getComputedStyle(el);return r.width>0&&r.height>0&&s.display!=='none'&&s.visibility!=='hidden'&&Number(s.opacity)>0;}
      var dialogs=Array.from(document.querySelectorAll('[role="dialog"]')).filter(visible);
      var viewerImages=dialogs.flatMap(function(d){return Array.from(d.querySelectorAll('img')).filter(function(img){var r=img.getBoundingClientRect();return img.complete&&img.naturalWidth>120&&img.naturalHeight>120&&r.width>120&&r.height>120&&visible(img)&&(img.currentSrc||img.src)===c.source;});});
      if(viewerImages.length!==1)return result;
      var dialog=viewerImages[0].closest('[role="dialog"]');
      var controls=Array.from(dialog.querySelectorAll('button,a,[role="button"]')).filter(function(el){return /download|descargar|guardar imagen|save image/i.test([el.getAttribute('aria-label'),el.title,el.innerText,el.getAttribute('data-testid')].filter(Boolean).join(' '));}).map(function(el){var r=el.getBoundingClientRect(),hit=document.elementFromPoint(r.x+r.width/2,r.y+r.height/2);if(!el.__openimgDownloadControlToken)el.__openimgDownloadControlToken=(window.crypto&&crypto.randomUUID)?crypto.randomUUID():'openimg-'+Date.now()+'-'+Math.random().toString(36).slice(2);return {ariaLabel:el.getAttribute('aria-label'),title:el.title,text:(el.innerText||'').slice(0,80),testId:el.getAttribute('data-testid'),elementToken:el.__openimgDownloadControlToken,visible:!el.disabled&&el.getAttribute('aria-disabled')!=='true'&&visible(el)&&!!hit&&(hit===el||el.contains(hit)),inDialog:true,x:r.x+r.width/2,y:r.y+r.height/2};});
      return {state:'Ready',controls:controls};
    }
    """;
}
