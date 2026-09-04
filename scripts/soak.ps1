<#
.SYNOPSIS
  Stability soak: opens a PDF, resizes and focuses the window, scrolls through the document and back, zooms,
  minimizes/restores, and keeps polling for the given number of seconds. Fails (exit 1) if the process exits.
.EXAMPLE
  pwsh -NoProfile -File scripts/soak.ps1 -Pdf "C:\docs\a.pdf" -Seconds 90
  pwsh -NoProfile -File scripts/soak.ps1 -Exe "$env:LOCALAPPDATA\Programs\Leaf\Leaf.exe" -Pdf "C:\docs\a.pdf"
#>
[CmdletBinding()]
param(
    [string]$Exe = (Join-Path $PSScriptRoot "..\src\Leaf\bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64\Leaf.exe"),
    [Parameter(Mandatory = $true)][string]$Pdf,
    [int]$Seconds = 90,
    [string]$Actions = "scrollthrough;home;zoomin;zoomin;zoomout;fitwidth;end;home;wait:1000;scrollthrough;home"
)
$ErrorActionPreference = "Stop"
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class SoakNative {
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int hh, bool repaint);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
}
"@
$Exe = [System.IO.Path]::GetFullPath($Exe)
$Pdf = [System.IO.Path]::GetFullPath($Pdf)
if (-not (Test-Path -LiteralPath $Exe)) { throw "exe not found: $Exe" }
if (-not (Test-Path -LiteralPath $Pdf)) { throw "pdf not found: $Pdf" }

Get-Process Leaf -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 500
$env:LEAF_TEST_ACTIONS = $Actions
$env:LEAF_PERF = "1"
$t0 = Get-Date
$p = Start-Process -FilePath $Exe -ArgumentList ('"' + $Pdf + '"') -PassThru
$deadline = (Get-Date).AddSeconds(20)
while (-not $p.HasExited -and (Get-Date) -lt $deadline -and ($p.MainWindowHandle -eq 0 -or -not $p.MainWindowTitle -or $p.MainWindowTitle -eq "Leaf")) { Start-Sleep -Milliseconds 50; $p.Refresh() }
if ($p.HasExited) { Write-Host "FAIL: exited during startup, code 0x$('{0:X8}' -f $p.ExitCode)"; exit 1 }
$h = $p.MainWindowHandle
"document open at t+$([int]((Get-Date) - $t0).TotalSeconds)s: '$($p.MainWindowTitle)'"

# stress the surfaces: resize, focus, then let the scripted actions run
[SoakNative]::MoveWindow($h, 30, 30, 1500, 950, $true) | Out-Null
[SoakNative]::SetForegroundWindow($h) | Out-Null
$sizes = @(@(1500, 950), @(900, 700), @(1700, 1000))
$next = 0
$minimizedAt = [int]($Seconds / 2)
$restored = $false
while (((Get-Date) - $t0).TotalSeconds -lt $Seconds) {
    Start-Sleep -Seconds 3
    $p.Refresh()
    if ($p.HasExited) {
        Write-Host ("FAIL: process exited at t+{0}s with code 0x{1:X8}" -f [int]((Get-Date) - $t0).TotalSeconds, $p.ExitCode)
        exit 1
    }
    $elapsed = [int]((Get-Date) - $t0).TotalSeconds
    if ($elapsed % 12 -lt 3) {
        $s = $sizes[$next % $sizes.Count]; $next++
        [SoakNative]::MoveWindow($h, 30, 30, $s[0], $s[1], $true) | Out-Null
    }
    if (-not $restored -and $elapsed -ge $minimizedAt) {
        [SoakNative]::ShowWindow($h, 6) | Out-Null   # minimize
        Start-Sleep -Seconds 4
        [SoakNative]::ShowWindow($h, 9) | Out-Null   # restore
        $restored = $true
    }
    "t+{0,3}s alive  WS {1,6:N1} MB  private {2,6:N1} MB  title '{3}'" -f $elapsed, ($p.WorkingSet64 / 1MB), ($p.PrivateMemorySize64 / 1MB), $p.MainWindowTitle
}
$p.Refresh()
if ($p.HasExited) { Write-Host "FAIL: exited at the end with code 0x$('{0:X8}' -f $p.ExitCode)"; exit 1 }
Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
$log = Join-Path $env:LOCALAPPDATA "Leaf\perf.log"
if (Test-Path $log) { "perf.log tail:"; Get-Content $log | Select-Object -Last 3 }
Write-Host "PASS: alive for $Seconds s"
exit 0
