<#
.SYNOPSIS
  Launches Leaf on a PDF, waits, captures the window (DPI-aware), optionally sends keys first, and saves a PNG.
.EXAMPLE
  pwsh -NoProfile -File scripts/screenshot.ps1 -Pdf "C:\docs\a.pdf" -Out artifacts\shot.png
  pwsh -NoProfile -File scripts/screenshot.ps1 -Pdf "C:\docs\a.pdf" -Out artifacts\shot.png -Keys "^f","hello" -Wait 3
#>
[CmdletBinding()]
param(
    [string]$Exe = (Join-Path $PSScriptRoot "..\src\Leaf\bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64\Leaf.exe"),
    [Parameter(Mandatory = $true)][string]$Pdf,
    [string]$Out = (Join-Path $PSScriptRoot "..\artifacts\shot.png"),
    [string[]]$Keys = @(),
    [string]$Actions = "",      # LEAF_TEST_ACTIONS script, e.g. "find:Business;zoomin;rotate;pagedown"
    [double]$Wait = 3.0,
    [switch]$Keep,
    [int]$Width = 1400,
    [int]$Height = 900
)
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class ShotNative {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int hh, bool repaint);
  [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT r, int size);
}
"@
[ShotNative]::SetProcessDpiAwarenessContext([IntPtr](-4)) | Out-Null   # PER_MONITOR_AWARE_V2

$Exe = [System.IO.Path]::GetFullPath($Exe)
$Pdf = [System.IO.Path]::GetFullPath($Pdf)
$Out = [System.IO.Path]::GetFullPath($Out)
if (-not (Test-Path -LiteralPath $Exe)) { throw "exe not found: $Exe" }
if (-not (Test-Path -LiteralPath $Pdf)) { throw "pdf not found: $Pdf" }
New-Item -ItemType Directory -Force (Split-Path $Out) | Out-Null

Get-Process Leaf -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 400
if ($Actions) { $env:LEAF_TEST_ACTIONS = $Actions } else { Remove-Item Env:\LEAF_TEST_ACTIONS -ErrorAction SilentlyContinue }
$t0 = Get-Date
$p = Start-Process -FilePath $Exe -ArgumentList ('"' + $Pdf + '"') -PassThru
$deadline = (Get-Date).AddSeconds(20)
while ($p.MainWindowHandle -eq 0 -and -not $p.HasExited -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 10; $p.Refresh() }
if ($p.HasExited) { throw "Leaf exited early with code $($p.ExitCode)" }
$windowMs = [int]((Get-Date) - $t0).TotalMilliseconds
$h = $p.MainWindowHandle
[ShotNative]::MoveWindow($h, 40, 40, $Width, $Height, $true) | Out-Null
[ShotNative]::SetForegroundWindow($h) | Out-Null
Start-Sleep -Milliseconds 600
foreach ($k in $Keys) {
    [System.Windows.Forms.SendKeys]::SendWait($k)
    Start-Sleep -Milliseconds 350
}
Start-Sleep -Seconds $Wait
$p.Refresh()

$r = New-Object ShotNative+RECT
if ([ShotNative]::DwmGetWindowAttribute($h, 9, [ref]$r, 16) -ne 0) { [ShotNative]::GetWindowRect($h, [ref]$r) | Out-Null }   # 9 = DWMWA_EXTENDED_FRAME_BOUNDS
$w = $r.R - $r.L; $hh = $r.B - $r.T
$bmp = New-Object System.Drawing.Bitmap $w, $hh
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($r.L, $r.T, 0, 0, $bmp.Size)
$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()
"window-up {0} ms | title '{1}' | WS {2:N1} MB | private {3:N1} MB | shot {4}x{5} -> {6}" -f $windowMs, $p.MainWindowTitle, ($p.WorkingSet64 / 1MB), ($p.PrivateMemorySize64 / 1MB), $w, $hh, $Out
if (-not $Keep) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue }
