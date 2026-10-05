# Renders the app's main states to docs\images, without touching any real account or download.
param([string]$Exe = (Join-Path $PSScriptRoot '..\src\CloudLink\bin\Release\net10.0-windows\CloudLink.exe'),
      [string]$Out = (Join-Path $PSScriptRoot '..\docs\images'))
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
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

function Save-Window([IntPtr]$handle, [string]$path) {
    $r = New-Object Shot+RECT
    [Shot]::GetWindowRect($handle, [ref]$r) | Out-Null
    $bmp = New-Object Drawing.Bitmap ($r.R - $r.L), ($r.B - $r.T)
    $g = [Drawing.Graphics]::FromImage($bmp)
    $dc = $g.GetHdc()
    [Shot]::PrintWindow($handle, $dc, 2) | Out-Null
    $g.ReleaseHdc($dc); $g.Dispose()
    $bmp.Save($path); $bmp.Dispose()
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
    @{ Name = 'settings-light';    Flags = '--light', '--settings'; Title = 'CloudLink settings' }
)
foreach ($s in $shots) {
    $p = Start-Process $Exe -ArgumentList (@('--demo', '--software') + $s.Flags) -PassThru
    Start-Sleep -Seconds 4
    $w = Get-Windows $p.Id | Where-Object Title -eq $s.Title | Select-Object -First 1
    if ($w) { Save-Window $w.Handle (Join-Path $Out "$($s.Name).png") } else { Write-Warning "No window titled '$($s.Title)'" }
    $p.Kill()
}
Get-ChildItem $Out | Select-Object Name, Length
