# Draws the pictures a Microsoft Store package needs (the icons and tiles named in store\AppxManifest.xml),
# each at its exact pixel size. With -ListingDir it also writes the logo that is uploaded to the Store listing.
param([Parameter(Mandatory)][string]$OutDir,
      [string]$ListingDir)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Mark.ps1')
New-Item -ItemType Directory -Force $OutDir | Out-Null
$OutDir = (Resolve-Path -LiteralPath $OutDir).Path   # .NET resolves a relative path against another folder than PowerShell does

# A transparent canvas of w x h with the mark drawn at $mark px in the middle.
function Save-Asset([string]$name, [int]$w, [int]$h, [int]$mark) {
    $canvas = New-Object Drawing.Bitmap $w, $h, ([Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [Drawing.Graphics]::FromImage($canvas)
    $g.Clear([Drawing.Color]::Transparent)
    $m = New-Mark $mark
    $g.DrawImageUnscaled($m, [int](($w - $mark) / 2), [int](($h - $mark) / 2))
    $m.Dispose(); $g.Dispose()
    $canvas.Save((Join-Path $OutDir $name), [Drawing.Imaging.ImageFormat]::Png)
    $canvas.Dispose()
}

function Px([double]$base, [double]$factor) { [int][math]::Round($base * $factor, [MidpointRounding]::AwayFromZero) }

foreach ($scale in 100, 125, 150, 200, 400) {
    $f = $scale / 100.0
    # App list icon and Store logo: the mark fills the canvas (it carries its own margin).
    Save-Asset "Square44x44Logo.scale-$scale.png" (Px 44 $f) (Px 44 $f) (Px 44 $f)
    Save-Asset "StoreLogo.scale-$scale.png"       (Px 50 $f) (Px 50 $f) (Px 50 $f)
    # Tiles of the Windows 10 Start menu: transparent, the mark in the middle with room around it.
    Save-Asset "SmallTile.scale-$scale.png"         (Px 71 $f)  (Px 71 $f)  (Px (71 * 0.66) $f)
    Save-Asset "Square150x150Logo.scale-$scale.png" (Px 150 $f) (Px 150 $f) (Px (150 * 0.50) $f)
    Save-Asset "Wide310x150Logo.scale-$scale.png"   (Px 310 $f) (Px 150 $f) (Px (150 * 0.50) $f)
    Save-Asset "LargeTile.scale-$scale.png"         (Px 310 $f) (Px 310 $f) (Px (310 * 0.50) $f)
}
# Taskbar, Start and Explorer ask for exact sizes. The "unplated" forms stop Windows from shrinking the icon
# onto a coloured plate; the light-theme form is the same drawing.
foreach ($size in 16, 20, 24, 30, 32, 36, 40, 48, 60, 64, 72, 80, 96, 256) {
    Save-Asset "Square44x44Logo.targetsize-$size.png" $size $size $size
    Save-Asset "Square44x44Logo.targetsize-${size}_altform-unplated.png" $size $size $size
    Save-Asset "Square44x44Logo.targetsize-${size}_altform-lightunplated.png" $size $size $size
}

if ($ListingDir) {
    New-Item -ItemType Directory -Force $ListingDir | Out-Null
    $ListingDir = (Resolve-Path -LiteralPath $ListingDir).Path
    # "1:1 app tile icon" of the Store listing.
    $tile = New-Mark 300
    $tile.Save((Join-Path $ListingDir 'store-logo-300.png'), [Drawing.Imaging.ImageFormat]::Png)
    $tile.Dispose()
}

Get-ChildItem $OutDir -Filter *.png | Measure-Object Length -Sum -Maximum | Select-Object Count, Sum, Maximum
