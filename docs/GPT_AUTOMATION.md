# GPT image generation

OpenIMG uses the ChatGPT web interface hosted in WebView2. The user authenticates in the visible provider panel. The normal operation is:

```text
Upload source → Send once → wait for generation → verify candidate ownership
→ open viewer → native download → validate file and SHA-256 → continue queue
```

## Waiting and conversation binding

- Stop controls, including their test IDs, and visible Thinking/Pensando indicate active generation. Zero message or image counts are transient while this activity remains visible.
- Submission acknowledgement confirms acceptance of Send; it does not wait for the completed image. Its default deadline is 15 seconds.
- `GptGenerationTimeoutSeconds` independently defaults to 600 seconds. The existing provider boundary accepts a 30–600 second selected generation deadline, including explicit operation overrides.
- ChatGPT may navigate through `/c/local-chatgpt:...` or its encoded form before assigning `/c/<server-id>`. The local route is provisional and never becomes the operation's durable conversation identity.
- The Send checkpoint is persisted immediately. The server conversation is persisted when it appears. Active generation continues waiting without recovery navigation; a different final conversation cannot authorize a download.
- After activity stops, allow a short stabilization period and consecutive candidate observations before opening the viewer.
- Recovery uses existing submission evidence and never silently resends a prompt. An unresolved operation without a durable conversation requires an explicit user decision.

## Verification

Run deterministic product tests:

```powershell
.\scripts\test-fast.ps1
```

Run the six focused native WebView2 generation regressions:

```powershell
.\scripts\test-gpt-generation.ps1
```

These cases cover Thinking with no messages/images, delayed conversation binding, route-only acknowledgement, output stabilization, generation beyond 20 seconds, and both provisional-route encodings. Pages and downloads are intercepted locally, using synthetic images and isolated test profiles. No authenticated ChatGPT generation occurs. Generated test artifacts are ignored by Git.

A human live test confirmed the corrected image-generation flow on 2026-10-23. This establishes that execution's result; it does not certify every account, future provider layout or clean-machine installation.

Close all OpenIMG windows before launching a different build: one runtime directory allows one application instance. Keep user settings, databases, browser profiles, photos and generated results outside the repository.
