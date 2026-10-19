# Contributing

## Prerequisites

- Windows x64
- .NET 10 SDK
- Microsoft Edge WebView2 Runtime for manual desktop/provider checks

## Verify changes

Run the deterministic suite from the repository root:

```powershell
.\scripts\test-fast.ps1
```

It runs only the public `Unit` and `Integration` test categories. `dotnet test OpenIMG.slnx` is also safe by default: calibration-only tests are not compiled into the default test project. Live GPT operations are not part of default verification. The live-GPT runner currently exits without doing work if no explicitly categorized live test exists.

## Data and privacy boundaries

- Use synthetic or explicitly redistributable test fixtures; never add personal/customer photographs to a change.
- Do not submit `settings.json`, SQLite databases, logs, WebView2 profiles, provider telemetry, downloads, or generated images.
- Keep provider automation behind the provider boundary and keep local deterministic validation independent from provider availability.
- Preserve originals and keep automated decisions separate from human review and explicit export.
- Do not change Barrier 3 thresholds or enable Barrier 4 as part of an unrelated change.

Please describe which build and deterministic tests you ran. Do not run authenticated GPT or historical calibration commands as a routine contribution check.
