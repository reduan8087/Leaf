# Decisions

Format: date, decision, why, consequences. Newest first.

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
