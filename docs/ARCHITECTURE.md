# Leaf architecture

Leaf is two projects: an engine library with no UI dependencies and a WinUI 3 shell.

## `src/Leaf.Pdfium` — engine
| Type | Responsibility |
|---|---|
| `Native/Pdfium.cs` (`NativeMethods`) | `[LibraryImport]` declarations for the ~60 pdfium functions Leaf uses. `unsigned long` → `uint`. |
| `Native/PdfFileSource` | `FPDF_FILEACCESS` over a `FileStream` opened with `FileShare.ReadWrite \| Delete` (never locks the user's file). Read failures set `Faulted`. |
| `PdfiumThread` | The only thread that calls pdfium. Priority queue (lower runs first, FIFO within a priority), `RunAsync<T>`, Debug assertion helper. |
| `PdfDocument` | Open with password retry (UTF-8 then Latin-1), form-fill environment (so widgets draw), page table, LRU of 8 page handles, `RenderTile`, `GetTextLayout`, `Search`. |
| `PdfTextLayout` | Immutable per-page snapshot: text + a box per character in PDF points. Hit-testing, nearest-char, word boundaries, line rectangles — all pure and thread-free. |
| `PdfGeometry` | Page-point ↔ device-pixel math for view rotations 0–3 (validated against `FPDF_PageToDevice` in tests). |
| `PdfException` | Every engine failure; `Error` mirrors `FPDF_GetLastError`. |

## `src/Leaf` — WinUI 3 shell
| Type | Responsibility |
|---|---|
| `Program` | Custom `Main`: `AppInstance.FindOrRegisterForKey("Leaf")`; a second launch redirects its command line and exits before XAML loads (~50 ms). |
| `App` | Activation (initial + redirected), first-frame perf stamp, last-resort unhandled-exception handler. |
| `MainWindow` | Mica title bar with `TabView`, app menu, drag-drop, Ctrl+O/W/Tab, password prompts, error InfoBar, "make default" prompt. One `ViewerControl` per tab. |
| `Viewer/ViewerControl` | The document view: toolbar, `ScrollViewer` + `PageHostPanel`, two-phase zoom, rotation, navigation, keyboard, find bar, selection, reload on file change. |
| `Viewer/PageLayout` | Pure layout math: page rectangles in DIPs for zoom/rotation, prefix sums, fit-width/fit-page. |
| `Viewer/PageHostPanel` | Custom `Panel` measuring to the document extent; realizes `PdfPageView` children only near the viewport. |
| `Viewer/PdfPageView` | One page: tile images on a device-pixel-aligned canvas, stale-tile layer during zoom, thumbnail placeholder, highlight/selection paths. |
| `Viewer/RenderScheduler` | Tile requests → pdfium thread (priority = distance to viewport centre, generation counter, wanted-set) → `SoftwareBitmapSource` on the UI thread. |
| `Viewer/TileCache` | LRU by bytes (64–128 MiB depending on viewport), pinned visible tiles. |
| `Viewer/DocumentSession` | Owns the `PdfDocument`, caches text layouts, async search wrapper. |
| `Services/DocumentWatcher` | `FileSystemWatcher` + debounce → "This file changed. Reload". |
| `Services/DefaultAppService` | Registration/default checks in the registry, `ms-settings:defaultapps?registeredAppUser=Leaf` deep link. |
| `TestAutomation` | `LEAF_TEST_ACTIONS` scripted actions for screenshots and perf runs. |

## Rendering pipeline
1. `ScrollViewer.ViewChanged` → `RefreshVisible`: viewport rect (DIPs) → pages within viewport ±¼ (or +1/−½ viewport when prefetching) → realize/recycle page views.
2. For each realized page: device-pixel page size at `renderScale = zoom × RasterizationScale × 96/72`, tile grid of 1024 px, tiles intersecting the reach rect. Cache hits are bound to `Image`s (`Width = px / RasterizationScale`, `Stretch=Fill`, canvas with `UseLayoutRounding=false`); misses become `TileRequest`s.
3. The wanted-set is published, then requests are queued. On the pdfium thread: skip if stale/unwanted, else `FPDF_RenderPageBitmap` with negative offsets into a pooled BGRA buffer, `FPDF_FFLDraw` for form widgets, copy into a `SoftwareBitmap`.
4. On the UI thread: `SoftwareBitmapSource.SetBitmapAsync`, cache insert, LRU eviction, `TileReady` → `RefreshVisible` binds it; when all visible tiles of a page are present the stale layer is cleared.

Zoom: Ctrl+wheel / Ctrl+± scale the page host with a `ScaleTransform` around the cursor immediately; 150 ms later `CommitZoom` re-lays out at the new zoom, keeps the anchor fixed (`offset' = (offset + anchor)·z1/z0 − anchor`), demotes current tiles to the stale layer (scaled), bumps the generation and re-renders crisp tiles.

Text: when a page is used for hit-testing/selection/highlights its `PdfTextLayout` is fetched once and cached on the session, so pointer handling never waits on the pdfium thread. Search runs page-by-page (starting at the current page) at a lower priority than visible tiles and streams results into the find bar.

## Threads
- UI thread: all XAML objects, caches, layout.
- `Leaf.Pdfium` thread: every pdfium call (rendering, text, search, open/close).
- Thread pool: nothing engine-related; only `Task.Run` for the single-instance redirect in a secondary process.
