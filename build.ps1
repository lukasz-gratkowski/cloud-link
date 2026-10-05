<#
.SYNOPSIS
    Tests, publishes and signs CloudLink. Output goes to .\publish.

.PARAMETER CertThumbprint
    Thumbprint of the code-signing certificate to use (CurrentUser\My or LocalMachine\My).
    Defaults to $env:CLOUDLINK_SIGN_THUMBPRINT, then to a valid certificate whose subject starts with
    "CN=AMG Cloud Engineering" or "CN=CloudLink" (one issued by a certificate authority before a self-signed one).

.PARAMETER CreateSelfSignedCert
    If no certificate is found, create a self-signed one for this user. A self-signed signature proves the
    file was not altered after the build, but Windows does not know the publisher: see docs\SIGNING.md.

.PARAMETER NoSign
    Skip signing.

.PARAMETER MicrosoftClientId
    Application (client) ID to build in for OneDrive sign-in, overriding the one in Directory.Build.props
    for this build only, or "none" for a build without one. See docs\APP-REGISTRATION.md.
#>
param(
    [string]$CertThumbprint = $env:CLOUDLINK_SIGN_THUMBPRINT,
    [string]$MicrosoftClientId,
    [switch]$CreateSelfSignedCert,
    [switch]$NoSign
)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$idArgs = @()
if ($MicrosoftClientId) {
    if ($MicrosoftClientId -ne 'none' -and -not [guid]::TryParse($MicrosoftClientId, [ref][guid]::Empty)) {
        throw "-MicrosoftClientId must be a GUID, or 'none' for a build without one: $MicrosoftClientId"
    }
    if ($MicrosoftClientId -ne 'none') { $MicrosoftClientId = ([guid]$MicrosoftClientId).ToString() }
    $idArgs = @("-p:CloudLinkMicrosoftClientId=$MicrosoftClientId")
}

# A build is expected to carry an application ID; one without has to be asked for by name.
if (-not $MicrosoftClientId) {
    $node = ([xml](Get-Content Directory.Build.props -Raw)).SelectSingleNode('/Project/PropertyGroup/CloudLinkMicrosoftClientId')
    $configured = if ($node) { $node.InnerText.Trim() } else { '' }
    if (-not [guid]::TryParse($configured, [ref][guid]::Empty)) {
        throw "Directory.Build.props has no application ID in CloudLinkMicrosoftClientId. Set it, or pass -MicrosoftClientId <guid>, or -MicrosoftClientId none for a build without one."
    }
}

# A certificate can be listed as having a private key that Windows can no longer open (the key was stored
# for another logon, or its protection was lost). Signing with it fails, so it is tested first.
function Test-PrivateKey($certificate) {
    try {
        $key = [System.Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($certificate)
        if (-not $key) { $key = [System.Security.Cryptography.X509Certificates.ECDsaCertificateExtensions]::GetECDsaPrivateKey($certificate) }
        return [bool]$key
    }
    catch { return $false }
}

function Find-SigningCert {
    $all = @(Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert) + @(Get-ChildItem Cert:\LocalMachine\My -CodeSigningCert -ErrorAction SilentlyContinue)
    $valid = $all | Where-Object { $_.HasPrivateKey -and $_.NotAfter -gt (Get-Date) }
    if ($CertThumbprint) {
        $match = $valid | Where-Object Thumbprint -eq ($CertThumbprint -replace '\s', '') | Select-Object -First 1
        if (-not $match) { throw "No valid code-signing certificate with thumbprint $CertThumbprint." }
        if (-not (Test-PrivateKey $match)) { throw "The private key of certificate $CertThumbprint cannot be opened from this session." }
        return $match
    }
    $ours = $valid | Where-Object { $_.Subject -like 'CN=AMG Cloud Engineering*' -or $_.Subject -like 'CN=CloudLink*' }
    foreach ($dead in $ours | Where-Object { -not (Test-PrivateKey $_) }) {
        Write-Warning "Skipping certificate '$($dead.Subject)' ($($dead.Thumbprint)): its private key cannot be opened from this session."
    }
    # A certificate issued to the publisher is preferred over a self-signed one.
    $ours | Where-Object { Test-PrivateKey $_ } |
        Sort-Object @{ Expression = { $_.Subject -eq $_.Issuer } }, @{ Expression = 'NotAfter'; Descending = $true } |
        Select-Object -First 1
}

# The certificate is settled before anything is built, so a missing one stops the build with nothing half-made.
$cert = $null
if (-not $NoSign) {
    $cert = Find-SigningCert
    if (-not $cert -and $CreateSelfSignedCert) {
        $cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject 'CN=AMG Cloud Engineering (self-signed)' `
            -CertStoreLocation Cert:\CurrentUser\My -KeyAlgorithm RSA -KeyLength 3072 -HashAlgorithm SHA256 `
            -KeyExportPolicy NonExportable -NotAfter (Get-Date).AddYears(3)
        Write-Host "Created self-signed certificate $($cert.Thumbprint)"
    }
    if (-not $cert) {
        throw 'No code-signing certificate found. Pass -CertThumbprint, -CreateSelfSignedCert, or -NoSign for an unsigned build. See docs\SIGNING.md.'
    }
}

dotnet test -c Release -nologo -v q @idArgs
if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }

if (Test-Path publish) { Remove-Item publish -Recurse -Force }
dotnet publish src\CloudLink\CloudLink.csproj -c Release -r win-x64 --self-contained `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none -o publish -nologo -v q @idArgs
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
$exe = Join-Path $PSScriptRoot 'publish\CloudLink.exe'

if ($cert) {
    try {
        # The timestamp keeps the signature valid after the certificate itself expires.
        $result = Set-AuthenticodeSignature -FilePath $exe -Certificate $cert -HashAlgorithm SHA256 `
            -TimestampServer 'http://timestamp.digicert.com'
        if (-not $result.SignerCertificate) { throw "Signing failed: $($result.StatusMessage)" }
        $check = Get-AuthenticodeSignature $exe
        if (-not $check.SignerCertificate) { throw 'The exe carries no signature after signing.' }
        if (-not $check.TimeStamperCertificate) { throw 'The signature has no timestamp (timestamp server unreachable?). Run the build again.' }
    }
    catch {
        # Never leave something that looks like a finished release.
        Move-Item $exe (Join-Path $PSScriptRoot 'publish\CloudLink.UNSIGNED.exe') -Force
        throw
    }
    $selfSigned = $cert.Subject -eq $cert.Issuer
    if ($selfSigned) {
        # The public half, for anyone who decides to trust this publisher on their own PC.
        Export-Certificate -Cert $cert -FilePath publish\CloudLink-signing.cer | Out-Null
    }
    Write-Host "Signed by '$($cert.Subject)'$(if ($selfSigned) { ' (self-signed: Windows will still say Unknown publisher)' })"
}

# Checksums let anyone confirm that a download is the file that was built here.
$hash = (Get-FileHash $exe -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content publish\SHA256SUMS.txt "$hash  CloudLink.exe"

$sig = Get-AuthenticodeSignature $exe
[pscustomobject]@{
    File      = $exe
    Version   = (Get-Item $exe).VersionInfo.ProductVersion
    MB        = [math]::Round((Get-Item $exe).Length / 1MB, 1)
    Signature = if ($sig.SignerCertificate) { "$($sig.SignerCertificate.Subject) [$($sig.Status)]" } else { 'none' }
    Timestamp = if ($sig.TimeStamperCertificate) { 'yes' } else { 'no' }
    SHA256    = $hash
} | Format-List
