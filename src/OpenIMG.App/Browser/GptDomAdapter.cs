namespace OpenIMG.App.Browser;

/// <summary>
/// GPT-specific DOM contract. Selectors are isolated here so the application
/// workflow never depends on ChatGPT markup.
/// </summary>
internal static partial class GptDomAdapter
{
    public const string DownloadControlSnapshotScript = @"
JSON.stringify((function(){
  function visible(el) {
    var r=el.getBoundingClientRect(), s=window.getComputedStyle(el);
    var hit=document.elementFromPoint(r.x+r.width/2,r.y+r.height/2);
    return r.width>0 && r.height>0 && s.visibility!=='hidden' && s.display!=='none' && Number(s.opacity)>0 && !!hit && (hit===el || el.contains(hit));
  }
  return Array.from(document.querySelectorAll('button,a,[role=""button""]')).map(function(el){
    var r=el.getBoundingClientRect();
    if(!el.__openimgDownloadControlToken) el.__openimgDownloadControlToken=(window.crypto&&crypto.randomUUID)?crypto.randomUUID():'openimg-'+Date.now()+'-'+Math.random().toString(36).slice(2);
    return {ariaLabel:el.getAttribute('aria-label'),title:el.getAttribute('title'),text:(el.innerText||'').trim().slice(0,80),
      testId:el.getAttribute('data-testid'),elementToken:el.__openimgDownloadControlToken,visible:visible(el),inDialog:!!el.closest('[role=""dialog""]'),x:r.x+r.width/2,y:r.y+r.height/2};
  }).filter(function(item){return /download|descargar|guardar imagen|save image/i.test([item.ariaLabel,item.title,item.text,item.testId].filter(Boolean).join(' '));});
})())";

    public const string AttachmentUiSnapshotScript = @"
JSON.stringify((function(){
  function visible(el) { var r=el.getBoundingClientRect(), s=window.getComputedStyle(el); return r.width>0 && r.height>0 && s.visibility!=='hidden' && s.display!=='none'; }
  var buttonPattern=/attach|upload|file|add files|photo|image|adjuntar|añadir|agregar|archivo|subir|foto|imagen/i;
  var buttons=Array.from(document.querySelectorAll('button,[role=""button""]')).filter(function(el){
    var label=[el.getAttribute('aria-label'),el.getAttribute('title'),el.innerText].filter(Boolean).join(' ');
    return visible(el) && buttonPattern.test(label);
  }).map(function(el,index){ var r=el.getBoundingClientRect(); return {index:index,tag:el.tagName,role:el.getAttribute('role'),ariaLabel:el.getAttribute('aria-label'),title:el.getAttribute('title'),text:(el.innerText||'').trim().slice(0,80),x:r.x,y:r.y,width:r.width,height:r.height}; });
  var inputs=[]; var seen=new Set();
  function visitRoot(root, frameUrl, frameName){
    if(!root) return;
    Array.from(root.querySelectorAll ? root.querySelectorAll('*') : []).forEach(function(el){
      if(el.matches && el.matches('input[type=""file""]') && !seen.has(el)){
        seen.add(el); var r=el.getBoundingClientRect(), s=el.ownerDocument.defaultView.getComputedStyle(el);
        inputs.push({index:inputs.length,accept:el.getAttribute('accept'),multiple:!!el.multiple,disabled:!!el.disabled,connected:!!el.isConnected,visible:r.width>0&&r.height>0&&s.visibility!=='hidden'&&s.display!=='none',ownerFrame:frameName+' '+frameUrl,x:r.x,y:r.y,width:r.width,height:r.height});
      }
      if(el.shadowRoot) visitRoot(el.shadowRoot,frameUrl,frameName+' shadow');
      if(el.tagName==='IFRAME' || el.tagName==='FRAME') { try { var child=el.contentDocument; if(child) visitRoot(child,child.location.origin+child.location.pathname,'iframe'); } catch(_) {} }
    });
  }
  visitRoot(document,location.origin+location.pathname,'main');
  var menuItems=Array.from(document.querySelectorAll('[role=""menuitem""],button,[role=""button""]')).filter(visible).map(function(el){
    var r=el.getBoundingClientRect(); return {tag:el.tagName,role:el.getAttribute('role'),ariaLabel:el.getAttribute('aria-label'),title:el.getAttribute('title'),text:(el.innerText||'').trim().slice(0,80),x:r.x,y:r.y,width:r.width,height:r.height};
  }).filter(function(item){return /upload|photo|image|computer|archivo|subir|foto|imagen|equipo/i.test([item.ariaLabel,item.title,item.text].filter(Boolean).join(' '));});
  return {url:location.origin+location.pathname,buttons:buttons,inputs:inputs,menuItems:menuItems};
})())";

    public static string GetFileInputAtScript(int index) => $@"
(function(){{
  var found=[], seen=new Set();
  function visitRoot(root){{
    if(!root) return;
    Array.from(root.querySelectorAll ? root.querySelectorAll('*') : []).forEach(function(el){{
      if(el.matches && el.matches('input[type=""file""]') && !seen.has(el)){{seen.add(el);found.push(el);}}
      if(el.shadowRoot) visitRoot(el.shadowRoot);
      if(el.tagName==='IFRAME'||el.tagName==='FRAME'){{try{{if(el.contentDocument)visitRoot(el.contentDocument);}}catch(_){{}}}}
    }});
  }}
  visitRoot(document);
  return found[{index}]||null;
}})()";

    public static string GetFileInputDiagnosticsScript() => AttachmentUiSnapshotScript;

    public static string GetPromptSubmissionStateScript(string serializedExpectedText) =>
        $"JSON.stringify(window.__openimg_gpt.promptSubmissionState({serializedExpectedText}))";

    public const string ReadinessSnapshotScript = @"
JSON.stringify((function(){
  function visible(el) { if (!el) return false; var r = el.getBoundingClientRect(); var s = window.getComputedStyle(el); return r.width > 0 && r.height > 0 && s.visibility !== 'hidden' && s.display !== 'none'; }
  var candidates = Array.from(document.querySelectorAll('textarea, [contenteditable=""true""], [role=""textbox""]'));
  var visibleCandidates = candidates.filter(visible);
  var composer = visibleCandidates[0] || null;
  var loginLinkOrButton = Array.from(document.querySelectorAll('a[href*=""/auth/login""], button[aria-label*=""log in"" i], button[aria-label*=""sign in"" i], button[aria-label*=""iniciar sesión"" i]')).some(visible);
  var identityField = Array.from(document.querySelectorAll('input[type=""password""], input[autocomplete=""username"" i], input[autocomplete=""email"" i], input[name=""username"" i], input[name=""email"" i]')).some(visible);
  var identityAction = Array.from(document.querySelectorAll('button, a, [role=""button""]')).some(function(el){
    if(!visible(el)) return false;
    var label=((el.innerText||'')+' '+(el.getAttribute('aria-label')||'')+' '+(el.getAttribute('title')||'')).replace(/\s+/g,' ').trim();
    return /^(sign in|log in|iniciar sesión|iniciar sesion|acceder)( to chatgpt)?$/i.test(label) ||
      /^(continue with|continuar con) (google|apple|microsoft)$/i.test(label);
  });
  // A sign-in overlay can coexist with a landing-page composer. Authentication
  // evidence must take precedence; composer visibility alone is not proof of a
  // signed-in session.
  var login = loginLinkOrButton || identityField || identityAction;
  var body = document.body;
  var text = body ? (body.innerText || '') : '';
  var blocking = /rate limit|usage limit|too many requests|something went wrong/i.test(text);
  return {
    readyState: document.readyState,
    title: document.title || null,
    url: window.location.href,
    bodyAvailable: !!body,
    bodyInnerTextLength: text.length,
    scriptExecutionHealthy: !!body && document.readyState !== 'loading',
    authenticationState: login ? 'AuthenticationRequired' : composer ? 'Authenticated' : 'Unknown',
    composerCandidateCount: candidates.length,
    visibleComposerCandidateCount: visibleCandidates.length,
    composerDetected: !!composer,
    composerDiagnostics: composer ? JSON.stringify({ tag: composer.tagName, role: composer.getAttribute('role'), contentEditable: composer.getAttribute('contenteditable') === 'true', ariaLabel: composer.getAttribute('aria-label') || composer.getAttribute('placeholder') || null }) : null,
    blocking: blocking
  };
})())";

    public const string CanonicalConversationPathFunction = """
    function(path) {
      if(typeof path!=='string')return null;
      path=path.replace(/\/+$/,'');
      if(!path.startsWith('/c/')||path.length<=3)return null;
      try {var identifier=decodeURIComponent(path.slice(3));return !/^local-chatgpt:/i.test(identifier)&&/^[A-Za-z0-9._~!$&'()*+,;=:@-]+$/.test(identifier)&&identifier!=='.'&&identifier!=='..'?'/c/'+identifier:null;}
      catch(_) {return null;}
    }
    """;

    public const string HelperScript = @"
window.__openimg_gpt = {
  canonicalConversationPath: " + CanonicalConversationPathFunction + @",
  findComposer: function() {
    var candidates = Array.from(document.querySelectorAll('textarea[data-testid*=""textbox"" i], textarea[placeholder*=""message"" i], textarea[placeholder*=""mensaje"" i], div[contenteditable=""true""][data-testid*=""textbox"" i], div[contenteditable=""true""], textarea'));
    return candidates.find(function(el) { var r = el.getBoundingClientRect(); var s = window.getComputedStyle(el); return r.width > 0 && r.height > 0 && s.visibility !== 'hidden' && s.display !== 'none'; }) || null;
  },
  findNewChatButton: function() {
    return document.querySelector('a[href=""/""], a[href=""/"" i]') ||
           document.querySelector('button[data-testid*=""new-chat"" i]') ||
           document.querySelector('button[aria-label*=""new chat"" i]') ||
           document.querySelector('button[aria-label*=""nuevo chat"" i]');
  },
  findFileInput: function() {
    var direct = document.querySelector('input[type=""file""]');
    if (direct) return direct;
    function walk(node) {
      if (!node) return null;
      if (node.shadowRoot) {
        var inside = node.shadowRoot.querySelector('input[type=""file""]');
        if (inside) return inside;
        for (var i = 0; i < node.shadowRoot.children.length; i++) {
          var nested = walk(node.shadowRoot.children[i]);
          if (nested) return nested;
        }
      }
      for (var j = 0; j < (node.children || []).length; j++) {
        var child = walk(node.children[j]);
        if (child) return child;
      }
      return null;
    }
    return walk(document.body);
  },
  findSendButton: function() {
    var candidates = Array.from(document.querySelectorAll('button[data-testid*=""send"" i], button[aria-label*=""send"" i], button[aria-label*=""enviar"" i]'));
    return candidates.find(function(button) { var r=button.getBoundingClientRect(), s=window.getComputedStyle(button); return r.width>0 && r.height>0 && s.visibility!=='hidden' && s.display!=='none'; }) || null;
  },
  countAttachments: function() {
    var selectors = '[data-testid*=""attachment"" i], [data-testid*=""file-preview"" i], [data-testid*=""upload-preview"" i], [aria-label*=""attachment"" i]';
    var nodes = Array.from(document.querySelectorAll(selectors));
    var images = Array.from(document.querySelectorAll('img[src^=""blob:""], img[src^=""data:image/""]'));
    var all = nodes.concat(images);
    return Array.from(new Set(all)).filter(function(el) {
      var rect = el.getBoundingClientRect();
      var aria = (el.getAttribute('aria-label') || '').toLowerCase();
      return el.tagName !== 'INPUT' && !aria.includes('adjuntar archivos') && !aria.includes('añadir archivos') && rect.width > 10 && rect.height > 10;
    }).length;
  },
  attachmentDiagnostics: function() {
    var selectors = '[data-testid*=""attachment"" i], [data-testid*=""file-preview"" i], [data-testid*=""upload-preview"" i], [aria-label*=""attachment"" i]';
    var nodes = Array.from(document.querySelectorAll(selectors));
    var images = Array.from(document.querySelectorAll('img[src^=""blob:""], img[src^=""data:image/""]'));
    return Array.from(new Set(nodes.concat(images))).filter(function(el) {
      var r = el.getBoundingClientRect(); var aria = (el.getAttribute('aria-label') || '').toLowerCase();
      var s = window.getComputedStyle(el);
      return el.tagName !== 'INPUT' && !aria.includes('adjuntar archivos') && !aria.includes('añadir archivos') && r.width > 10 && r.height > 10 && s.visibility !== 'hidden' && s.display !== 'none';
    }).map(function(el) {
      var isPreviewImage = el.tagName === 'IMG' && /^(blob:|data:image\/)/i.test(el.getAttribute('src') || '');
      var r = el.getBoundingClientRect();
      return { kind: isPreviewImage ? 'image-preview' : 'attachment-element', tag: el.tagName, testId: el.getAttribute('data-testid') || '', aria: el.getAttribute('aria-label') || '', width: r.width, height: r.height, text: (el.innerText || '').trim().substring(0, 80) };
    });
  },
  countAssistantResponses: function() {
    var direct = document.querySelectorAll('[data-message-author-role=""assistant""], [data-testid*=""assistant-message"" i]');
    var turns = Array.from(document.querySelectorAll('article, [data-testid*=""conversation-turn"" i]')).filter(function(t,i){
      var r=t.getAttribute('data-message-author-role');
      return r==='assistant'||(!r&&i%2===1);
    });
    return Math.max(direct.length, turns.length, document.querySelectorAll('[data-testid=""generated-image-gallery""]').length);
  },
  countUserMessages: function() {
    var direct = document.querySelectorAll('[data-message-author-role=""user""], [data-testid*=""user-message"" i]');
    var turns = Array.from(document.querySelectorAll('article, [data-testid*=""conversation-turn"" i]')).filter(function(t,i){
      var r=t.getAttribute('data-message-author-role');
      return r==='user'||(!r&&i%2===0);
    });
    return Math.max(direct.length, turns.length);
  },
  submissionState: function(expectedPrompt) {
    function visible(el) { if (!el) return false; var r=el.getBoundingClientRect(), s=getComputedStyle(el); return r.width>0 && r.height>0 && s.visibility!=='hidden' && s.display!=='none'; }
    function canonical(value) { return (value||'').normalize('NFKC').replace(/[\u200B\u200C\u200D\uFEFF]/g,'').replace(/\s+/gu,' ').trim(); }
    var users=Array.from(document.querySelectorAll('[data-message-author-role=""user""], [data-testid*=""user-message"" i]'));
    if(!users.length){
      users=Array.from(document.querySelectorAll('article, [data-testid*=""conversation-turn"" i]')).filter(function(t,i){
        var r=t.getAttribute('data-message-author-role');
        return r==='user'||(!r&&i%2===0);
      });
    }
    var lastUser=users.length ? users[users.length-1] : null;
    var composer=window.__openimg_gpt.findComposer();
    var composerText=composer ? (composer.value || composer.innerText || composer.textContent || '') : '';
    var form=composer ? (composer.closest('form') || composer.parentElement) : null;
    var previews=form ? Array.from(form.querySelectorAll('[data-testid*=""attachment"" i],[data-testid*=""file-preview"" i],[data-testid*=""upload-preview"" i],img[src^=""blob:""],img[src^=""data:image/""]')) : [];
    var send=window.__openimg_gpt.findSendButton();
    var sendForm=send ? send.closest('form') : null;
    var conversationTurns=document.querySelectorAll('[data-testid*=""conversation-turn"" i]').length;
    var alerts=Array.from(document.querySelectorAll('[role=""alert""],[data-sonner-toast]')).filter(visible);
    var generation=window.__openimg_gpt.generationState();
    return {conversationPath:location.pathname,userMessageCount:users.length,
      lastUserMessageMatchesPrompt:!!lastUser && canonical(lastUser.innerText||lastUser.textContent||'')===canonical(expectedPrompt),
      composerEmpty:!composerText.trim(), composerAttachmentCount:previews.filter(visible).length,
      stopControlDetected:generation.stopControlDetected, generationActive:generation.generationActive,
      sendButtonDisabled:!!send && (send.disabled || send.getAttribute('aria-disabled')==='true'),
      diagnostics:{path:location.pathname,bodyTextLength:document.body ? (document.body.innerText||'').length : 0,
        conversationTurnCount:conversationTurns,alertCount:alerts.length,composerPresent:!!composer,
        composerTextLength:composerText.length,sendButtonPresent:!!send,
        sendButtonTestId:send ? (send.getAttribute('data-testid')||'') : '',
        sendButtonAriaLabel:send ? (send.getAttribute('aria-label')||'') : '',
        sendButtonInsideComposerForm:!!sendForm && !!composer && sendForm.contains(composer)}};
  },
  countGeneratedImages: function() {
    return Array.from(document.querySelectorAll('img')).filter(function(img) {
      var src = img.src || '';
      var alt = (img.alt || '').toLowerCase();
      return src && !src.startsWith('blob:') && !alt.includes('avatar') && !alt.includes('profile') && img.getBoundingClientRect().width > 120;
    }).length;
  },
  currentAssistantImages: function(baselineResponseCount) {
    var messages=Array.from(document.querySelectorAll('[data-message-author-role=""assistant""]'));
    var users=Array.from(document.querySelectorAll('[data-message-author-role=""user""]'));
    var scopes=messages.slice(baselineResponseCount).map(function(message){return message.closest('article,[data-testid*=""conversation-turn""]') || message;});
    // Fresh one-message conversations may render A/B image galleries outside
    // the assistant text node. Both alternatives still belong to this turn.
    if(baselineResponseCount===0 && users.length===1) {
      Array.from(document.querySelectorAll('[data-testid=""generated-image-gallery""]')).forEach(function(gallery){
        if(!gallery.closest('[data-message-author-role=""user""],form') &&
          (users[0].compareDocumentPosition(gallery)&Node.DOCUMENT_POSITION_FOLLOWING)) scopes.push(gallery);
      });
    }
    var seen=new Set(), images=[];
    scopes.forEach(function(scope){Array.from(scope.querySelectorAll('img')).forEach(function(image){
      var src=image.currentSrc || image.getAttribute('src') || '', r=image.getBoundingClientRect(), s=getComputedStyle(image);
      var generated=!!image.closest('[data-testid=""generated-image-gallery""]');
      if(src && !seen.has(src) && image.naturalWidth>120 && image.naturalHeight>120 && r.width>0 && r.height>0 &&
        s.display!=='none' && s.visibility!=='hidden' && !image.closest('[data-message-author-role=""user""],form') &&
        (generated || !/^(blob:|data:image\/)/i.test(src)) && !/avatar|profile/i.test(image.alt||'')) {
        seen.add(src); images.push(image);
      }
    });});
    return images.sort(function(a,b){return a===b ? 0 : a.compareDocumentPosition(b)&Node.DOCUMENT_POSITION_FOLLOWING ? -1 : 1;});
  },
  generationState: function() {
    var visible = function(el) { var r=el.getBoundingClientRect(), s=getComputedStyle(el); return r.width>0 && r.height>0 && s.visibility!=='hidden' && s.display!=='none' && Number(s.opacity)>0; };
    var stop = Array.from(document.querySelectorAll('button,[role=""button""]')).some(function(button) {
      var label = ((button.getAttribute('aria-label') || '') + ' ' + (button.getAttribute('title') || '') + ' ' + (button.innerText || '') + ' ' + (button.getAttribute('data-testid') || '')).toLowerCase();
      return visible(button) && /stop|detener/.test(label);
    });
    var thinking = Array.from(document.querySelectorAll('*')).some(function(el) {
      if(el.children.length || el.closest('[data-message-author-role=""user""],form'))return false;
      var text = (el.innerText || el.textContent || '').trim().toLowerCase();
      return (text === 'pensando' || text === 'thinking' || text.startsWith('pensando') || text.startsWith('thinking')) &&
        visible(el);
    });
    return {stopControlDetected:stop, generationActive:stop || thinking};
  },
  currentAssistantImageState: function(baselineResponseCount) {
    var messages = Array.from(document.querySelectorAll('[data-message-author-role=""assistant""]'));
    var galleries = Array.from(document.querySelectorAll('[data-testid=""generated-image-gallery""]'));
    var responseCount = Math.max(messages.length, galleries.length);
    var current = messages.length ? messages[messages.length - 1] : (galleries.length ? galleries[galleries.length - 1] : null);
    var images = window.__openimg_gpt.currentAssistantImages(baselineResponseCount);
    var active = window.__openimg_gpt.generationState().generationActive;
    var responseText = current ? (current.innerText || '').toLowerCase() : '';
    var pageText = (document.body ? (document.body.innerText || '') : '').toLowerCase();
    var visible = function(el) { var r=el.getBoundingClientRect(), s=getComputedStyle(el); return r.width>0 && r.height>0 && s.visibility!=='hidden' && s.display!=='none'; };
    var providerWarning = Array.from(document.querySelectorAll('[role=""alert""],[data-sonner-toast],[data-testid*=""error"" i],[data-testid*=""warning"" i]')).some(visible);
    var rateLimited = /rate[ -]?limit|usage limit|limit reached|reached (?:your|the) (?:image )?(?:generation )?limit|hit (?:your|the) (?:image )?(?:generation )?limit|too many requests|try again (?:later|in \d+|at )|l[ií]mite (?:de uso|alcanzado|diario)|alcanzaste el l[ií]mite/.test(responseText + ' ' + pageText);
    var generationUnavailable = /image generation unavailable|generaci[oó]n de im[aá]genes no est[aá] disponible/.test(responseText);
    var providerUnavailable = /service unavailable|temporarily unavailable|servicio no disponible|int[eé]ntalo m[aá]s tarde/.test(responseText);
    return {responseCount: responseCount, newResponse: responseCount > baselineResponseCount,
      currentResponseImageCount: images.length, currentResponseOwnedImage: responseCount > baselineResponseCount && images.length > 0,
      generationActive: active, rateLimited: rateLimited, generationUnavailable: generationUnavailable,
      providerUnavailable: providerUnavailable, providerWarning: providerWarning};
  },
  insertComposerText: function(text) {
    var el = window.__openimg_gpt.findComposer();
    if (!el) return {composerDetected:false, inserted:false, method:'none'};
    var isContentEditable = el.isContentEditable || el.getAttribute('contenteditable') === 'true';
    var inputObserved = false;
    var onInput = function() { inputObserved = true; };
    el.addEventListener('input', onInput, true);
    el.focus();
    var inserted = false;
    var method;
    if (isContentEditable) {
      var range = document.createRange();
      range.selectNodeContents(el);
      var selection = window.getSelection();
      selection.removeAllRanges();
      selection.addRange(range);
      method = 'contenteditable: focus + selectNodeContents + execCommand(insertText) + InputEvent';
      inserted = document.execCommand('insertText', false, text);
      el.dispatchEvent(new InputEvent('input', { bubbles: true, inputType: 'insertText', data: text }));
      inserted = inserted || (el.innerText || el.textContent || '').length > 0;
    } else if (el.tagName.toLowerCase() === 'textarea' || el.tagName.toLowerCase() === 'input') {
      var prototype = el.tagName.toLowerCase() === 'textarea' ? window.HTMLTextAreaElement.prototype : window.HTMLInputElement.prototype;
      var setter = Object.getOwnPropertyDescriptor(prototype, 'value').set;
      setter.call(el, text);
      el.dispatchEvent(new InputEvent('input', { bubbles: true, inputType: 'insertText', data: text }));
      method = 'input: native value setter + InputEvent';
      inserted = true;
    } else {
      el.removeEventListener('input', onInput, true);
      return {composerDetected:true, inserted:false, method:'unsupported-composer'};
    }
    el.removeEventListener('input', onInput, true);
    var innerText = el.innerText || '';
    var textContent = el.textContent || '';
    var rawText = isContentEditable ? innerText : (el.value || innerText || textContent);
    var sendButton = window.__openimg_gpt.findSendButton();
    var sendRect = sendButton ? sendButton.getBoundingClientRect() : null;
    var sendStyle = sendButton ? window.getComputedStyle(sendButton) : null;
    var sendDetected = !!sendButton && sendRect.width > 0 && sendRect.height > 0 && sendStyle.visibility !== 'hidden' && sendStyle.display !== 'none';
    var sendEnabled = sendDetected && !sendButton.disabled && sendButton.getAttribute('aria-disabled') !== 'true';
    return {composerDetected:true, inserted:inserted, method:method, inputEventObserved:inputObserved,
      rawText:rawText, innerText:innerText, textContent:textContent, innerHtmlLength:(el.innerHTML || '').length,
      sendButtonDetected:sendDetected, sendButtonEnabled:sendEnabled};
  },
  setComposerText: function(text) {
    return !!window.__openimg_gpt.insertComposerText(text).inserted;
  },
  promptSubmissionState: function(expectedText) {
    var el = window.__openimg_gpt.findComposer();
    function canonical(value) { return (value || '').normalize('NFKC').replace(/[\u200B\u200C\u200D\uFEFF]/g, '').replace(/\s+/gu, ' ').trim(); }
    var text = el ? (el.innerText || el.textContent || '') : '';
    var button = window.__openimg_gpt.findSendButton();
    var rect = button ? button.getBoundingClientRect() : null;
    var style = button ? window.getComputedStyle(button) : null;
    var visible = !!button && rect.width > 0 && rect.height > 0 && style.visibility !== 'hidden' && style.display !== 'none';
    return {composerDetected:!!el, normalizedContentMatches:canonical(text) === canonical(expectedText),
      composerInnerText:text, sendButtonDetected:visible,
      sendButtonEnabled:visible && !button.disabled && button.getAttribute('aria-disabled') !== 'true',
      sendButtonAriaLabel:button ? (button.getAttribute('aria-label') || '') : '',
      sendButtonTestId:button ? (button.getAttribute('data-testid') || '') : ''};
  },
  clickSend: function() {
    var button = window.__openimg_gpt.findSendButton();
    if (!button) return false;
    button.click();
    return true;
  },
  clickLatestDownload: function() {
    var messages = Array.from(document.querySelectorAll('[data-message-author-role=""assistant""]'));
    if (!messages.length) return false;
    var message = messages[messages.length - 1];
    var images = Array.from(message.querySelectorAll('img')).filter(function(img) {
      var src = img.getAttribute('src') || '';
      var r = img.getBoundingClientRect();
      return src && !/^(blob:|data:image\/)/i.test(src) && r.width > 120 && r.height > 120;
    });
    if (!images.length) return false;
    var image = images[images.length - 1];
    var scope = image.closest('[data-message-author-role=""assistant""]') || image.closest('article') || image.parentElement?.parentElement?.parentElement;
    if (!scope) return false;
    var buttons = scope.querySelectorAll('button, a');
    for (var i = 0; i < buttons.length; i++) {
      var label = ((buttons[i].getAttribute('aria-label') || '') + ' ' + (buttons[i].innerText || '')).toLowerCase();
      if (label.includes('download') || label.includes('descargar')) { buttons[i].click(); return true; }
    }
    return false;
  },
  isAuthenticated: function() {
    var login = document.querySelector('a[href*=""/auth/login""], button[aria-label*=""log in"" i], button[aria-label*=""sign in"" i], button[aria-label*=""iniciar sesión"" i]');
    if (login) {
      var rect = login.getBoundingClientRect();
      if (rect.width > 0 && rect.height > 0) return false;
    }
    return !!window.__openimg_gpt.findComposer();
  },
  detectBlocking: function() {
    var body = (document.body?.innerText || '').toLowerCase();
    return body.includes('rate limit') || body.includes('usage limit') || body.includes('too many requests') || body.includes('something went wrong');
  },
  getDiagnostics: function() {
    return JSON.stringify({
      readyState: document.readyState,
      url: location.href,
      authenticated: window.__openimg_gpt.isAuthenticated(),
      attachmentCount: window.__openimg_gpt.countAttachments(),
      attachments: window.__openimg_gpt.attachmentDiagnostics(),
      assistantResponseCount: window.__openimg_gpt.countAssistantResponses(),
      generatedImageCount: window.__openimg_gpt.countGeneratedImages(),
      blocking: window.__openimg_gpt.detectBlocking(),
      composer: !!window.__openimg_gpt.findComposer(),
      sendButton: !!window.__openimg_gpt.findSendButton(),
      fileInput: !!window.__openimg_gpt.findFileInput()
    });
  }
};";
}
