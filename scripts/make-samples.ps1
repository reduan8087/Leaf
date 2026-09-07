<#
.SYNOPSIS
    Writes the sample PDFs and images used for manual testing, screenshots and perf runs.

.DESCRIPTION
    Everything is generated, so nothing personal ever ends up in a screenshot and the perf numbers in
    docs/PERF.md can be reproduced on any machine. Output goes to artifacts/sample, which is gitignored.

      report.pdf    12 A4 pages, an outline with a nested chapter, one internal and one web link.
      long60.pdf    60 A4 pages of text. The document docs/PERF.md measures.
      contract.pdf  2 A4 pages, and appendix.pdf 1 landscape page, for Combine Files.
      bars.png      opaque RGB bars. circle.png a blue disc on transparency, which must flatten onto white.

.EXAMPLE
    .\scripts\make-samples.ps1
#>
[CmdletBinding()]
param(
    [string]$Output = (Join-Path $PSScriptRoot "..\artifacts\sample")
)

$ErrorActionPreference = "Stop"
$Output = [System.IO.Path]::GetFullPath($Output)
New-Item -ItemType Directory -Force -Path $Output | Out-Null

function Escape-PdfText([string]$Text) {
    $Text.Replace("\", "\\").Replace("(", "\(").Replace(")", "\)")
}

# Assembles numbered objects into a valid PDF with an xref table.
function Write-Pdf([string]$Path, [string[]]$Objects, [string]$Info) {
    $body = New-Object System.Text.StringBuilder
    [void]$body.Append("%PDF-1.7`n")
    $offsets = @()
    for ($i = 0; $i -lt $Objects.Count; $i++) {
        $offsets += $body.Length
        [void]$body.Append("$($i + 1) 0 obj`n$($Objects[$i])`nendobj`n")
    }
    $xref = $body.Length
    [void]$body.Append("xref`n0 $($Objects.Count + 1)`n0000000000 65535 f `n")
    foreach ($o in $offsets) { [void]$body.Append("{0:D10} 00000 n `n" -f $o) }
    $id = "<0102030405060708090A0B0C0D0E0F10>"
    [void]$body.Append("trailer`n<< /Size $($Objects.Count + 1) /Root 1 0 R $Info /ID [$id$id] >>`nstartxref`n$xref`n%%EOF`n")

    # Latin-1: offsets in the xref table are byte offsets, so one char must be one byte.
    [System.IO.File]::WriteAllBytes($Path, [System.Text.Encoding]::Latin1.GetBytes($body.ToString()))
    "  {0,-14} {1,8:N0} bytes" -f (Split-Path -Leaf $Path), (Get-Item $Path).Length
}

# One page's content stream: a heading, body lines, and a green frame that makes page edges obvious.
function New-PageContent([string]$Heading, [string[]]$Lines, [double]$W, [double]$H) {
    $parts = @("BT /F1 30 Tf 1 0 0 1 60 $($H - 100) Tm ($(Escape-PdfText $Heading)) Tj ET")
    $y = $H - 165
    foreach ($line in $Lines) {
        if ($line) { $parts += "BT /F1 13 Tf 1 0 0 1 60 $y Tm ($(Escape-PdfText $line)) Tj ET" }
        $y -= 24
    }
    $parts += "0.15 0.55 0.28 RG 2 w 40 40 $($W - 80) $($H - 80) re S"
    $parts -join "`n"
}

function New-SimplePdf([string]$Path, [object[]]$Pages, [double]$W = 595, [double]$H = 842) {
    $objects = @("<< /Type /Catalog /Pages 2 0 R >>")
    $kids = (0..($Pages.Count - 1) | ForEach-Object { "$(4 + $_ * 2) 0 R" }) -join " "
    $objects += "<< /Type /Pages /Kids [ $kids ] /Count $($Pages.Count) >>"
    $objects += "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>"
    for ($i = 0; $i -lt $Pages.Count; $i++) {
        $pw = if ($Pages[$i].Width) { $Pages[$i].Width } else { $W }
        $ph = if ($Pages[$i].Height) { $Pages[$i].Height } else { $H }
        $objects += "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 $pw $ph] /Resources << /Font << /F1 3 0 R >> >> /Contents $(5 + $i * 2) 0 R >>"
        $content = New-PageContent $Pages[$i].Heading $Pages[$i].Lines $pw $ph
        $objects += "<< /Length $($content.Length) >>`nstream`n$content`nendstream"
    }
    Write-Pdf $Path $objects "/Info << /Title (Leaf sample) /Author (Leaf) /Creator (make-samples.ps1) >>"
}

Write-Host "Writing samples to $Output"

# --- report.pdf: 12 pages, outline with a nested chapter, two links on page 2 ---
$titles = "Cover", "Contents", "Introduction", "Method", "Results", "Discussion",
          "Appendix A", "Appendix B", "Tables", "Figures", "References", "Colophon"
$W = 595; $H = 842
$pageObj = { param($i) 4 + $i * 2 }
$outlineRoot = 4 + $titles.Count * 2
$itemStart = $outlineRoot + 1
$chapters = @(@{ Page = 0; Title = "Cover" }, @{ Page = 2; Title = "Introduction" }, @{ Page = 4; Title = "Results" },
              @{ Page = 6; Title = "Appendix A" }, @{ Page = 10; Title = "References" })
$childA = $itemStart + $chapters.Count
$linkStart = $childA + 2

$objects = @("<< /Type /Catalog /Pages 2 0 R /Outlines $outlineRoot 0 R /PageMode /UseOutlines >>")
$kids = (0..($titles.Count - 1) | ForEach-Object { "$(& $pageObj $_) 0 R" }) -join " "
$objects += "<< /Type /Pages /Kids [ $kids ] /Count $($titles.Count) >>"
$objects += "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>"
for ($i = 0; $i -lt $titles.Count; $i++) {
    $annots = if ($i -eq 1) { " /Annots [ $linkStart 0 R $($linkStart + 1) 0 R ]" } else { "" }
    $objects += "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 $W $H] /Resources << /Font << /F1 3 0 R >> >> /Contents $((& $pageObj $i) + 1) 0 R$annots >>"
    $lines = @("Leaf sample document, generated for screenshots and manual testing.",
               "It contains no personal data.", "",
               "This is page $($i + 1) of $($titles.Count).",
               "Selectable text lets Find and text selection be exercised.",
               "The green frame makes page boundaries visible in two-page and grid views.")
    if ($i -eq 1) { $lines += @("", "Jump to References (internal link)", "Visit example.org (web link)") }
    $content = New-PageContent "$($i + 1).  $($titles[$i])" $lines $W $H
    $objects += "<< /Length $($content.Length) >>`nstream`n$content`nendstream"
}
$objects += "<< /Type /Outlines /First $itemStart 0 R /Last $($itemStart + $chapters.Count - 1) 0 R /Count $($chapters.Count + 2) >>"
for ($k = 0; $k -lt $chapters.Count; $k++) {
    $num = $itemStart + $k
    $parts = @("/Title ($($chapters[$k].Title))", "/Parent $outlineRoot 0 R",
               "/Dest [ $(& $pageObj $chapters[$k].Page) 0 R /XYZ null null null ]")
    if ($k -gt 0) { $parts += "/Prev $($num - 1) 0 R" }
    if ($k -lt $chapters.Count - 1) { $parts += "/Next $($num + 1) 0 R" }
    if ($chapters[$k].Title -eq "Appendix A") { $parts += "/First $childA 0 R /Last $($childA + 1) 0 R /Count 2" }
    $objects += "<< $($parts -join ' ') >>"
}
$appendix = $itemStart + 3
$objects += "<< /Title (A.1 Tables) /Parent $appendix 0 R /Next $($childA + 1) 0 R /Dest [ $(& $pageObj 8) 0 R /XYZ null null null ] >>"
$objects += "<< /Title (A.2 Figures) /Parent $appendix 0 R /Prev $childA 0 R /Dest [ $(& $pageObj 9) 0 R /XYZ null null null ] >>"
$objects += "<< /Type /Annot /Subtype /Link /Border [0 0 0] /Rect [ 60 471 400 497 ] /Dest [ $(& $pageObj 10) 0 R /XYZ null null null ] >>"
$objects += "<< /Type /Annot /Subtype /Link /Border [0 0 0] /Rect [ 60 447 400 473 ] /A << /Type /Action /S /URI /URI (https://example.org/leaf) >> >>"
Write-Pdf (Join-Path $Output "report.pdf") $objects "/Info << /Title (Leaf Sample Report) /Author (Leaf) /Creator (make-samples.ps1) >>"

# --- long60.pdf: the document docs/PERF.md measures ---
$pages = 0..59 | ForEach-Object {
    $n = $_ + 1
    @{ Heading = "Section $n"
       Lines   = 1..20 | ForEach-Object { "Line $_ on page $n. Lorem ipsum dolor sit amet, consectetur adipiscing elit, sed do eiusmod tempor." } }
}
New-SimplePdf (Join-Path $Output "long60.pdf") $pages

# --- Combine Files inputs ---
New-SimplePdf (Join-Path $Output "contract.pdf") @(
    @{ Heading = "Contract"; Lines = @("Part one of the combine test.", "Page A1") },
    @{ Heading = "Contract"; Lines = @("Page A2") })
New-SimplePdf (Join-Path $Output "appendix.pdf") @(
    @{ Heading = "Appendix (landscape)"; Lines = @("Part two of the combine test.", "Page B1"); Width = 842; Height = 595 })

# --- Images: one opaque, one with transparency that must flatten onto white ---
Add-Type -AssemblyName System.Drawing
$bars = New-Object System.Drawing.Bitmap 320, 200
$g = [System.Drawing.Graphics]::FromImage($bars)
$g.FillRectangle([System.Drawing.Brushes]::Red, 0, 0, 107, 200)
$g.FillRectangle([System.Drawing.Brushes]::Green, 107, 0, 107, 200)
$g.FillRectangle([System.Drawing.Brushes]::Blue, 214, 0, 106, 200)
$g.Dispose()
$bars.Save((Join-Path $Output "bars.png"), [System.Drawing.Imaging.ImageFormat]::Png)
$bars.Dispose()
"  {0,-14} {1,8:N0} bytes" -f "bars.png", (Get-Item (Join-Path $Output "bars.png")).Length

$circle = New-Object System.Drawing.Bitmap 200, 200
$g = [System.Drawing.Graphics]::FromImage($circle)
$g.Clear([System.Drawing.Color]::Transparent)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$brush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 20, 120, 220))
$g.FillEllipse($brush, 20, 20, 160, 160)
$brush.Dispose(); $g.Dispose()
$circle.Save((Join-Path $Output "circle.png"), [System.Drawing.Imaging.ImageFormat]::Png)
$circle.Dispose()
"  {0,-14} {1,8:N0} bytes" -f "circle.png", (Get-Item (Join-Path $Output "circle.png")).Length

Write-Host "done"
