#requires -Version 7.0
param(
    [Parameter(Mandatory)][string]$Package,
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$PrivateKey,
    [Parameter(Mandatory)][string]$Feed,
    [string]$Notes = '更新无界鼠标独立版。'
)
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+\.\d+$') { throw 'A four-part version is required.' }
$packageFile = Get-Item -LiteralPath $Package
if ($packageFile.Name -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]{0,180}\.zip$') { throw 'Invalid package filename.' }
if ($packageFile.Length -gt 512MB) { throw 'Package exceeds the client limit.' }
$publicCode = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'UpdateCore/UpdateTrust.cs') -Raw
$publicKey = [regex]::Match($publicCode, 'PublicKey = "([^"]+)"').Groups[1].Value
$plainKey = [Security.Cryptography.ProtectedData]::Unprotect([IO.File]::ReadAllBytes((Resolve-Path -LiteralPath $PrivateKey)), $null, [Security.Cryptography.DataProtectionScope]::CurrentUser)
$rsa = [Security.Cryptography.RSA]::Create()
try {
    $rsa.ImportPkcs8PrivateKey($plainKey, [ref]$null)
    if ([Convert]::ToBase64String($rsa.ExportSubjectPublicKeyInfo()) -cne $publicKey) { throw 'Private key does not match the pinned client key.' }
    $zip = [IO.Compression.ZipFile]::OpenRead($packageFile.FullName)
    $temporaryDll = [IO.Path]::GetTempFileName()
    try {
        foreach ($required in @('MouseWithoutBorders.exe', 'MouseWithoutBorders.dll', 'MouseWithoutBordersHelper.exe', 'MouseWithoutBorders.Updater.exe', 'mwb-standalone-install.txt')) {
            if ($null -eq $zip.GetEntry($required)) { throw "Required file missing: $required" }
        }
        [IO.Compression.ZipFileExtensions]::ExtractToFile($zip.GetEntry('MouseWithoutBorders.dll'), $temporaryDll, $true)
        if ([Diagnostics.FileVersionInfo]::GetVersionInfo($temporaryDll).FileVersion -ne $Version) { throw 'Package binary version mismatch.' }
    } finally { $zip.Dispose(); Remove-Item -LiteralPath $temporaryDll }
    $manifest = [ordered]@{
        Product = 'MouseWithoutBordersStandalone'; Platform = 'win-x64'; Version = $Version
        File = $packageFile.Name; Bytes = $packageFile.Length
        Sha256 = (Get-FileHash -LiteralPath $packageFile.FullName -Algorithm SHA256).Hash
        Notes = $Notes
    }
    $payload = [Text.Encoding]::UTF8.GetBytes(($manifest | ConvertTo-Json -Compress))
    $signature = $rsa.SignData($payload, [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1)
    $envelope = @{ Payload = [Convert]::ToBase64String($payload); Signature = [Convert]::ToBase64String($signature) } | ConvertTo-Json -Compress
    New-Item -ItemType Directory -Path $Feed -Force | Out-Null
    $target = Join-Path $Feed $packageFile.Name
    if (Test-Path -LiteralPath $target) {
        if ((Get-FileHash -LiteralPath $target).Hash -ne $manifest.Sha256) { throw 'Refusing to overwrite an existing release with different contents.' }
    } else {
        $temporaryPackage = Join-Path $Feed ($packageFile.Name + '.' + [guid]::NewGuid().ToString('N') + '.tmp')
        Copy-Item -LiteralPath $packageFile.FullName -Destination $temporaryPackage
        if ((Get-FileHash -LiteralPath $temporaryPackage).Hash -ne $manifest.Sha256) { throw 'Copied package hash mismatch.' }
        [IO.File]::Move($temporaryPackage, $target)
    }
    $temporaryManifest = Join-Path $Feed ('latest.' + [guid]::NewGuid().ToString('N') + '.tmp')
    [IO.File]::WriteAllText($temporaryManifest, $envelope, [Text.UTF8Encoding]::new($false))
    $latest = Join-Path $Feed 'latest.json'
    for ($attempt = 0; $attempt -lt 10; $attempt++) {
        try { [IO.File]::Move($temporaryManifest, $latest, $true); break }
        catch [IO.IOException] { if ($attempt -eq 9) { throw }; Start-Sleep -Milliseconds 200 }
    }
    Write-Output "Published signed update $Version to $Feed"
} finally {
    [Security.Cryptography.CryptographicOperations]::ZeroMemory($plainKey)
    $rsa.Dispose()
}
