<#
.SYNOPSIS
    Builds the Microsoft Store package of CloudLink. Output goes to .\publish-store.

    tests -> publish (self-contained folder) -> icons -> AppxManifest.xml -> resources.pri -> .msix

    The package is left unsigned: the Store signs it. Nothing here installs or registers anything.
    See docs\STORE.md.

.PARAMETER IdentityName
.PARAMETER Publisher
.PARAMETER PublisherDisplayName
.PARAMETER DisplayName
    The package identity. By default the four values are read from store\identity.json, which holds what
    Partner Center shows on the product's "Product identity" page, and the reserved product name.

.PARAMETER DryRun
    Builds with a made-up identity, to try the build before the product exists in Partner Center.
    The result cannot be uploaded.

.PARAMETER Arch
    x64 (default) or arm64.

.PARAMETER UnsignedTest
    Appends the unsigned-package marker to Publisher, so that the result can be installed for a test on
    Windows 11 with  Add-AppxPackage -AllowUnsigned  from an elevated PowerShell. Such a package has a
    different identity from the Store's: never upload it.

.PARAMETER Uncommitted
    Builds although there are uncommitted changes, for registering the layout on this PC while working on it
    (docs\STORE.md, section 4). The result must not be uploaded: its version line names a commit that does
    not hold what was built.

.PARAMETER SdkBin
    Folder holding makeappx.exe and makepri.exe (Windows SDK). Found automatically when omitted.
#>
[CmdletBinding(PositionalBinding = $false)]   # a mistyped switch or a stray word stops the build instead of being ignored
param(
    [string]$IdentityName,
    [string]$Publisher,
    [string]$PublisherDisplayName,
    [string]$DisplayName,
    [ValidateSet('x64', 'arm64')][string]$Arch = 'x64',
    [string]$SdkBin = $env:CLOUDLINK_WINSDK_BIN,
    [switch]$DryRun,
    [switch]$UnsignedTest,
    [switch]$Uncommitted
)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

# --- Identity ---------------------------------------------------------------------------------------------
if ($DryRun) {
    $IdentityName = 'AMGCloudEngineering.CloudLink'; $Publisher = 'CN=00000000-0000-0000-0000-000000000000'
    $PublisherDisplayName = 'AMG Cloud Engineering'; $DisplayName = 'AMG CloudLink'
}
else {
    $stored = Get-Content store\identity.json -Raw -Encoding UTF8 | ConvertFrom-Json
    if (-not $IdentityName) { $IdentityName = $stored.IdentityName }
    if (-not $Publisher) { $Publisher = $stored.Publisher }
    if (-not $PublisherDisplayName) { $PublisherDisplayName = $stored.PublisherDisplayName }
    if (-not $DisplayName) { $DisplayName = $stored.DisplayName }
    $IdentityName = "$IdentityName".Trim(); $Publisher = "$Publisher".Trim()
    $PublisherDisplayName = "$PublisherDisplayName".Trim(); $DisplayName = "$DisplayName".Trim()
    $missing = @('IdentityName', 'Publisher', 'PublisherDisplayName', 'DisplayName') | Where-Object { -not (Get-Variable $_ -ValueOnly) }
    if ($missing) {
        throw "No value for $($missing -join ', '). Fill in store\identity.json from Partner Center (docs\STORE.md), or use -DryRun to try the build with a made-up identity."
    }
    if ($Publisher -cnotmatch '^CN=') { throw "Publisher must be the whole string from Partner Center, starting with CN=: $Publisher" }

    # The package's version line shows the commit it was built from, so that commit has to hold what is built.
    if (-not $UnsignedTest -and -not $Uncommitted -and (git status --porcelain)) {
        throw 'There are uncommitted changes. Commit first: the Store package carries the commit in its version line. (-Uncommitted builds anyway, for a test on this PC only.)'
    }
    if ($Uncommitted -and -not (git status --porcelain)) { $Uncommitted = $false }   # nothing uncommitted after all
}
if ($UnsignedTest) { $Publisher += ', OID.2.25.311729368913984317654407730594956997722=1' }

# --- Version: the one in Directory.Build.props; the fourth part belongs to the Store and stays 0 ------------
$props = [xml](Get-Content Directory.Build.props -Raw)
$version = $props.SelectSingleNode('/Project/PropertyGroup/Version').InnerText.Trim()
if ($version -notmatch '^(\d+)\.(\d+)\.(\d+)$') { throw "Version '$version' must be Major.Minor.Patch for a Store package." }
$parts = $Matches[1..3] | ForEach-Object { [int]$_ }
if ($parts[0] -eq 0) { throw 'The first part of a Store package version cannot be 0.' }
if (($parts | Measure-Object -Maximum).Maximum -gt 65535) { throw 'Each part of the version must be 65535 or less.' }
$packageVersion = "$version.0"

# A Store build without the application ID could not sign in to OneDrive.
$idNode = $props.SelectSingleNode('/Project/PropertyGroup/CloudLinkMicrosoftClientId')
if (-not $idNode -or -not [guid]::TryParse($idNode.InnerText.Trim(), [ref][guid]::Empty)) {
    throw 'Directory.Build.props has no application ID in CloudLinkMicrosoftClientId.'
}

# --- Windows SDK tools ------------------------------------------------------------------------------------
function Find-SdkBin {
    if ($SdkBin -and -not ((Test-Path (Join-Path $SdkBin 'makeappx.exe')) -and (Test-Path (Join-Path $SdkBin 'makepri.exe')))) {
        throw "makeappx.exe and makepri.exe are not in $SdkBin."
    }
    $kits ='D:\DevDrive\Windows Kits\10\bin', "${env:ProgramFiles(x86)}\Windows Kits\10\bin" | Where-Object { Test-Path $_ }
    $fromKits = $kits | ForEach-Object { Get-ChildItem $_ -Directory -Filter '10.*' } |
        Sort-Object { [version]$_.Name } -Descending | ForEach-Object { Join-Path $_.FullName 'x64' }
    $fromNuGet = Get-ChildItem "$env:USERPROFILE\.nuget\packages\microsoft.windows.sdk.buildtools" -Directory -ErrorAction SilentlyContinue |
        Sort-Object { [version]$_.Name } -Descending |
        ForEach-Object { Get-ChildItem (Join-Path $_.FullName 'bin') -Directory | ForEach-Object { Join-Path $_.FullName 'x64' } }
    foreach ($candidate in @($SdkBin) + @($fromKits) + @($fromNuGet)) {
        if ($candidate -and (Test-Path (Join-Path $candidate 'makeappx.exe')) -and (Test-Path (Join-Path $candidate 'makepri.exe'))) { return $candidate }
    }
    throw 'makeappx.exe and makepri.exe (Windows SDK) were not found. Pass -SdkBin <folder>.'
}
$sdk = Find-SdkBin
$makepri = Join-Path $sdk 'makepri.exe'; $makeappx = Join-Path $sdk 'makeappx.exe'

dotnet test -c Release -nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }

$out = Join-Path $PSScriptRoot 'publish-store'
# A trial build gets a folder of its own, so it never replaces a layout that is registered for testing.
$stage = Join-Path $out ("stage-$Arch" + $(if ($DryRun) { '-dryrun' }) + $(if ($UnsignedTest) { '-unsigned' }))
$layout = Join-Path $stage 'layout'
$registered = Get-AppxPackage -Name $IdentityName -ErrorAction SilentlyContinue | Where-Object { $_.InstallLocation -eq $layout }
if ($registered) {
    throw "CloudLink is registered for testing from $layout. Remove it first:  Get-AppxPackage $IdentityName | Remove-AppxPackage"
}
if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
New-Item -ItemType Directory -Force $layout | Out-Null

# --- 1. Publish: an ordinary self-contained folder. The single-file exe of build.ps1 would unpack its native
#        DLLs to %TEMP% at start, outside the package.
dotnet publish src\CloudLink\CloudLink.csproj -c Release -r "win-$Arch" --self-contained -p:DebugType=none -o $layout -nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
$unwanted = Get-ChildItem $layout -Recurse -File -Include *.pfx, *.snk, *.cer, *.pdb
if ($unwanted) { throw "Files that must not be in the package: $($unwanted.Name -join ', ')" }

# --- 2. Icons and tiles, and the logo for the Store listing.
& (Join-Path $PSScriptRoot 'tools\make-store-assets.ps1') -OutDir (Join-Path $layout 'Assets') -ListingDir (Join-Path $out 'listing') | Out-Null
$heavy = Get-ChildItem (Join-Path $layout 'Assets') -Filter *.png | Where-Object Length -ge 204800
if ($heavy) { throw "Package images must be smaller than 204800 bytes: $($heavy.Name -join ', ')" }

# --- 3. Manifest.
$escape = { param($text) [Security.SecurityElement]::Escape($text) }
$manifest = (Get-Content store\AppxManifest.xml -Raw -Encoding UTF8).
    Replace('__IDENTITY_NAME__', (& $escape $IdentityName)).
    Replace('__IDENTITY_PUBLISHER__', (& $escape $Publisher)).
    Replace('__PUBLISHER_DISPLAY_NAME__', (& $escape $PublisherDisplayName)).
    Replace('__DISPLAY_NAME__', (& $escape $DisplayName)).
    Replace('__VERSION__', $packageVersion).Replace('__ARCH__', $Arch)
if ($manifest -match '__[A-Z_]+__') { throw "Unfilled placeholder in the manifest: $($Matches[0])" }
$manifestPath = Join-Path $layout 'AppxManifest.xml'
[IO.File]::WriteAllText($manifestPath, $manifest, (New-Object Text.UTF8Encoding $false))

# --- 4. resources.pri: the index that maps "Assets\StoreLogo.png" in the manifest to the right picture for
#        each size and theme. One file, built from the pictures only.
$config = Join-Path $stage 'priconfig.xml'; $pri = Join-Path $stage 'resources.pri'
& $makepri createconfig /cf $config /dq en-US /pv 10.0.0 /o | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'makepri createconfig failed.' }
$listed = Join-Path $layout 'assets.resfiles'
Get-ChildItem (Join-Path $layout 'Assets') -Filter *.png | ForEach-Object { "Assets\$($_.Name)" } | Set-Content $listed -Encoding ascii
$xml = [xml](Get-Content $config -Raw)
# Without this makepri splits the scales into resources.scale-NNN.pri files, which only a bundle can carry.
if ($xml.resources['packaging']) { [void]$xml.resources.RemoveChild($xml.resources['packaging']) }
$index = $xml.resources.index
$index.SetAttribute('startIndexAt', 'assets.resfiles')
foreach ($indexer in @($index.SelectNodes('indexer-config'))) { [void]$index.RemoveChild($indexer) }
$files = $xml.CreateElement('indexer-config')
$files.SetAttribute('type', 'resfiles'); $files.SetAttribute('qualifierDelimiter', '.')
[void]$index.AppendChild($files)
$xml.Save($config)
& $makepri new /pr $layout /cf $config /mn $manifestPath /of $pri /o | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'makepri new failed.' }
Remove-Item -LiteralPath $listed
if (@(Get-ChildItem $stage -Filter 'resources*.pri').Count -ne 1) { throw 'makepri wrote more than one .pri file.' }

# makeappx does not notice a manifest picture that the index cannot find, so that is checked here.
$dump = Join-Path $stage 'resources.pri.xml'
& $makepri dump /if $pri /of $dump /o | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'makepri dump failed.' }
$dumped = Get-Content $dump -Raw
$pictures = [regex]::Matches($manifest, '"(Assets\\[^"]+\.png)"|>(Assets\\[^<]+\.png)<') |
    ForEach-Object { if ($_.Groups[1].Success) { $_.Groups[1].Value } else { $_.Groups[2].Value } } | Sort-Object -Unique
foreach ($picture in $pictures) {
    $uri = "ms-resource://$IdentityName/Files/" + $picture.Replace('\', '/')
    if (-not $dumped.Contains($uri)) { throw "resources.pri has no entry for $picture (expected $uri)." }
}
Copy-Item $pri (Join-Path $layout 'resources.pri') -Force

# --- 5. Package.
$name = "CloudLink_${packageVersion}_$Arch" + $(if ($DryRun) { '_DRYRUN' }) + $(if ($UnsignedTest) { '_UNSIGNED-TEST' }) +
    $(if ($Uncommitted) { '_UNCOMMITTED' }) + '.msix'
$msix = Join-Path $out $name
$log = & $makeappx pack /o /h SHA256 /d $layout /p $msix 2>&1
if ($LASTEXITCODE -ne 0) { $log | Select-Object -Last 12 | Write-Host; throw 'makeappx pack failed.' }

[pscustomobject]@{
    Package   = $msix
    Upload    = if ($DryRun -or $UnsignedTest -or $Uncommitted) { 'NO: this build is for trying things out only' } else { 'yes, as it is (the Store signs it)' }
    Identity  = "$IdentityName  $packageVersion  $Arch"
    Publisher = $Publisher
    ShownAs   = "$DisplayName, by $PublisherDisplayName"
    Files     = (Get-ChildItem $layout -Recurse -File).Count
    MB        = [math]::Round((Get-Item $msix).Length / 1MB, 1)
    SHA256    = (Get-FileHash $msix -Algorithm SHA256).Hash.ToLowerInvariant()
    Pictures  = "$($pictures.Count) manifest pictures resolved in resources.pri"
    Listing   = Join-Path $out 'listing'
} | Format-List
