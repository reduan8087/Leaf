<#
.SYNOPSIS
  Native AOT publish of Leaf to artifacts/publish (win-x64, self-contained, no prerequisites on the target).

  Works around two toolchain gaps on this machine:
  1. VS 2022's vcvarsall.bat prints a 'vswhere.exe' error unless the VS Installer folder is on PATH, and that
     error line corrupts the linker path that the ILCompiler targets parse. We prepend the folder to PATH.
  2. The Windows Kits\10 folder is missing (registry lists SDK 26100 but the files are gone), so the MSVC linker
     cannot find kernel32.lib/ucrt.lib. We use the official Microsoft.Windows.SDK.CPP.x64 NuGet package instead
     (downloaded once into tools/winsdk-libs) and pass its lib folders to the linker.
  When a real Windows SDK is installed, the script detects it and skips the NuGet libs.
.EXAMPLE
  pwsh -NoProfile -File scripts/publish.ps1
  pwsh -NoProfile -File scripts/publish.ps1 -Output artifacts/publish -Clean
#>
[CmdletBinding()]
param(
    [string]$Output = "artifacts/publish",
    [switch]$Clean,
    [string]$SdkLibsVersion = ""   # e.g. 10.0.26100.4654; empty = latest 26100.* on nuget.org or whatever is cached
)
$ErrorActionPreference = "Stop"
$root = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
Set-Location $root

# 1. vswhere on PATH for vcvarsall.bat
$vsInstaller = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer"
if ((Test-Path (Join-Path $vsInstaller "vswhere.exe")) -and ($env:Path -notlike "*$vsInstaller*")) {
    $env:Path = "$vsInstaller;$env:Path"
}

# 2. Windows SDK import libraries
$extraLibDirs = @()
$kitsLib = Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10\Lib"
$sdkOnDisk = if (Test-Path $kitsLib) { Get-ChildItem $kitsLib -Directory | Where-Object { Test-Path (Join-Path $_.FullName "um\x64\kernel32.lib") } | Sort-Object Name -Descending | Select-Object -First 1 } else { $null }
if ($sdkOnDisk) {
    Write-Host "Windows SDK found on disk: $($sdkOnDisk.FullName)"
}
else {
    $toolsRoot = Join-Path $root "tools\winsdk-libs"
    $cached = if (Test-Path $toolsRoot) { Get-ChildItem $toolsRoot -Directory | Where-Object { Test-Path (Join-Path $_.FullName "c\um\x64\kernel32.lib") } | Sort-Object Name -Descending | Select-Object -First 1 } else { $null }
    if (-not $cached -or ($SdkLibsVersion -and $cached.Name -ne $SdkLibsVersion)) {
        $ver = $SdkLibsVersion
        if (-not $ver) {
            $vers = ((Invoke-WebRequest -Uri "https://api.nuget.org/v3-flatcontainer/microsoft.windows.sdk.cpp.x64/index.json" -UseBasicParsing -TimeoutSec 30).Content | ConvertFrom-Json).versions |
                Where-Object { $_ -like "10.0.26100.*" -and $_ -notmatch "preview" }
            $ver = $vers | Select-Object -Last 1
        }
        $dest = Join-Path $toolsRoot $ver
        Write-Host "Fetching Microsoft.Windows.SDK.CPP.x64 $ver (x64 import libraries) into $dest ..."
        New-Item -ItemType Directory -Force $dest | Out-Null
        $nupkg = Join-Path $env:TEMP "microsoft.windows.sdk.cpp.x64.$ver.nupkg"
        Invoke-WebRequest -Uri "https://api.nuget.org/v3-flatcontainer/microsoft.windows.sdk.cpp.x64/$ver/microsoft.windows.sdk.cpp.x64.$ver.nupkg" -OutFile $nupkg -UseBasicParsing -TimeoutSec 900
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $zip = [System.IO.Compression.ZipFile]::OpenRead($nupkg)
        try {
            foreach ($e in $zip.Entries) {
                if ($e.FullName -match "^c/(um|ucrt)/x64/" -and $e.Name) {
                    $file = Join-Path $dest ($e.FullName -replace "/", "\")
                    New-Item -ItemType Directory -Force (Split-Path $file) | Out-Null
                    [System.IO.Compression.ZipFileExtensions]::ExtractToFile($e, $file, $true)
                }
            }
        }
        finally { $zip.Dispose() }
        [System.IO.File]::Delete($nupkg)
        $cached = Get-Item $dest
    }
    Write-Host "Using SDK import libraries from NuGet: $($cached.FullName)"
    $extraLibDirs = @((Join-Path $cached.FullName "c"))
}

if ($Clean) {
    Remove-Item -Recurse -Force -ErrorAction SilentlyContinue "src\Leaf\obj\x64\Release", "src\Leaf.Pdfium\obj\x64\Release", $Output
}

$props = @("-p:Platform=x64")
if ($extraLibDirs.Count -gt 0) {
    # Consumed by src/Leaf/Leaf.csproj as <LinkerArg Include="/LIBPATH:...um\x64 and ucrt\x64"/>
    $props += "-p:LeafWinSdkLibRoot=" + $extraLibDirs[0]
}

$sw = [Diagnostics.Stopwatch]::StartNew()
Write-Host "dotnet publish src/Leaf -c Release -r win-x64 -o $Output $($props -join ' ')"
& dotnet publish src/Leaf/Leaf.csproj -c Release -r win-x64 -o $Output -nologo -v:m @props
if ($LASTEXITCODE -ne 0) { throw "publish failed ($LASTEXITCODE)" }
Write-Host ("publish OK in {0} s" -f [int]$sw.Elapsed.TotalSeconds)

$exe = Join-Path $Output "Leaf.exe"
if (Test-Path $exe) {
    "publish folder: {0:N1} MB" -f ((Get-ChildItem $Output -Recurse -File | Measure-Object Length -Sum).Sum / 1MB)
    Get-ChildItem $Output -File | Sort-Object Length -Descending | Select-Object -First 10 | ForEach-Object { "  {0,-48} {1,8:N2} MB" -f $_.Name, ($_.Length / 1MB) }
}
