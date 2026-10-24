param([string]$DotNet = 'dotnet')
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:TESTINGPLATFORM_TELEMETRY_OPTOUT = '1'
$previousDataPath = $env:MWB_STANDALONE_DATA
$env:MWB_STANDALONE_DATA = Join-Path $PSScriptRoot 'test-data'
Push-Location $PSScriptRoot
try {
    & $DotNet build Tests/MouseWithoutBorders.UnitTests.csproj -c Release -p:RestoreLockedMode=true --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Test build failed.' }
    & $DotNet Tests/bin/Release/net10.0-windows10.0.19041.0/win-x64/MouseWithoutBorders.UnitTests.dll --report-trx --results-directory test-results
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
} finally {
    $env:MWB_STANDALONE_DATA = $previousDataPath
    Pop-Location
}
