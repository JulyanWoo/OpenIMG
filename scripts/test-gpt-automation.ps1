[CmdletBinding()]
param([ValidateSet('Both','WebView2','Playwright')][string]$Backend = 'Both')
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$outputs = Join-Path $repo 'artifacts/automation-stabilization'
$buildRoot = Join-Path $outputs 'build'
& dotnet build (Join-Path $repo 'eng/OpenIMG.AutomationCandidates') -c Release --artifacts-path $buildRoot -v quiet
if ($LASTEXITCODE -ne 0) { throw 'Automation lab build failed.' }
$runner = Join-Path $buildRoot 'bin/OpenIMG.AutomationCandidates/release/OpenIMG.AutomationCandidates.dll'
$env:PLAYWRIGHT_BROWSERS_PATH = Join-Path $env:LOCALAPPDATA 'OpenIMG/Playwright/Browsers'
if ($Backend -ne 'WebView2' -and !(Test-Path -LiteralPath $env:PLAYWRIGHT_BROWSERS_PATH)) {
    throw 'Install the isolated Playwright Chromium browser first; see eng/README.md.'
}
foreach ($candidate in @('webview','playwright')) {
    if (($Backend -eq 'WebView2' -and $candidate -ne 'webview') -or ($Backend -eq 'Playwright' -and $candidate -ne 'playwright')) { continue }
    & dotnet $runner $candidate (Join-Path $outputs "$candidate-final-matrix.json")
    if ($LASTEXITCODE -ne 0) { throw "$candidate acceptance failed. No automatic re-run was performed." }
}
