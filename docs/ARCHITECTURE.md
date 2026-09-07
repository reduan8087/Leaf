# Leaf architecture

Leaf is two projects: an engine library with no UI dependencies and a WinUI 3 shell.

## `src/Leaf.Pdfium` — engine
| Type | Responsibility |
|---|---|
| `Native/Pdfium.cs` (`NativeMethods`) | `[LibraryImport]` declarations for the ~85 pdfium functions Leaf uses. `unsigned long` → `uint`. |
| `Native/PdfFileSource` | `FPDF_FILEACCESS` over a `FileStream` opened with `FileShare.ReadWrite \| Delete` (never locks the user's file). Read failures set `Faulted`. |
| `Native/PdfFileSink` | `FPDF_FILEWRITE` over a `Stream`. The struct has no user-data field, so the block is over-allocated and the `GCHandle` sits just past the two fields pdfium reads. |
| `PdfiumThread` | The only thread that calls pdfium. Priority queue (lower runs first, FIFO within a priority), `RunAsync<T>`, Debug assertion helper. |
| `PdfDocument` | Open with password retry (UTF-8 then Latin-1), form-fill environment (so widgets draw), page table, LRU of 8 page handles, `RenderTile`, `GetTextLayout`, `Search`. |
| `PdfDocument.Editing` | `SetPageRotation`, `DeletePages`, `ReorderPages`, `ImportPages`, `SaveAs`/`SaveTo`. Each drops the page-handle LRU first and rebuilds the page table, since indices shift and reported sizes have `/Rotate` applied. |
| `PdfDocument.Navigation` | The outline as a tree (depth- and cycle-guarded) and a page's link annotations with their rectangles. |
| `PdfDocument.Metadata` | Information dictionary, permission bits, and a tolerant PDF date parser. |
| `PdfBuilder` | Composes a new document for Combine Files: pages imported by index, images as pages. Owns an `FPDF_CreateNewDocument` handle, which has no file behind it — hence a sibling of `PdfDocument`, not another constructor. |
| `PdfPageOrder` | The delete-then-reorder index maths behind Organize Pages. Lives here rather than in the UI so it can be unit-tested. |
| `PdfTextLayout` | Immutable per-page snapshot: text + a box per character in PDF points. Hit-testing, nearest-char, word boundaries, line rectangles — all pure and thread-free. |
| `PdfGeometry` | Page-point ↔ device-pixel math for view rotations 0–3 (validated against `FPDF_PageToDevice` in tests). |
| `PdfException` | Every engine failure; `Error` mirrors `FPDF_GetLastError`. |

## `src/Leaf` — WinUI 3 shell
| Type | Responsibility |
|---|---|
| `Program` | Custom `Main`: `AppInstance.FindOrRegisterForKey("Leaf")`; a second launch redirects its command line and exits before XAML loads (~50 ms). |
| `App` | Activation (initial + redirected), first-frame perf stamp, last-resort unhandled-exception handler. |
| `MainWindow` | Mica title bar with `TabView`, the app menu, drag-drop, shortcuts, password prompts, error InfoBar, "make default" prompt, full screen. One `ViewerControl` per tab. |
| `MainWindow.Documents` | What a tab holds, saving, reverting, and the prompts that stop page edits being lost silently. |
| `MainWindow.Editing` | Entry points for Organize Pages and Combine Files. |
| `Viewer/ViewerControl` | The document view: toolbar, `ScrollViewer` + `PageHostPanel`, two-phase zoom, rotation, navigation, keyboard, find bar, selection, reload on file change. |
| `Viewer/PageLayout` | Pure layout math: pages grouped into rows for the column count, rectangles in DIPs for zoom and rotation, the fit-zoom calculations. Rows stay contiguous page ranges, so viewport queries are a binary search over row tops. |
| `Viewer/ViewSettings` | `FitMode`, `SidePanelKind` and `PageArrangement` (columns × continuous × cover page). |
| `Viewer/SidePanel` | Thumbnails or bookmarks. Thumbnails render only for realized rows (`ContainerContentChanging`). |
| `Viewer/ThumbnailCache` | Bounded by count and separate from `TileCache`, so browsing a long document cannot evict the tiles being read. |
| `Viewer/PageHostPanel` | Custom `Panel` measuring to the document extent; realizes `PdfPageView` children only near the viewport. |
| `Viewer/PdfPageView` | One page: tile images on a device-pixel-aligned canvas, stale-tile layer during zoom, thumbnail placeholder, highlight/selection paths. |
| `Viewer/RenderScheduler` | Tile requests → pdfium thread (priority = distance to viewport centre, generation counter, wanted-set) → `WriteableBitmap` on the UI thread. |
| `Viewer/TileCache` | LRU by bytes, `clamp(2 × viewport bytes, 32 MiB, 64 MiB)`, pinned visible tiles. |
| `Viewer/DocumentSession` | Owns the `PdfDocument`, caches text layouts, async search wrapper. |
| `Services/DocumentWatcher` | `FileSystemWatcher` + debounce → "This file changed. Reload". |
| `Services/DefaultAppService` | Registration/default checks in the registry, `ms-settings:defaultapps?registeredAppUser=Leaf` deep link. |
| `Services/SettingsStore` | `%LOCALAPPDATA%\Leaf\settings.json` through a source-generated `JsonSerializerContext`, debounced, written via a temp file. Never throws: preferences are a convenience, not a dependency. |
| `Services/RecentFiles`, `FileDialogs`, `SaveService`, `ScratchFiles` | The recent list, the pickers, the atomic save, and the folder for documents that do not have a home yet. |
| `Organize/OrganizePagesView` | The full-window page grid. Edits are staged in `PageEditItem`s and applied on Done. |
| `Dialogs/*` | Password, document properties, save-changes, and the Combine Files queue. |
| `TestAutomation` | `LEAF_TEST_ACTIONS` scripted actions for screenshots and perf runs. |

## Rendering pipeline
1. `ScrollViewer.ViewChanged` → `RefreshVisible`: viewport rect (DIPs) → pages within viewport ±¼ (or +1/−½ viewport when prefetching) → realize/recycle page views.
2. For each realized page: device-pixel page size at `renderScale = zoom × RasterizationScale × 96/72`, tile grid of 1024 px, tiles intersecting the reach rect. Cache hits are bound to `Image`s (`Width = px / RasterizationScale`, `Stretch=Fill`, canvas with `UseLayoutRounding=false`); misses become `TileRequest`s.
3. The wanted-set is published, then requests are queued. On the pdfium thread: skip if stale/unwanted, else `FPDF_RenderPageBitmap` with negative offsets into a pooled BGRA buffer, `FPDF_FFLDraw` for form widgets.
4. On the UI thread: the buffer is copied into a `WriteableBitmap`, cache insert, LRU eviction, `TileReady` → `RefreshVisible` binds it; when all visible tiles of a page are present the stale layer is cleared. Tiles are never disposed: XAML re-reads an `Image`'s source when it re-creates surfaces, and a closed WinRT bitmap is a fatal `RO_E_CLOSED`.

Zoom: Ctrl+wheel / Ctrl+± scale the page host with a `ScaleTransform` around the cursor immediately; 150 ms later `CommitZoom` re-lays out at the new zoom, keeps the anchor fixed (`offset' = (offset + anchor)·z1/z0 − anchor`), demotes current tiles to the stale layer (scaled), bumps the generation and re-renders crisp tiles.

Page edits: Organize Pages and Combine Files stage their work and then make one pass of engine calls — import, rotate, delete descending, reorder — after which the viewer rebuilds its layout, tile cache, text layouts, link cache and panel around the same open document. Saving is the only step that touches the file, and it always writes a temp file and swaps.

Text: when a page is used for hit-testing/selection/highlights its `PdfTextLayout` is fetched once and cached on the session, so pointer handling never waits on the pdfium thread. Search runs page-by-page (starting at the current page) at a lower priority than visible tiles and streams results into the find bar.

## Threads
- UI thread: all XAML objects, caches, layout.
- `Leaf.Pdfium` thread: every pdfium call (rendering, text, search, open/close).
- Thread pool: nothing engine-related; only `Task.Run` for the single-instance redirect in a secondary process.
