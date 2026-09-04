<#
.SYNOPSIS
  Generates Leaf's icons from vector artwork (design "B · Document", chosen 2026-09-04):
    src/Leaf/Assets/Leaf.ico     - app icon (rounded green tile with the leaf): exe resource, window, taskbar, Start
    src/Leaf/Assets/LeafPdf.ico  - .pdf document icon (green page with folded corner, leaf, PDF label)
  Every size (16 ... 256) is rasterised natively from SVG with headless Chrome or Edge so edges stay crisp;
  sizes <= 32 px use a simplified drawing without the PDF label. Also writes 256 px PNG previews.
#>
param([string]$OutDir = (Join-Path $PSScriptRoot "..\src\Leaf\Assets"))
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing
$OutDir = [System.IO.Path]::GetFullPath($OutDir)
New-Item -ItemType Directory -Force $OutDir | Out-Null
$work = Join-Path $env:TEMP "leaf-icons"
New-Item -ItemType Directory -Force $work | Out-Null

$browser = @(
    "C:\Program Files\Google\Chrome\Application\chrome.exe",
    "C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
    "$env:LOCALAPPDATA\Google\Chrome\Application\chrome.exe",
    "C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
    "C:\Program Files\Microsoft\Edge\Application\msedge.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $browser) { throw "Neither Chrome nor Edge found for SVG rasterisation." }

# ---- artwork (256 x 256 design space) ----------------------------------------------------------------
$leaf = 'M64 194 C 56 118, 120 50, 206 50 C 208 136, 144 200, 64 194 Z'
$font = "font-family=`"'Segoe UI Black','Segoe UI','Arial Black',Arial,sans-serif`" font-weight=`"900`""
$defs = @"
<defs>
  <linearGradient id="gB" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#22a044"/><stop offset="1" stop-color="#157a33"/></linearGradient>
  <path id="leaf" d="$leaf"/>
</defs>
"@
# Document icon, full detail (>= 40 px)
$docFull = @"
$defs
<path d="M52 30 Q52 14 68 14 H162 L212 64 V226 Q212 242 196 242 H68 Q52 242 52 226 Z" fill="url(#gB)"/>
<path d="M162 14 V54 Q162 64 172 64 H212 Z" fill="#8fe08f"/>
<g transform="translate(46 30) scale(0.64)"><use href="#leaf" fill="#ffffff"/><path d="M78 182 L 192 66" stroke="#1d8f3d" stroke-width="14" stroke-linecap="round" fill="none"/></g>
<rect x="76" y="176" width="112" height="42" rx="10" fill="#ffffff"/>
<text x="132" y="208" text-anchor="middle" $font font-size="34" letter-spacing="2" fill="#157a33">PDF</text>
"@
# Document icon, small (<= 32 px): page + leaf only, thicker strokes
$docSmall = @"
$defs
<path d="M44 26 Q44 8 62 8 H160 L220 68 V230 Q220 248 202 248 H62 Q44 248 44 230 Z" fill="#1d8f3d"/>
<path d="M160 8 V58 Q160 68 170 68 H220 Z" fill="#8fe08f"/>
<g transform="translate(40 52) scale(0.72)"><use href="#leaf" fill="#ffffff"/><path d="M78 182 L 192 66" stroke="#1d8f3d" stroke-width="18" stroke-linecap="round" fill="none"/></g>
"@
# App icon: rounded tile in the same greens with the leaf
$appFull = @"
$defs
<rect width="256" height="256" rx="58" fill="url(#gB)"/>
<g transform="translate(10 4) scale(0.92)"><use href="#leaf" fill="#ffffff"/><path d="M78 182 L 192 66" stroke="#1d8f3d" stroke-width="13" stroke-linecap="round" fill="none"/></g>
"@
$appSmall = @"
$defs
<rect width="256" height="256" rx="64" fill="#1d8f3d"/>
<g transform="translate(8 8) scale(0.94)"><use href="#leaf" fill="#ffffff"/><path d="M78 182 L 192 66" stroke="#1d8f3d" stroke-width="17" stroke-linecap="round" fill="none"/></g>
"@

function Render-Png([string]$svgBody, [int]$size, [string]$png) {
    # Chrome refuses tiny windows, so render into a 512 px viewport with the icon at the top-left and crop.
    $frame = 512
    $html = Join-Path $work ("frame-{0}.html" -f [guid]::NewGuid().ToString("N").Substring(0, 8))
    $shot = Join-Path $work ("shot-{0}.png" -f [guid]::NewGuid().ToString("N").Substring(0, 8))
    @"
<!doctype html><html><head><meta charset="utf-8"><style>html,body{margin:0;padding:0;background:transparent;overflow:hidden}svg{display:block;position:absolute;left:0;top:0}</style></head>
<body><svg xmlns="http://www.w3.org/2000/svg" width="$size" height="$size" viewBox="0 0 256 256">$svgBody</svg></body></html>
"@ | Set-Content -Path $html -Encoding UTF8
    # A private profile keeps this launch from being handed off to an already-running browser (which would exit without a screenshot).
    $profile = Join-Path $work "profile"
    $url = "file:///" + ($html -replace '\\', '/')
    $chromeArgs = @("--headless=new", "--disable-gpu", "--hide-scrollbars", "--force-device-scale-factor=1", "--default-background-color=00000000",
              "--user-data-dir=$profile", "--no-first-run", "--no-default-browser-check", "--disable-extensions",
              "--window-size=$frame,$frame", "--screenshot=$shot", $url)
    $chromeOut = & $browser @chromeArgs 2>&1 | Out-String
    if (-not (Test-Path $shot)) { throw "rasterisation failed for $png (no screenshot produced by $browser). Output: $chromeOut" }
    [System.IO.File]::Delete($html)
    $full = [System.Drawing.Bitmap]::FromFile($shot)
    try {
        $corner = $full.GetPixel($frame - 1, $frame - 1)
        if ($corner.A -ne 0) { throw "background is not transparent (alpha $($corner.A)); check --default-background-color support" }
        $crop = $full.Clone((New-Object System.Drawing.Rectangle 0, 0, $size, $size), [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        try { $crop.Save($png, [System.Drawing.Imaging.ImageFormat]::Png) } finally { $crop.Dispose() }
    } finally { $full.Dispose(); [System.IO.File]::Delete($shot) }
}

function Write-Ico([string]$path, [string]$fullSvg, [string]$smallSvg) {
    $sizes = 16, 20, 24, 32, 40, 48, 64, 96, 128, 256
    $entries = @()
    foreach ($sz in $sizes) {
        $png = Join-Path $work ("{0}-{1}.png" -f [System.IO.Path]::GetFileNameWithoutExtension($path), $sz)
        if (Test-Path $png) { [System.IO.File]::Delete($png) }
        Render-Png $(if ($sz -le 32) { $smallSvg } else { $fullSvg }) $sz $png
        $entries += ,@{ Size = $sz; Bytes = [System.IO.File]::ReadAllBytes($png) }
    }
    $fs = [System.IO.File]::Create($path)
    $bw = New-Object System.IO.BinaryWriter $fs
    $bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$entries.Count)
    $offset = 6 + 16 * $entries.Count
    foreach ($e in $entries) {
        $dim = if ($e.Size -ge 256) { 0 } else { $e.Size }
        $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
        $bw.Write([uint16]1); $bw.Write([uint16]32)
        $bw.Write([uint32]$e.Bytes.Length); $bw.Write([uint32]$offset)
        $offset += $e.Bytes.Length
    }
    foreach ($e in $entries) { $bw.Write($e.Bytes) }
    $bw.Flush(); $bw.Dispose(); $fs.Dispose()
    Write-Host ("wrote {0} ({1:N0} bytes, {2} sizes)" -f $path, (Get-Item $path).Length, $entries.Count)
}

Write-Ico (Join-Path $OutDir "Leaf.ico") $appFull $appSmall
Write-Ico (Join-Path $OutDir "LeafPdf.ico") $docFull $docSmall
Copy-Item (Join-Path $work "Leaf-256.png") (Join-Path $OutDir "Leaf-256.png") -Force
Copy-Item (Join-Path $work "LeafPdf-256.png") (Join-Path $OutDir "LeafPdf-256.png") -Force
Copy-Item (Join-Path $work "LeafPdf-48.png") (Join-Path $OutDir "LeafPdf-48.png") -Force
Copy-Item (Join-Path $work "LeafPdf-16.png") (Join-Path $OutDir "LeafPdf-16.png") -Force
$old = Join-Path $OutDir "LeafDoc.ico"; if (Test-Path $old) { [System.IO.File]::Delete($old) }
$oldPng = Join-Path $OutDir "LeafDoc-256.png"; if (Test-Path $oldPng) { [System.IO.File]::Delete($oldPng) }
Write-Host "done"
