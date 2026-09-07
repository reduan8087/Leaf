# Performance log

Machine: i7-11800H, 16 GB, Windows 11 25H2. Measured with `scripts/measure-perf.ps1` on the Release Native AOT
publish unless noted. "Private WS" is Task Manager's *Memory* column.

**Display matters more than anything else in this file.** Tiles are device pixels, so memory scales with the
panel. The v0.1 numbers were taken on a 1920x1200 @125 % panel (1536x960 DIP); from v0.2 the machine drives a
3072x1920 @200 % panel (1536x960 DIP, but **2.56x the pixels** for the same layout). Compare v0.2 numbers with
v0.2 numbers.

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

## 2026-09-08 — v0.2.0 (menu, page editing, view modes, panels)

Document: generated 60-page text PDF (214 KB), `artifacts/sample/long60.pdf` from `scripts/make-samples.ps1`.
5 warm runs, median. **New display: 3072x1920 @200 %**, so these are not comparable with the v0.1 rows above.

| Metric | v0.2.0 | Budget |
|---|---|---|
| Time to window (warm, median) | 274 ms | <= 500 ms |
| Time to first frame | ~500 ms | — |
| Time to first painted page | ~555 ms | <= 1,000 ms |
| Working set, document open, idle | 215 MB | — |
| Private working set (Task Manager), idle | 135–149 MB | <= 150 MB |
| Private bytes (commit), idle | ~170 MB | — |
| Working set after scrolling all 60 pages twice | 222–269 MB, settling ~240 MB | <= 250 MB |
| Publish folder without the PDB | 73.8 MB | <= 80 MB |
| Installer | 19.2 MB | <= 40 MB |

Everything is inside budget except the transient peak while scrolling, which touches 269 MB against a 250 MB
budget on a panel with 2.56x the pixels the budget was set on. At rest after scrolling it is ~240 MB.

### A pre-existing memory runaway, found and fixed

Scrolling a 60-page document at this DPI drove the working set to **2.8 GB and private bytes to 21.9 GB**.
Verified against an untouched v0.1.2 build on the same machine and document: it does the same, so this was
present since v0.1 and simply never showed up on the 125 % panel, where the same layout renders 2.56x fewer
pixels and the budget was comfortably met.

Cause: a retired tile is a tiny managed object in front of a multi-megabyte native buffer. `TileCache` evicts
correctly and `GC.RemoveMemoryPressure` keeps the accounting balanced, but nothing about dropping the last
managed reference makes the GC want to run, so the native buffers accumulated until something else forced a
collection. Fix: `ViewerControl.Retire` counts retired bytes and asks for a non-blocking, non-compacting gen2
collection every 96 MB.

| Same soak, same document, same zoom (1.82–1.83) | Peak working set | Peak private bytes |
|---|---|---|
| v0.1.2 baseline | 2,802 MB | 21,892 MB |
| v0.2.0 before the fix | 6,640 MB | 23,939 MB |
| v0.2.0 with the fix | 269 MB | 221 MB |

Note for future measurements: the same runaway appears in **Debug** builds regardless of this fix (Debug tile
handling differs), so memory must always be measured on the Release AOT publish, as the `leaf-perf` skill says.

## Budgets (revised from measurements; enforce with /leaf-perf)
| Metric | Budget |
|---|---|
| Warm time to window | <= 500 ms |
| Warm time to first painted page (text PDF) | <= 1,000 ms |
| Private working set (Task Manager), one document idle | <= 150 MB |
| Working set after scrolling a 60-page document | <= 250 MB |
| Installer | <= 40 MB |

## Known levers for v0.3
- Memory: the GC nudge is a workaround, not a cure. GPU-only tiles (VirtualSurfaceImageSource + Direct2D) would
  remove the CPU-side copy entirely and make the whole question go away.
- Startup: defer toolbar/flyout construction and lazy `ViewerControl` XAML. (The `DisplayArea` query is already
  skipped when a saved window rectangle exists, as of v0.2.)
- Memory: each tile costs a CPU-side `WriteableBitmap` plus its GPU surface; a VirtualSurfaceImageSource + Direct2D upgrade would drop the CPU-side copy entirely.
- First page on image-heavy PDFs: render the 256 px placeholder first (done), then consider progressive tile rendering.
