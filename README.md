<p align="center">
  <strong style="font-size: 2.2em;">OpenIMG</strong><br>
  <em>Conservative furniture photo restoration for Windows</em>
</p>

<p align="center">
  <img src="https://img.shields.io/badge/Windows-x64-0078D4?style=for-the-badge&logo=windows&logoColor=white" alt="Windows x64" />
  <img src="https://img.shields.io/badge/.NET-10-512BD4?style=for-the-badge&logo=dotnet&logoColor=white" alt=".NET 10" />
  <img src="https://img.shields.io/badge/Distribution-Source_build-475569?style=for-the-badge" alt="Build from source" />
</p>

OpenIMG is a WPF desktop application for improving furniture photographs while preserving the physical structure shown in the original image. It combines local analysis, GPT-assisted restoration, automated validation, and human review before export.

Build and run from source using the commands below. Installers, update patches and generated packages are not part of the repository. The corrected GPT image flow was confirmed in a human live test; see [GPT automation](docs/GPT_AUTOMATION.md) for its behavior and verification limits.

## Workflow

```text
Import photo
        ↓
Verify the original and analyze locally
        ↓
Restore with GPT
        ↓
Validate with Barrier 3
        ↓
Review the original and result
        ↓
Export explicitly
```

## Key features

- **GPT restoration:** uses the GPT web interface in WebView2 with a session authenticated by the user.
- **Local analysis:** prepares image and furniture evidence for the restoration workflow.
- **Independent validation:** Barrier 3 classifies each result as `Accepted`, `NeedsReview`, or `Rejected`; this classification does not replace human judgment.
- **Review and comparison:** lets you inspect the original alongside the restored candidate before deciding.
- **Explicit export:** results are not automatically approved or exported. The original remains immutable.
- **Optional result copies:** an explicitly configured folder can receive review copies without approving the result or changing the original.
- **Local data:** the database, settings, and authenticated browser profile are stored in the user's data directory.

The optional local object detector is not included in this snapshot. The application can be built and started without that model; its absence does not replace or enable GPT restoration.

## Requirements

- Windows x64.
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) to build from source.
- Microsoft Edge WebView2 Runtime to use the GPT web interface.
- A ChatGPT account with access to the required features. Availability and usage limits depend on the account and service.

## Build and run

From PowerShell at the repository root:

```powershell
dotnet restore OpenIMG.slnx
dotnet build OpenIMG.slnx --no-restore
dotnet run --project .\src\OpenIMG.App\OpenIMG.App.csproj
```

To run the fast deterministic tests:

```powershell
.\scripts\test-fast.ps1
```

The standard verification workflow does not run live GPT operations and does not require private photographs or calibration files. Read [CONTRIBUTING.md](CONTRIBUTING.md) before adding tests or data.

For the six offline native-browser generation regressions, run `./scripts/test-gpt-generation.ps1` on Windows with WebView2 Runtime. These cases never send a real prompt to ChatGPT.

## Data and privacy

By default, OpenIMG stores settings, the database, logs, operations, and the WebView2 profile under `%LocalAppData%\OpenIMG`. Do not add personal or customer photographs, databases, logs, browser profiles, credentials, provider telemetry, or generated results to the repository.

The GPT account is authenticated inside WebView2. The session is local to the user and must not be copied or shared. Before sending an image to GPT, make sure you are authorized to process it with an external service.

## Repository structure

| Path | Contents |
|---|---|
| `src/OpenIMG.App` | WPF desktop application and web-provider automation |
| `src/OpenIMG.Application` | Use cases, orchestration, policies, and validation |
| `src/OpenIMG.Core` | Domain entities, contracts, and models |
| `src/OpenIMG.Infrastructure` | Persistence and local analysis/processing |
| `tests/OpenIMG.Tests` | Deterministic product tests |
| `scripts` | Developer verification commands |
| `eng` | Isolated browser automation verification tools |
| `docs` | Current GPT workflow and troubleshooting guidance |

## License and distribution

OpenIMG-authored code is licensed under the MIT License; see [LICENSE](LICENSE). Dependencies remain under their own licenses and notices, described in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md). The YOLO model and local icon are excluded from this snapshot and must not be added to a public package until their licensing and provenance have been resolved separately. See [LICENSING.md](LICENSING.md) for the scope of the project license.

## Notice

Image restoration can alter visual details and should not be treated as evidence of an object's actual state. Preserve the original and review every candidate before using or sharing it.
