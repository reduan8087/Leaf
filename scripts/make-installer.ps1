<#
.SYNOPSIS
  Builds dist/Leaf-Setup-x64.exe: Native AOT publish (unless -SkipPublish) then Inno Setup compile of installer/Leaf.iss.
.EXAMPLE
  pwsh -NoProfile -File scripts/make-installer.ps1
  pwsh -NoProfile -File scripts/make-installer.ps1 -SkipPublish -Version 0.1.0
#>
[CmdletBinding()]
param(
    [switch]$SkipPublish,
    [string]$Version = ""
)
$ErrorActionPreference = "Stop"
$root = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
Set-Location $root

if (-not $Version) {
    $props = Get-Content (Join-Path $root "Directory.Build.props") -Raw
    $Version = if ($props -match "<Version>([^<]+)</Version>") { $Matches[1] } else { "0.1.0" }
}

if (-not $SkipPublish) {
    & pwsh -NoProfile -File (Join-Path $PSScriptRoot "publish.ps1")
    if ($LASTEXITCODE -ne 0) { throw "publish failed" }
}
$publish = Join-Path $root "artifacts\publish"
if (-not (Test-Path (Join-Path $publish "Leaf.exe"))) { throw "publish output missing: $publish" }
if (-not (Test-Path (Join-Path $publish "Leaf.pri"))) { throw "Leaf.pri missing from publish output (XAML resources)" }

$iscc = @(
    (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe"),
    (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe")
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6 not found. Install with: winget install --id JRSoftware.InnoSetup --exact --scope user" }

New-Item -ItemType Directory -Force (Join-Path $root "dist") | Out-Null
$sw = [Diagnostics.Stopwatch]::StartNew()
& $iscc "/Qp" "/DPublishDir=$publish" "/DAppVersion=$Version" (Join-Path $root "installer\Leaf.iss")
if ($LASTEXITCODE -ne 0) { throw "ISCC failed ($LASTEXITCODE)" }
$setup = Join-Path $root "dist\Leaf-Setup-x64.exe"
"installer: {0} ({1:N1} MB) in {2} s" -f $setup, ((Get-Item $setup).Length / 1MB), [int]$sw.Elapsed.TotalSeconds
