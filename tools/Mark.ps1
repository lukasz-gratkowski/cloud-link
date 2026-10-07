# The CloudLink mark, drawn as vectors (the same geometry as LogoImage in App.xaml).
# Dot-sourced by make-icon.ps1 and make-store-assets.ps1 so every size is drawn, never resampled.
Add-Type -AssemblyName System.Drawing

function New-RoundedRect([single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
    $p = New-Object Drawing.Drawing2D.GraphicsPath
    $d = 2 * $r
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    $p
}

# -Solid fills the whole square and writes no alpha channel, for places that ask for a logo without
# transparency; with -Flat the fill is one colour instead of the gradient. Otherwise the tile has rounded,
# transparent corners.
function New-Mark([int]$size, [switch]$Solid, [switch]$Flat) {
    $format = if ($Solid) { [Drawing.Imaging.PixelFormat]::Format24bppRgb } else { [Drawing.Imaging.PixelFormat]::Format32bppArgb }
    $bmp = New-Object Drawing.Bitmap $size, $size, $format
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.ScaleTransform($size / 64.0, $size / 64.0)

    # Tile: azure to violet, top-left to bottom-right.
    $from = [Drawing.Color]::FromArgb(0x2E, 0x7B, 0xFF); $to = [Drawing.Color]::FromArgb(0x7B, 0x4D, 0xFF)
    if ($Solid) {
        $fill = if ($Flat) { New-Object Drawing.SolidBrush $from } else {
            New-Object Drawing.Drawing2D.LinearGradientBrush (
                (New-Object Drawing.PointF -1, -1), (New-Object Drawing.PointF 65, 65), $from, $to)
        }
        $g.FillRectangle($fill, -1, -1, 66, 66)
    }
    else {
        $tile = New-RoundedRect 2 2 60 60 15
        $fill = New-Object Drawing.Drawing2D.LinearGradientBrush (
            (New-Object Drawing.PointF 2, 2), (New-Object Drawing.PointF 62, 62), $from, $to)
        $g.FillPath($fill, $tile)
    }

    # Cloud.
    $cloud = New-Object Drawing.Drawing2D.GraphicsPath
    $cloud.FillMode = 'Winding'
    $cloud.AddEllipse(14, 21, 20, 20)
    $cloud.AddEllipse(24, 13, 28, 28)
    $cloud.AddPath((New-RoundedRect 10 31 44 20 10), $false)
    $g.FillPath([Drawing.Brushes]::White, $cloud)

    # Arrow, heavier at small sizes so it survives 16 px.
    $weight = if ($size -le 24) { 6.5 } else { 4.5 }
    $pen = New-Object Drawing.Pen ([Drawing.Color]::FromArgb(0x3D, 0x6B, 0xFA)), $weight
    $pen.StartCap = 'Round'; $pen.EndCap = 'Round'; $pen.LineJoin = 'Round'
    $g.DrawLine($pen, 32, 27, 32, 44)
    $g.DrawLines($pen, [Drawing.PointF[]]@(
        (New-Object Drawing.PointF 25, 37), (New-Object Drawing.PointF 32, 44), (New-Object Drawing.PointF 39, 37)))

    $g.Dispose()
    $bmp
}
