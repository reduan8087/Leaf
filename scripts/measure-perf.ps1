<#
.SYNOPSIS
  Measures Leaf startup time and memory, optionally compares with Adobe Acrobat, and reports publish/installer sizes.
.EXAMPLE
  pwsh -NoProfile -File scripts/measure-perf.ps1 -Pdf "C:\docs\sample.pdf" -Runs 5
  pwsh -NoProfile -File scripts/measure-perf.ps1 -Pdf "C:\docs\sample.pdf" -Acrobat
  pwsh -NoProfile -File scripts/measure-perf.ps1 -SizeOnly
#>
[CmdletBinding()]
param(
    [string]$Exe = (Join-Path $PSScriptRoot "..\artifacts\publish\Leaf.exe"),
    [string]$Pdf,
    [int]$Runs = 5,
    [int]$SettleSeconds = 4,
    [switch]$Acrobat,
    [switch]$SizeOnly
)
$ErrorActionPreference = "Stop"
$root = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$Exe = [System.IO.Path]::GetFullPath($Exe)

function Get-DirSizeMB([string]$dir) {
    if (-not (Test-Path $dir)) { return $null }
    [math]::Round(((Get-ChildItem $dir -Recurse -File | Measure-Object Length -Sum).Sum / 1MB), 1)
}

Write-Host "== Sizes"
$pub = Split-Path $Exe
"publish folder : {0} MB ({1})" -f (Get-DirSizeMB $pub), $pub
$setup = Get-ChildItem (Join-Path $root "dist") -Filter "Leaf-Setup*.exe" -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if ($setup) { "installer      : {0} MB ({1})" -f [math]::Round($setup.Length / 1MB, 1), $setup.Name } else { "installer      : (none built)" }
if (Test-Path $pub) {
    Get-ChildItem $pub -File | Sort-Object Length -Descending | Select-Object -First 8 | ForEach-Object { "  {0,-45} {1,8:N1} MB" -f $_.Name, ($_.Length / 1MB) }
}
if ($SizeOnly) { return }

if (-not (Test-Path $Exe)) { throw "Leaf.exe not found at $Exe. Run: dotnet publish src/Leaf -c Release -r win-x64 -o artifacts/publish" }
if (-not $Pdf) { throw "-Pdf is required (a sample PDF to open)" }
$Pdf = [System.IO.Path]::GetFullPath($Pdf)
if (-not (Test-Path $Pdf)) { throw "PDF not found: $Pdf" }

$perfLog = Join-Path $env:LOCALAPPDATA "Leaf\perf.log"
$env:LEAF_PERF = "1"

Write-Host "`n== Leaf: $Runs runs on $(Split-Path $Pdf -Leaf) (run 1 is cold only if you just rebooted)"
$rows = foreach ($i in 1..$Runs) {
    Get-Process Leaf -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 800
    if (Test-Path $perfLog) { Remove-Item $perfLog -Force }
    $t0 = Get-Date
    $p = Start-Process -FilePath $Exe -ArgumentList ('"' + $Pdf + '"') -PassThru
    while ($p.MainWindowHandle -eq 0 -and -not $p.HasExited) { Start-Sleep -Milliseconds 2; $p.Refresh() }
    $windowMs = [int]((Get-Date) - $t0).TotalMilliseconds
    Start-Sleep -Seconds $SettleSeconds
    $p.Refresh()
    $firstFrame = $null; $firstPage = $null
    if (Test-Path $perfLog) {
        $lines = Get-Content $perfLog
        $ff = $lines | Where-Object { $_ -match "first-frame=(\d+)" } | Select-Object -Last 1
        if ($ff) { $firstFrame = [int]$Matches[1] }
        $fp = $lines | Where-Object { $_ -match "first-page=(\d+)" } | Select-Object -Last 1
        if ($fp) { $firstPage = [int]$Matches[1] }
    }
    $privWs = $null
    try { $privWs = [math]::Round(((Get-Counter "\Process(Leaf)\Working Set - Private").CounterSamples[0].CookedValue) / 1MB, 1) } catch {}
    $row = [pscustomobject]@{
        Run = $i
        WindowMs = $windowMs
        FirstFrameMs = $firstFrame
        FirstPageMs = $firstPage
        WS_MB = [math]::Round($p.WorkingSet64 / 1MB, 1)
        PrivWS_MB = $privWs
        Private_MB = [math]::Round($p.PrivateMemorySize64 / 1MB, 1)
        PeakWS_MB = [math]::Round($p.PeakWorkingSet64 / 1MB, 1)
    }
    Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
    $row
}
$rows | Format-Table -AutoSize
$warm = $rows | Select-Object -Skip 1
if ($warm) {
    "median warm window-up: {0} ms; median WS: {1} MB" -f (($warm.WindowMs | Sort-Object)[[int](($warm.Count - 1) / 2)]), (($warm.WS_MB | Sort-Object)[[int](($warm.Count - 1) / 2)])
}

if ($Acrobat) {
    Write-Host "`n== Adobe Acrobat on the same file (10 s settle)"
    $acro = Get-ItemProperty "HKLM:\SOFTWARE\Classes\Acrobat.Document.DC\shell\Open\command" -ErrorAction SilentlyContinue
    if (-not $acro) { $acro = Get-ItemProperty "HKCU:\SOFTWARE\Classes\Acrobat.Document.DC\shell\Open\command" -ErrorAction SilentlyContinue }
    $t0 = Get-Date
    Start-Process -FilePath $Pdf | Out-Null   # uses the current default handler
    Start-Sleep -Seconds 10
    Get-Process Acrobat, AcroCEF, AdobeCollabSync, armsvc, AcroRd32, RdrCEF -ErrorAction SilentlyContinue |
        Select-Object Name, Id, @{n = 'WS_MB'; e = { [math]::Round($_.WorkingSet64 / 1MB, 1) } }, @{n = 'Private_MB'; e = { [math]::Round($_.PrivateMemorySize64 / 1MB, 1) } } |
        Format-Table -AutoSize
    $total = (Get-Process Acrobat, AcroCEF, AdobeCollabSync, AcroRd32, RdrCEF -ErrorAction SilentlyContinue | Measure-Object WorkingSet64 -Sum).Sum
    "Acrobat process set total working set: {0} MB" -f [math]::Round($total / 1MB, 1)
}
