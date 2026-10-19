param(
    [switch] $ConfirmLiveGpt
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $repoRoot "OpenIMG.slnx"
$settings = Join-Path $repoRoot "tests\OpenIMG.Tests\OpenIMG.Tests.runsettings"

$liveGptTests = @(Get-ChildItem (Join-Path $repoRoot "tests\OpenIMG.Tests") -Filter "*.cs" -File -Recurse |
    Select-String -Pattern 'TestCategories\.LiveGpt|Trait\("Category"\s*,\s*"LiveGpt"')
if ($liveGptTests.Count -eq 0) {
    Write-Error "No automated LiveGpt tests are present. This runner will not submit prompts or consume GPT capacity."
    exit 2
}

if (-not $ConfirmLiveGpt) {
    throw "Live GPT tests can submit prompts or consume account capacity. Re-run with -ConfirmLiveGpt only when explicitly intended."
}

& dotnet test $solution --settings $settings --filter "Category=LiveGpt"
exit $LASTEXITCODE
