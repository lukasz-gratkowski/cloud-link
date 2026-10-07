# Renders the app's main states to docs\images, without touching any real account or download.
# A shot may name controls to point at; they are outlined and numbered in the order given.
# -Only takes one or more names or wildcards, for example -Only downloading-*, done-dark. Type such a list in a
# PowerShell window; "pwsh -File" passes it on as loose words, not as a list.
param([string]$Exe = (Join-Path $PSScriptRoot '..\src\CloudLink\bin\Release\net10.0-windows\CloudLink.exe'),
      [string]$Out = (Join-Path $PSScriptRoot '..\docs\images'),
      [string[]]$Only)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing, UIAutomationClient, UIAutomationTypes
Add-Type @"
using System; using System.Runtime.InteropServices; using System.Text;
public static class Shot {
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    public struct RECT { public int L, T, R, B; }
}
"@
[Shot]::SetProcessDPIAware() | Out-Null
New-Item -ItemType Directory -Force $Out | Out-Null
$Out = (Resolve-Path -LiteralPath $Out).Path   # .NET resolves a relative path against another folder than PowerShell does

# The colour of the numbered marks: readable on the light and the dark theme alike.
$markColour = [Drawing.Color]::FromArgb(0xFF, 0x95, 0x00)

# Outlines a rectangle and puts a numbered badge on its top right corner.
function Add-Mark([Drawing.Graphics]$g, [int]$number, [single]$x, [single]$y, [single]$w, [single]$h) {
    $r = 8; $d = 2 * $r
    $path = New-Object Drawing.Drawing2D.GraphicsPath
    $path.AddArc($x, $y, $d, $d, 180, 90)
    $path.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $path.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $path.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    $g.DrawPath((New-Object Drawing.Pen $markColour, 3), $path)
    $radius = 14
    $g.FillEllipse((New-Object Drawing.SolidBrush $markColour), $x + $w - $radius, $y - $radius, 2 * $radius, 2 * $radius)
    $font = New-Object Drawing.Font 'Segoe UI', 15, ([Drawing.FontStyle]::Bold), ([Drawing.GraphicsUnit]::Pixel)
    $format = New-Object Drawing.StringFormat
    $format.Alignment = 'Center'; $format.LineAlignment = 'Center'
    $g.DrawString("$number", $font, [Drawing.Brushes]::Black,
        (New-Object Drawing.RectangleF ($x + $w - $radius), ($y - $radius), (2 * $radius), (2 * $radius)), $format)
}

function Save-Window([IntPtr]$handle, [string]$path, [string[]]$highlight) {
    $r = New-Object Shot+RECT
    [Shot]::GetWindowRect($handle, [ref]$r) | Out-Null

    # Where the named controls are, read through UI Automation before the picture is taken.
    $boxes = @()
    if ($highlight) {
        $root = [System.Windows.Automation.AutomationElement]::FromHandle($handle)
        foreach ($name in $highlight) {
            $condition = New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::NameProperty, $name)
            $element = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
            if (-not $element) { throw "No control named '$name' in the window for $path" }
            $boxes += , $element.Current.BoundingRectangle
        }
    }

    $bmp = New-Object Drawing.Bitmap ($r.R - $r.L), ($r.B - $r.T)
    $g = [Drawing.Graphics]::FromImage($bmp)
    $dc = $g.GetHdc()
    [Shot]::PrintWindow($handle, $dc, 2) | Out-Null
    $g.ReleaseHdc($dc)

    $g.SmoothingMode = 'AntiAlias'
    $g.TextRenderingHint = 'AntiAliasGridFit'
    $pad = 7
    for ($i = 0; $i -lt $boxes.Count; $i++) {
        $b = $boxes[$i]
        Add-Mark $g ($i + 1) ($b.X - $r.L - $pad) ($b.Y - $r.T - $pad) ($b.Width + 2 * $pad) ($b.Height + 2 * $pad)
    }
    $g.Dispose()
    $bmp.Save($path); $bmp.Dispose()
}

# True once the window shows more than its bare background: a busy PC can take several seconds to draw it.
function Test-Rendered([IntPtr]$handle) {
    $r = New-Object Shot+RECT
    [Shot]::GetWindowRect($handle, [ref]$r) | Out-Null
    $w = $r.R - $r.L; $h = $r.B - $r.T
    if ($w -lt 200 -or $h -lt 200) { return $false }
    $bmp = New-Object Drawing.Bitmap $w, $h
    $g = [Drawing.Graphics]::FromImage($bmp)
    $dc = $g.GetHdc()
    [Shot]::PrintWindow($handle, $dc, 2) | Out-Null
    $g.ReleaseHdc($dc); $g.Dispose()
    $colours = New-Object Collections.Generic.HashSet[int]
    for ($y = 60; $y -lt $h - 10; $y += 24) { for ($x = 10; $x -lt $w - 10; $x += 24) { [void]$colours.Add($bmp.GetPixel($x, $y).ToArgb()) } }
    $bmp.Dispose()
    $colours.Count -gt 12
}

function Get-Windows([int]$processId) {
    $found = New-Object Collections.Generic.List[object]
    [Shot]::EnumWindows({ param($h, $l)
        $p = 0; [Shot]::GetWindowThreadProcessId($h, [ref]$p) | Out-Null
        if ($p -eq $processId -and [Shot]::IsWindowVisible($h)) {
            $sb = New-Object Text.StringBuilder 256
            [Shot]::GetWindowText($h, $sb, 256) | Out-Null
            if ($sb.Length -gt 0) { $found.Add([pscustomobject]@{ Handle = $h; Title = $sb.ToString() }) }
        }
        $true }, [IntPtr]::Zero) | Out-Null
    $found
}

$shots = @(
    @{ Name = 'downloading-dark';  Flags = '--dark';                Title = 'CloudLink' },
    @{ Name = 'downloading-light'; Flags = '--light';               Title = 'CloudLink' },
    @{ Name = 'done-dark';         Flags = '--dark', '--done';      Title = 'CloudLink' },
    @{ Name = 'empty-light';       Flags = '--light', '--empty';    Title = 'CloudLink' },
    @{ Name = 'help-dark';         Flags = '--dark', '--help';      Title = 'CloudLink help' },
    @{ Name = 'settings-light';    Flags = '--light', '--settings'; Title = 'CloudLink settings' },

    # Google Drive with an API key (public links): the walkthrough in docs\USER-GUIDE.md.
    @{ Name = 'google-key-1-settings';   Flags = '--dark', '--google-private', '--empty';   Title = 'CloudLink';          Highlight = 'Settings' },
    @{ Name = 'google-key-2-paste-key';  Flags = '--dark', '--google', '--settings'; Title = 'CloudLink settings'; Highlight = 'Google API key', 'Save' },
    @{ Name = 'google-key-3-paste-link'; Flags = '--dark', '--google', '--empty', '--link'; Title = 'CloudLink';         Highlight = 'Shared links, one per line', 'Download' },
    @{ Name = 'google-key-4-done';       Flags = '--dark', '--google';               Title = 'CloudLink' },

    # Google Drive with a sign-in (private links).
    @{ Name = 'google-signin-1-google-button'; Flags = '--dark', '--google-private', '--empty';              Title = 'CloudLink';          Highlight = 'Google account' },
    @{ Name = 'google-signin-2-paste-client';  Flags = '--dark', '--google-private', '--empty', '--settings'; Title = 'CloudLink settings'; Highlight = 'Google OAuth client ID', 'Google OAuth client secret', 'Save' },
    @{ Name = 'google-signin-3-waiting';       Flags = '--dark', '--google-private', '--empty', '--waiting';  Title = 'CloudLink' },
    @{ Name = 'google-signin-4-done';         Flags = '--dark', '--google-private', '--signed-in';           Title = 'CloudLink';          Highlight = 'Google account' }
)
if ($Only) {
    $shots = @($shots | Where-Object { $name = $_.Name; $Only | Where-Object { $name -like $_ } })
    if (-not $shots) { throw "No picture is named like: $($Only -join ', ')" }
}

foreach ($s in $shots) {
    $p = Start-Process $Exe -ArgumentList (@('--demo', '--software') + $s.Flags) -PassThru
    try {
        $w = $null
        for ($waited = 0; $waited -lt 40; $waited++) {
            Start-Sleep -Seconds 1
            $w = Get-Windows $p.Id | Where-Object Title -eq $s.Title | Select-Object -First 1
            if ($w -and (Test-Rendered $w.Handle)) { break }
        }
        if (-not $w) { Write-Warning "No window titled '$($s.Title)'" }
        elseif (-not (Test-Rendered $w.Handle)) { Write-Warning "$($s.Name): the window never finished drawing; no picture written" }
        else {
            Start-Sleep -Milliseconds 500
            Save-Window $w.Handle (Join-Path $Out "$($s.Name).png") $s.Highlight
        }
    }
    finally {
        if (-not $p.HasExited) { $p.Kill() }
    }
}
Get-ChildItem $Out | Select-Object Name, Length
