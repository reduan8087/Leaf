# Decisions

Format: date, decision, why, consequences. Newest first.

## 2026-09-08 — v0.2.0, not v1.0.0
The user asked for "v1.0.0" for this release. Pushed back: `docs/MVP-SCOPE.md` already defines exactly this batch of
features (bookmarks, thumbnails, links, recent files) as "planned v0.2+", semver bumps the minor for a pre-1.0 feature
release, and a PDF reader announcing 1.0 without a Print command reads as incomplete. Agreed ladder: 0.2.0 now,
0.3.0 printing, 1.0.0 when print and code signing land — the point at which Acrobat can actually be uninstalled.

## 2026-09-08 — The engine gains a write side
v0.1 bound only read APIs. Combine and Organize need ~20 more `[LibraryImport]`s (import/move/delete/rotate pages,
create documents, embed images, save), all of them already exported by the `pdfium.dll` we ship, so no new dependency
and no different build. Consequences:
- `FPDF_FILEWRITE` has no user-data field, unlike `FPDF_FILEACCESS`: pdfium hands the struct pointer back to the
  callback. `PdfFileSink` over-allocates the block and keeps its `GCHandle` just past the two fields pdfium reads.
- Every structural edit drops the page-handle LRU first and rebuilds the page table, because cached handles would
  dangle once indices shift and reported page sizes have `/Rotate` applied.
- `PdfError` gained `Write = 100`, deliberately outside 1..6: those mirror `FPDF_ERR_*` and are cast straight from
  `FPDF_GetLastError`.

## 2026-09-08 — Saving writes to a temp file and then reopens the document
`PdfFileSource` reads pages lazily from a handle that stays open for the document's life. Writing straight over that
file would corrupt every page not yet read, and a failed write would truncate the user's PDF. So `SaveService` writes
beside the target and swaps with `File.Replace`, and the caller reopens afterwards. The visible reload is the price of
never being able to damage a source file. Rejected: saving in place directly (fast, occasionally destructive) and
copy-on-open (doubles memory and startup cost for every document, to help the rare one that gets edited).

## 2026-09-08 — Page edits are staged, not applied as you go
Organize Pages holds `PageEditItem`s and only touches the document on Done, so Cancel genuinely discards and a
900-page reorder is one engine call rather than hundreds. Pages inserted from another PDF keep pointing at their own
open document until then, which is what makes them cancellable too. `PdfPageOrder` owns the delete-then-reorder index
maths and lives in the engine project so it can be unit-tested: deleting renumbers everything after it, so a surviving
page's new index is its rank among the survivors, and getting that wrong silently scrambles a document.

## 2026-09-08 — View modes are two axes, not a list of named modes
Column count (1/2/N) and continuous scrolling are chosen independently, plus "cover page on its own". That covers
Acrobat's Single Page View / Enable Scrolling / Two Page View / Two Page Scrolling from two controls, and generalises
to a grid for free. `PageLayout` groups pages into rows; rows stay contiguous page ranges in order, which keeps
viewport queries a binary search and lets the panel go on realizing a simple first..last range. Without continuous
scrolling only the anchor row is laid out, so turning the page is a re-layout rather than a scroll.

## 2026-09-08 — Full screen is F11, not Ctrl+F
The user first asked for Ctrl+F. `Ctrl+F` is Find in Leaf and in essentially every Windows application, and a reader
needs Find constantly. F11 is the Windows-wide fullscreen convention; Ctrl+L is bound as well for Acrobat muscle
memory, and Esc leaves. The viewer now lets Escape bubble when it has no find bar or selection to dismiss.

## 2026-09-08 — Combine accepts images, and JPEGs go in untouched
Merging scans with a PDF is the common case, so `.jpg .jpeg .png .bmp .tif .tiff .webp` are accepted. A JPEG with no
EXIF rotation is embedded byte-for-byte through `FPDFImageObj_LoadJpegFileInline`: re-encoding a photo as a raw
bitmap would inflate the output enormously. Everything else is decoded through WIC on the UI side (the engine project
stays free of Windows types), honouring EXIF orientation and flattened onto white, because a PDF page is opaque and a
transparent PNG would otherwise print black.

## 2026-09-08 — WinRT cannot see IReadOnlyList<T> through a binding
`TreeViewItem.ItemsSource` bound to an `IReadOnlyList<T>` silently produced an empty collection, so nested bookmarks
vanished with no error. Collections exposed to XAML through WinRT must be `IList`. Worth remembering: it fails
quietly, which is the worst way for a binding to fail.

## 2026-09-04 — Name: Leaf
Chosen by the user over Wren/Breeze/PDFViewer. Exe `Leaf.exe`, ProgId `Leaf.Document`, AUMID `Leaf`, install dir `%LOCALAPPDATA%\Programs\Leaf`.

## 2026-09-04 — Engine: PDFium via own LibraryImport bindings
Alternatives: Windows.Data.Pdf (zero deps, but rendering-only: no text, search, selection, links, outline, PNG round trip per render),
managed wrappers (PDFiumCore: generated IntPtr API without AOT annotations; PDFtoImage: +9.6 MB SkiaSharp, render-only; Docnet: 2022 pdfium, dormant).
Why: one ~5-7 MB native DLL, fastest path into BGRA tiles, full feature ceiling, first-class Native AOT, no third-party managed dependency.
Consequences: we own ~60 declarations; pdfium is single-threaded so all calls run on `PdfiumThread`; form widgets need `FPDF_FFLDraw`.

## 2026-09-04 — UI: WinUI 3 on Windows App SDK 2.x, lean package reference
Reference only `Microsoft.WindowsAppSDK.WinUI` (2.3.6); the 2.4.0 meta-package pulls AI/ML/Search/Widgets/WebView2.
Self-contained WinUI payload is ~51 MB for win-x64; accepted for a no-prerequisite installer.

## 2026-09-04 — Deployment: unpackaged, self-contained, Native AOT, Inno Setup per-user
Alternatives: MSIX (needs certificate trust before install; heavier pipeline). Framework-dependent (two runtime installs on the target).
Why: one double-click, no admin, no prerequisites, fastest startup. Windows 11 forbids silent default-app changes, so the installer and the app
deep-link to Settings > Default apps (`ms-settings:defaultapps?registeredAppUser=Leaf`).

## 2026-09-04 — Viewer: ScrollViewer + custom Panel + 1024 px tiles + SoftwareBitmapSource
Alternatives: ItemsRepeater (leak/crash fixes only in 2.1.3/2.3.1, hidden realization window), ScrollView (compositor zoom; harder flicker-free relayout),
whole-page bitmaps (82 MiB per Letter page at 400 % on the 125 % panel), WriteableBitmap (copy must happen on the UI thread), Win2D (WinUI 3 port
"work in progress", no AOT statement, pins WinUI 1.8).
Why: predictable memory bounded by the screen, crisp 1:1 device pixels, minimal dependencies. Zero-copy D2D surfaces are the v0.2 upgrade.

## 2026-09-04 — MVP includes Find and text selection
User choice: "Viewing + Find + text selection". Text layout per realized page is cached (char boxes + text) so hit-testing runs on the UI thread.

## 2026-09-04 — One window with tabs, single process
User choice. Second launches redirect through `AppInstance.RedirectActivationToAsync` before XAML loads.

## 2026-09-04 — Native AOT publish workarounds (scripts/publish.ps1)
Two machine-specific gaps: the Windows Kits\10 folder is missing (registry lists SDK 26100 but no files), so the MSVC link step could
not find kernel32.lib/ucrt.lib; and VS 2022's vcvarsall.bat prints a `vswhere.exe` error that corrupts the linker path the ILCompiler
targets parse. Fix: publish.ps1 fetches the official `Microsoft.Windows.SDK.CPP.x64` NuGet package once into `tools/winsdk-libs` and passes
its `um\x64` and `ucrt\x64` folders as `/LIBPATH` linker args (`LeafWinSdkLibRoot`), and prepends the VS Installer folder to PATH.
When a real Windows SDK is installed the script uses it automatically. Alternative rejected: installing the Windows SDK requires admin.

## 2026-09-04 — Copy XAML resources into the AOT publish folder
The AOT publish pipeline dropped `Leaf.pri`/`*.xbf`; WinUI then crashed at startup with a stowed exception (0xC000027B, E_FAIL).
`Leaf.csproj` copies `$(OutDir)*.pri;*.xbf` after Publish. Keep this until the Windows App SDK targets handle it.

## 2026-09-04 — Perf budgets set from measurements, not guesses
WinUI 3 alone costs ~113 MB working set and ~200 ms warm window-up, so the original "idle WS <= 90 MB" was unattainable. Budgets now:
window <= 500 ms, first page <= 1 s, private WS <= 150 MB idle, WS <= 250 MB after scrolling 60 pages, installer <= 40 MB (docs/PERF.md).

## 2026-09-04 — v0.1.1: tiles are WriteableBitmaps, nothing disposable behind an Image
v0.1.0 crashed 10–30 s after a resize or on scroll with 0xC000027B / RO_E_CLOSED: `RenderScheduler` disposed the `SoftwareBitmap`
right after `SoftwareBitmapSource.SetBitmapAsync`, but XAML keeps a reference and re-reads it when it re-creates image surfaces.
Fix: render into a pooled buffer, copy into a `WriteableBitmap` on the UI thread, never dispose; the cache returns retired entries
and the viewer unbinds them from Images first (`Retire`). Memory pressure is reported to the GC so native pixel memory is collected.
Rule (project conventions): never Dispose a WinRT bitmap an Image may still reference.

## 2026-09-04 — v0.1.2: icon design "B · Document"
User chose design B from three directions (A emblem tile, B green page with folded corner, C gradient monogram). File icon
`Assets/LeafPdf.ico`: green page (#22A044→#157A33) with a #8FE08F fold, white leaf, "PDF" on a white label; sizes <= 32 px drop the
label. App icon `Assets/Leaf.ico`: rounded tile in the same greens with the leaf. Both are rasterised per size from SVG by
`scripts/make-icons.ps1` using headless Chrome/Edge (private profile, 512 px viewport cropped) and packed as PNG-entry ICOs.
The document icon file was renamed (LeafDoc → LeafPdf) so Explorer's icon cache picks up the new art without a cache rebuild.
