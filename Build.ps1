param([string]$DotNet = 'dotnet', [string]$Destination = (Join-Path $PSScriptRoot 'dist'))
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
Push-Location $PSScriptRoot
try {
    foreach ($project in @('App/MouseWithoutBorders.csproj', 'App/Helper/MouseWithoutBordersHelper.csproj')) {
        & $DotNet publish $project -c Release --self-contained true -p:RestoreLockedMode=true -o $Destination --nologo
        if ($LASTEXITCODE -ne 0) { throw "Publish failed: $project" }
    }
    Copy-Item LICENSE,UPSTREAM-NOTICE.md,README-zh-CN.txt,CHANGES.md,THIRD-PARTY-NOTICES.txt $Destination
    Copy-Item THIRD-PARTY-LICENSES $Destination -Recurse -Force
} finally { Pop-Location }
