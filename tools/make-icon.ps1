# Draws the CloudLink mark (the same geometry as LogoImage in App.xaml) and writes
# src\CloudLink\Assets\CloudLink.ico with every size Windows asks for, plus a 256 px PNG.
$ErrorActionPreference = 'Stop'

$assets = Join-Path $PSScriptRoot '..\src\CloudLink\Assets'
New-Item -ItemType Directory -Force $assets | Out-Null

# New-Mark draws the mark at any size; it is shared with make-store-assets.ps1.
. (Join-Path $PSScriptRoot 'Mark.ps1')

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$images = foreach ($s in $sizes) {
    $bmp = New-Mark $s
    $ms = New-Object IO.MemoryStream
    $bmp.Save($ms, [Drawing.Imaging.ImageFormat]::Png)
    if ($s -eq 256) { $bmp.Save((Join-Path $assets 'CloudLink.png'), [Drawing.Imaging.ImageFormat]::Png) }
    $bmp.Dispose()
    , $ms.ToArray()
}

# ICO container: 6-byte header, one 16-byte entry per image, then the PNG data.
$out = New-Object IO.MemoryStream
$w = New-Object IO.BinaryWriter $out
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$images[$i].Length); $w.Write([uint32]$offset)
    $offset += $images[$i].Length
}
foreach ($img in $images) { $w.Write($img) }
$w.Flush()
[IO.File]::WriteAllBytes((Join-Path $assets 'CloudLink.ico'), $out.ToArray())

# Logos for places outside the program, such as the Microsoft Entra app registration (215 x 215 PNG).
$branding = Join-Path $PSScriptRoot '..\branding'
New-Item -ItemType Directory -Force $branding | Out-Null
$variants = @{ Name = 'CloudLink-logo-215-opaque.png'; Solid = $true; Flat = $false },
            @{ Name = 'CloudLink-logo-215-flat.png'; Solid = $true; Flat = $true },
            @{ Name = 'CloudLink-logo-215-transparent.png'; Solid = $false; Flat = $false }
foreach ($variant in $variants) {
    $bmp = New-Mark 215 -Solid:$variant.Solid -Flat:$variant.Flat
    $bmp.Save((Join-Path $branding $variant.Name), [Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
}
Get-ChildItem $assets, $branding | Select-Object Name, Length
