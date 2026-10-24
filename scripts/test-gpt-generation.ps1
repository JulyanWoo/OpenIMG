[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'eng/OpenIMG.AutomationCandidates/OpenIMG.AutomationCandidates.csproj'
$buildRoot = Join-Path $repoRoot 'artifacts/gpt-generation-tests/build'
$resultsRoot = Join-Path $repoRoot 'artifacts/gpt-generation-tests/results'

& dotnet build $project --artifacts-path $buildRoot -v quiet
if ($LASTEXITCODE -ne 0) { throw 'The offline GPT generation runner failed to build.' }

$runner = Join-Path $buildRoot 'bin/OpenIMG.AutomationCandidates/debug/OpenIMG.AutomationCandidates.dll'
foreach ($scenario in @('thinking-zero', 'route-only', 'thinking-stabilization', 'thinking-long', 'provisional-route', 'provisional-route-encoded')) {
    & dotnet $runner webview (Join-Path $resultsRoot "$scenario.json") $scenario
    if ($LASTEXITCODE -ne 0) { throw "Offline GPT generation case failed: $scenario" }
}
