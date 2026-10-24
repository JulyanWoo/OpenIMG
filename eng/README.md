# Offline provider verification

The projects in this folder are engineering tools, separate from the production WPF application. Their synthetic pages intercept all browser requests and use isolated profiles under `%LocalAppData%/OpenIMG-AutomationLab`.

For the six focused generation regressions, run:

```powershell
.\scripts\test-gpt-generation.ps1
```

For the broader native WebView2 automation matrix, run:

```powershell
.\scripts\test-gpt-automation.ps1 -Backend WebView2
```

The optional Playwright comparison is not a production backend. It requires a separately installed Chromium browser in its dedicated local cache; it is not needed for either command above. Do not copy a normal browser profile into these tools.

`OpenIMG.AutomationCandidates` also supports a single `webview` or complete `provider` scenario. Both require one Send and the expected native-download outcome; successful downloads verify exact fixture bytes and SHA-256 persistence. `OpenIMG.AutomationLab/Fixtures/chat.html` is the shared controlled page, not an authenticated provider session.

See [the GPT workflow guide](../docs/GPT_AUTOMATION.md) for conversation binding, timeouts and live-validation limits. Keep generated reports, profiles and download artifacts out of Git.
