$ErrorActionPreference = "Stop"
Write-Host "Running the portable deterministic Unit + Integration suite. Calibration and provider tests are excluded."
& (Join-Path $PSScriptRoot "test-fast.ps1")
exit $LASTEXITCODE
