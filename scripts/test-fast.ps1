$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $repoRoot "OpenIMG.slnx"
$settings = Join-Path $repoRoot "tests\OpenIMG.Tests\OpenIMG.Tests.runsettings"

& dotnet test $solution --settings $settings --filter "(Category=Unit|Category=Integration)"
exit $LASTEXITCODE
