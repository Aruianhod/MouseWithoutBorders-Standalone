param([string]$DotNet = 'dotnet', [string]$Destination = (Join-Path $PSScriptRoot 'dist'))
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
Push-Location $PSScriptRoot
try {
    foreach ($project in @('App/MouseWithoutBorders.csproj', 'App/Helper/MouseWithoutBordersHelper.csproj')) {
        & $DotNet publish $project -c Release --self-contained true -p:RestoreLockedMode=true -o $Destination --nologo
        if ($LASTEXITCODE -ne 0) { throw "Publish failed: $project" }
    }
    Copy-Item LICENSE,UPSTREAM-NOTICE.md,CHANGES.md,PROVENANCE.json,*.txt $Destination
    Copy-Item THIRD-PARTY-LICENSES $Destination -Recurse -Force
} finally { Pop-Location }
