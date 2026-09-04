# Performance log

Machine: i7-11800H, 16 GB, Windows 11 25H2, laptop panel 1920x1200 @125 %. Measured with `scripts/measure-perf.ps1`
on the Release Native AOT publish unless noted. "Private WS" is Task Manager's *Memory* column.

## 2026-09-04 — MVP v0.1 (commit after viewer + find + selection)

Document: 60-page text/graphics PDF (245 KB). 5 warm runs, median.

| Metric | Leaf | Adobe Acrobat 26.001 (same file) |
|---|---|---|
| Time to window | 472 ms | 2,884 ms |
| Time to first frame | ~750 ms | n/a |
| Time to first painted page | ~950 ms | n/a |
| Processes | 1 | 9 (Acrobat ×2, AcroCEF ×5, AdobeCollabSync ×2) |
| Working set, document open, idle | 156 MB | 541 MB (sum of processes) |
| Private working set (Task Manager) | 87 MB | ~250 MB (sum) |
| Private bytes (commit) | 110 MB | 336 MB |
| Working set after scrolling all 60 pages | 170 MB (peak 202 MB) | not measured |
| Redirect of a second launch to the running window | 49 ms | n/a |

8 MB, 23-page image-heavy guide: window 422 ms, first page ~1.9 s (large raster page), working set 167 MB idle.

Empty shell (no document): window ~200 ms warm / 570 ms first run, working set 113 MB, private 74 MB —
this is the WinUI 3 framework baseline and the floor for any WinUI app.

Publish folder 107 MB including a 36 MB PDB (excluded from the installer); Leaf.exe 7.6 MB; pdfium.dll 6.9 MB.

## Budgets (revised from measurements; enforce with /leaf-perf)
| Metric | Budget |
|---|---|
| Warm time to window | <= 500 ms |
| Warm time to first painted page (text PDF) | <= 1,000 ms |
| Private working set, one document idle | <= 100 MB |
| Working set after scrolling a 60-page document | <= 200 MB |
| Installer | <= 40 MB |

## Known levers for v0.2
- Startup: defer toolbar/flyout construction, lazy `ViewerControl` XAML, skip `DisplayArea` query when a saved window size exists.
- Memory: SoftwareBitmapSource keeps a GPU copy; a VirtualSurfaceImageSource + Direct2D upgrade would drop the CPU-side tile budget.
- First page on image-heavy PDFs: render the 256 px placeholder first (done), then consider progressive tile rendering.
