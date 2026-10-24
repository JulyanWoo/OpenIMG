namespace OpenIMG.App.Browser;

internal static partial class GptDomAdapter
{
    // The operation adapter is the sole owner of candidate/viewer selectors and DOM claims.
    public const string OperationScript = """
    if(!window.__openimg_operation_dom) window.__openimg_operation_dom=(function(){
      const canonical=t=>(t||'').normalize('NFKC').replace(/[\u200B\u200C\u200D\uFEFF]/g,'').replace(/\s+/gu,' ').trim();
      const visible=e=>{if(!e)return false;const r=e.getBoundingClientRect(),s=getComputedStyle(e);return r.width>0&&r.height>0&&s.visibility!=='hidden'&&s.display!=='none'&&Number(s.opacity)>0;};
      const users=()=>{
        const direct=Array.from(document.querySelectorAll('[data-message-author-role="user"], [data-testid*="user-message" i]'));
        if(direct.length)return direct;
        const turns=Array.from(document.querySelectorAll('article, [data-testid*="conversation-turn" i]'));
        if(turns.length){
          const u=turns.filter((t,i)=>{
            const r=t.getAttribute('data-message-author-role');
            if(r==='user')return true;
            if(r==='assistant')return false;
            const tid=t.getAttribute('data-testid')||'';
            if(/user/i.test(tid))return true;
            if(/assistant/i.test(tid))return false;
            return i%2===0;
          });
          if(u.length)return u;
        }
        return [];
      };
      const assistants=()=>{
        const direct=Array.from(document.querySelectorAll('[data-message-author-role="assistant"], [data-testid*="assistant-message" i]'));
        if(direct.length)return direct;
        const turns=Array.from(document.querySelectorAll('article, [data-testid*="conversation-turn" i]'));
        if(turns.length){
          const a=turns.filter((t,i)=>{
            const r=t.getAttribute('data-message-author-role');
            if(r==='assistant')return true;
            if(r==='user')return false;
            const tid=t.getAttribute('data-testid')||'';
            if(/assistant/i.test(tid))return true;
            if(/user/i.test(tid))return false;
            return i%2===1;
          });
          if(a.length)return a;
        }
        return [];
      };
      const galleries=()=>Array.from(document.querySelectorAll('[data-testid="generated-image-gallery"]'));
      const path=()=>window.__openimg_gpt.canonicalConversationPath(location.pathname);
      const source=i=>i.currentSrc||i.src;
      const loaded=i=>visible(i)&&i.complete&&i.naturalWidth>120&&i.naturalHeight>120;
      const warnings=()=>window.__openimg_gpt.currentAssistantImageState(0);
      let operation=null,observer=null,scheduled=false;
      function changed(){
        if(scheduled)return;scheduled=true;
        setTimeout(()=>{scheduled=false;if(operation&&window.chrome?.webview)chrome.webview.postMessage({type:'openimg-operation-dom',token:operation.id});},40);
      }
      function cleanPath(p){return window.__openimg_gpt.canonicalConversationPath(p);}
      function begin(id,expectedPath,baseline,prompt,recovery){
        observer?.disconnect();
        operation={id,expectedPath:cleanPath(expectedPath),baseline,prompt:canonical(prompt),recovery,submitted:recovery,baselineGalleries:recovery?null:new Set(galleries()),baselineImages:new Set(Array.from(document.querySelectorAll('img')).map(source)),owned:null,samples:0,openRequested:false,downloadClicked:false};
        observer=new MutationObserver(changed);observer.observe(document.documentElement,{childList:true,subtree:true,attributes:true,attributeFilter:['src','aria-disabled','disabled','style','class']});
        return true;
      }
      function submitted(id,expectedPath){
        if(operation?.id!==id)return false;
        operation.submitted=true;
        const cp=cleanPath(expectedPath);
        if(cp)operation.expectedPath=cp;
        return true;
      }
      function candidateImages(c){
        const user=users(),messages=assistants();
        let scopes=messages.slice(c.baseline).map(m=>m.closest('article,[data-testid*="conversation-turn"]')||m);
        if(c.baseline===0&&user.length===1)scopes.push(...galleries().filter(g=>!g.closest('[data-message-author-role="user"],form')&&(user[0].compareDocumentPosition(g)&Node.DOCUMENT_POSITION_FOLLOWING)));
        if(!c.recovery)scopes.push(...galleries().filter(g=>!c.baselineGalleries.has(g)&&!g.closest('[data-message-author-role="user"],form')));
        if(c.recovery)scopes.push(...galleries().filter(g=>!g.closest('[data-message-author-role="user"],form')));
        if(!scopes.length){
          var root=document.querySelector('main')||document.body;
          if(root)scopes.push(root);
        }
        const seen=new Set(),images=[];
        for(const scope of scopes)for(const img of scope.querySelectorAll('img')){
          if(loaded(img)&&!seen.has(source(img))&&!img.closest('[data-message-author-role="user"],form')&&!/avatar|profile/i.test(img.alt||'')){
            if(!c.recovery&&c.baselineImages&&c.baselineImages.has(source(img)))continue;
            seen.add(source(img));images.push(img);
          }
        }
        return images.sort((a,b)=>a===b?0:a.compareDocumentPosition(b)&Node.DOCUMENT_POSITION_FOLLOWING?-1:1);
      }
      function check(id){
        const c=operation;
        if(!c||c.id!==id)return {state:'RecoveryRequired',reason:'DocumentClaimMissing'};
        const provider=warnings();
        if(provider.rateLimited)return {state:'RateLimited'};
        if(provider.providerUnavailable||provider.generationUnavailable)return {state:'ProviderUnavailable'};
        const current=path();
        // A changing route during active generation must not interrupt the wait.
        // Bind only a durable identity; validate mismatches before owning output.
        if(c.submitted&&!c.owned&&provider.generationActive){
          if(!c.expectedPath&&current)c.expectedPath=current;
          c.lastActiveAt=Date.now();c.samples=0;c.pendingKey=null;
          return {state:'Generating',conversationPath:current===c.expectedPath?current:null};
        }
        if(location.pathname==='/'){
          if(c.recovery)return {state:'RecoveryRequired',reason:'HomeNavigation'};
          return {state:'ConversationPending'};
        }
        if(c.expectedPath&&current&&c.expectedPath.startsWith('/c/')&&current.startsWith('/c/')&&current!==c.expectedPath){
          return {state:'OwnershipFailed',reason:'DifferentConversation'};
        }
        if(c.owned){
          const owner=c.owned;
          if(c.openRequested){
            const matching=Array.from(document.querySelectorAll('[role="dialog"],[aria-modal="true"]')).filter(visible).flatMap(d=>Array.from(d.querySelectorAll('img')).filter(i=>loaded(i)&&source(i)===owner.source).map(i=>({image:i,dialog:d})));
            if(matching.length!==1)return {state:'ViewerPending'};
            return {state:'ViewerOpened',candidateCount:owner.count,width:owner.width,height:owner.height};
          }
          if(!owner.image.isConnected||source(owner.image)!==owner.source||owner.image.naturalWidth!==owner.width||owner.image.naturalHeight!==owner.height)return {state:'RecoveryRequired',reason:'CandidateChangedBeforeViewer'};
          return {state:'CandidateOwned',candidateCount:owner.count,width:owner.width,height:owner.height,source:owner.source,conversationPath:c.expectedPath||current};
        }
        if(!c.submitted)return {state:'AwaitingSubmission'};
        if(!c.expectedPath&&current&&current.startsWith('/c/'))c.expectedPath=current;
        if(provider.providerWarning)return {state:'ProviderWarning'};
        if(c.lastActiveAt&&Date.now()-c.lastActiveAt<1000)return {state:'Generating',conversationPath:current||c.expectedPath};
        if(!current){
          return {state:'ConversationPending'};
        }
        const images=candidateImages(c);
        if(!images.length)return {state:'Generating',conversationPath:current||c.expectedPath};
        // Verify ownership only when a completed candidate exists.
        const user=users();
        if(user.length&&canonical(user[user.length-1].innerText||user[user.length-1].textContent)!==c.prompt)return {state:'OwnershipFailed',reason:'DifferentPrompt'};
        if(c.recovery&&(!user.length||(c.baseline===0&&user.length!==1)))return {state:'ConversationPending'};
        const image=images[0],key=source(image)+'|'+image.naturalWidth+'|'+image.naturalHeight;
        c.samples=c.pendingKey===key?c.samples+1:1;c.pendingKey=key;
        if(c.samples<2)return {state:'CandidateDetected',conversationPath:current||c.expectedPath,candidateCount:images.length};
        c.owned={image,source:source(image),width:image.naturalWidth,height:image.naturalHeight,ordinal:0,count:images.length};
        return {state:'CandidateOwned',conversationPath:current||c.expectedPath,candidateCount:images.length,width:image.naturalWidth,height:image.naturalHeight,source:source(image)};
      }
      function open(id){
        const state=check(id),c=operation;if(state.state!=='CandidateOwned'||c.openRequested)return false;
        c.openRequested=true;c.owned.image.scrollIntoView({block:'center'});c.owned.image.click();return true;
      }
      function downloadControl(id){
        const state=check(id),c=operation;if(state.state!=='ViewerOpened')return state;
        const dialogs=Array.from(document.querySelectorAll('[role="dialog"],[aria-modal="true"]')).filter(visible);
        const dialog=dialogs.find(d=>Array.from(d.querySelectorAll('img')).some(i=>loaded(i)&&source(i)===c.owned.source));
        const controls=Array.from(dialog.querySelectorAll('button,a,[role="button"]')).filter(e=>{
          const label=[e.getAttribute('aria-label'),e.title,e.textContent,e.getAttribute('data-testid')].filter(Boolean).join(' ');
          const r=e.getBoundingClientRect(),hit=document.elementFromPoint(r.x+r.width/2,r.y+r.height/2);
          return /download|descargar|guardar imagen|save image/i.test(label)&&visible(e)&&!e.disabled&&e.getAttribute('aria-disabled')!=='true'&&hit&&(hit===e||e.contains(hit));
        });
        if(controls.length!==1)return {state:'DownloadControlPending',candidateCount:c.owned.count};
        const control=controls[0];control.__openimgOperationControlToken??=crypto.randomUUID();c.control=control;
        return {state:'DownloadReady',token:control.__openimgOperationControlToken,candidateCount:c.owned.count};
      }
      function click(id,token){
        const state=downloadControl(id),c=operation;
        if(state.state!=='DownloadReady'||state.token!==token||c.downloadClicked)return false;
        c.downloadClicked=true;c.control.click();return true;
      }
      function summary(id){
        const c=operation;return {present:!!c&&c.id===id,submitted:!!c?.submitted,candidateOwned:!!c?.owned,viewerOpened:!!c?.openRequested,downloadClicked:!!c?.downloadClicked,userMessageCount:users().length,assistantMessageCount:assistants().length,generatedGalleryCount:galleries().length};
      }
      return {begin,submitted,check,open,downloadControl,click,summary};
    })();
    """;
}
