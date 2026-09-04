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

## 2026-09-04 — v0.1.1 (crash fix: tiles are WriteableBitmaps)

v0.1.0 crashed 10–30 s after a resize or on scroll (RO_E_CLOSED: the SoftwareBitmap behind each SoftwareBitmapSource was disposed
but XAML re-reads it). v0.1.1 keeps tile pixels in WriteableBitmaps that XAML owns; each cached tile therefore costs CPU + GPU memory.
Same 60-page document, installed Native AOT build, 3 warm runs:

| Metric | v0.1.0 | v0.1.1 |
|---|---|---|
| Time to window (warm) | 472 ms | 214 ms |
| Time to first painted page | ~950 ms | ~440 ms |
| Working set, idle | 156 MB | 208 MB |
| Private working set (Task Manager), idle | 87 MB | 139 MB |
| Working set after scrolling all 60 pages | 170 MB | 224 MB (peak 263 MB) |
| Soak (resize, scroll, zoom, minimize/restore, 60–75 s) | crashed | passed |

Tile cache budget is now clamp(2 × viewport bytes, 32 MiB, 64 MiB) and prefetch is 0.75 viewport ahead / 0.25 behind.
The idle increase is the retained CPU copies of visible + prefetched tiles; the v0.2 lever is GPU-only tiles
(VirtualSurfaceImageSource + Direct2D), which would remove the CPU copies without reintroducing disposable objects.

## Budgets (revised from measurements; enforce with /leaf-perf)
| Metric | Budget |
|---|---|
| Warm time to window | <= 500 ms |
| Warm time to first painted page (text PDF) | <= 1,000 ms |
| Private working set (Task Manager), one document idle | <= 150 MB |
| Working set after scrolling a 60-page document | <= 250 MB |
| Installer | <= 40 MB |

## Known levers for v0.2
- Startup: defer toolbar/flyout construction, lazy `ViewerControl` XAML, skip `DisplayArea` query when a saved window size exists.
- Memory: SoftwareBitmapSource keeps a GPU copy; a VirtualSurfaceImageSource + Direct2D upgrade would drop the CPU-side tile budget.
- First page on image-heavy PDFs: render the 256 px placeholder first (done), then consider progressive tile rendering.
