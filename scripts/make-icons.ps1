<#
.SYNOPSIS
  Generates Leaf.ico (app icon) and LeafDoc.ico (document icon) from vector drawing code.
  Pure PowerShell + System.Drawing; no external tools. Output: src/Leaf/Assets/*.ico
#>
param([string]$OutDir = (Join-Path $PSScriptRoot "..\src\Leaf\Assets"))
Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = "Stop"
$OutDir = [System.IO.Path]::GetFullPath($OutDir)
New-Item -ItemType Directory -Force $OutDir | Out-Null

function New-LeafPath([float]$cx, [float]$cy, [float]$r) {
    # A leaf: two mirrored quadratic-ish curves meeting at tip and stem, drawn with beziers.
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $tipX = $cx + $r * 0.62; $tipY = $cy - $r * 0.62
    $stemX = $cx - $r * 0.62; $stemY = $cy + $r * 0.62
    $p.AddBezier($stemX, $stemY, $cx - $r * 0.9, $cy - $r * 0.55, $cx + $r * 0.25, $cy - $r * 0.95, $tipX, $tipY)
    $p.AddBezier($tipX, $tipY, $cx + $r * 0.55, $cy + $r * 0.25, $cx - $r * 0.25, $cy + $r * 0.9, $stemX, $stemY)
    $p.CloseFigure()
    return $p
}

function Draw-AppIcon([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = "AntiAlias"; $g.PixelOffsetMode = "HighQuality"; $g.Clear([System.Drawing.Color]::Transparent)
    $s = [float]$size
    $radius = $s * 0.22
    $rect = New-Object System.Drawing.RectangleF 0, 0, $s, $s
    $bg = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $radius * 2
    $bg.AddArc($rect.X, $rect.Y, $d, $d, 180, 90)
    $bg.AddArc($rect.Right - $d, $rect.Y, $d, $d, 270, 90)
    $bg.AddArc($rect.Right - $d, $rect.Bottom - $d, $d, $d, 0, 90)
    $bg.AddArc($rect.X, $rect.Bottom - $d, $d, $d, 90, 90)
    $bg.CloseFigure()
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush ($rect, [System.Drawing.Color]::FromArgb(255, 46, 125, 50), [System.Drawing.Color]::FromArgb(255, 102, 187, 106), 60)
    $g.FillPath($brush, $bg)
    $leaf = New-LeafPath ($s * 0.5) ($s * 0.5) ($s * 0.36)
    $g.FillPath([System.Drawing.Brushes]::White, $leaf)
    if ($size -ge 32) {
        $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 46, 125, 50)), ([Math]::Max(1, $s * 0.035))
        $pen.StartCap = "Round"; $pen.EndCap = "Round"
        $g.DrawLine($pen, $s * 0.29, $s * 0.71, $s * 0.70, $s * 0.30)
        $pen.Dispose()
    }
    $g.Dispose(); $brush.Dispose(); $leaf.Dispose(); $bg.Dispose()
    return $bmp
}

function Draw-DocIcon([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = "AntiAlias"; $g.PixelOffsetMode = "HighQuality"; $g.Clear([System.Drawing.Color]::Transparent)
    $s = [float]$size
    # Page with folded corner
    $left = $s * 0.16; $top = $s * 0.06; $right = $s * 0.84; $bottom = $s * 0.94; $fold = $s * 0.22
    $page = New-Object System.Drawing.Drawing2D.GraphicsPath
    $page.AddLine($left, $top, $right - $fold, $top)
    $page.AddLine($right - $fold, $top, $right, $top + $fold)
    $page.AddLine($right, $top + $fold, $right, $bottom)
    $page.AddLine($right, $bottom, $left, $bottom)
    $page.CloseFigure()
    $g.FillPath([System.Drawing.Brushes]::White, $page)
    $edge = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 160, 160, 160)), ([Math]::Max(1, $s * 0.03))
    $g.DrawPath($edge, $page)
    $foldPath = New-Object System.Drawing.Drawing2D.GraphicsPath
    $foldPath.AddLine($right - $fold, $top, $right - $fold, $top + $fold)
    $foldPath.AddLine($right - $fold, $top + $fold, $right, $top + $fold)
    $foldPath.CloseFigure()
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 225, 225, 225))), $foldPath)
    $g.DrawPath($edge, $foldPath)
    # Green leaf badge centred on the page
    $cx = ($left + $right) / 2; $cy = $s * 0.58; $r = $s * 0.24
    $leaf = New-LeafPath $cx $cy $r
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 56, 142, 60))), $leaf)
    if ($size -ge 32) {
        $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), ([Math]::Max(1, $s * 0.03))
        $pen.StartCap = "Round"; $pen.EndCap = "Round"
        $g.DrawLine($pen, $cx - $r * 0.5, $cy + $r * 0.5, $cx + $r * 0.5, $cy - $r * 0.5)
        $pen.Dispose()
    }
    $g.Dispose(); $edge.Dispose(); $page.Dispose(); $foldPath.Dispose(); $leaf.Dispose()
    return $bmp
}

function Write-Ico([string]$path, [scriptblock]$draw) {
    $sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
    $pngs = @()
    foreach ($sz in $sizes) {
        $bmp = & $draw $sz
        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $pngs += ,@{ Size = $sz; Bytes = $ms.ToArray() }
        $ms.Dispose(); $bmp.Dispose()
    }
    $fs = [System.IO.File]::Create($path)
    $bw = New-Object System.IO.BinaryWriter $fs
    $bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$pngs.Count)
    $offset = 6 + 16 * $pngs.Count
    foreach ($p in $pngs) {
        $dim = if ($p.Size -ge 256) { 0 } else { $p.Size }
        $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
        $bw.Write([uint16]1); $bw.Write([uint16]32)
        $bw.Write([uint32]$p.Bytes.Length); $bw.Write([uint32]$offset)
        $offset += $p.Bytes.Length
    }
    foreach ($p in $pngs) { $bw.Write($p.Bytes) }
    $bw.Flush(); $bw.Dispose(); $fs.Dispose()
    Write-Host ("wrote {0} ({1:N0} bytes)" -f $path, (Get-Item $path).Length)
}

Write-Ico (Join-Path $OutDir "Leaf.ico") ${function:Draw-AppIcon}
Write-Ico (Join-Path $OutDir "LeafDoc.ico") ${function:Draw-DocIcon}
# Also emit 256px PNG previews for docs/README
(Draw-AppIcon 256).Save((Join-Path $OutDir "Leaf-256.png"), [System.Drawing.Imaging.ImageFormat]::Png)
(Draw-DocIcon 256).Save((Join-Path $OutDir "LeafDoc-256.png"), [System.Drawing.Imaging.ImageFormat]::Png)
