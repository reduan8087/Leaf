# Leaf

Leaf is a personal replacement for Adobe Acrobat Reader on Windows 11: a native C#/.NET 10 + WinUI 3 PDF reader.
Priorities, in order: **stable > fast cold start > small memory > small install**. MVP scope is in @docs/MVP-SCOPE.md.

## Stack (do not drift)
- .NET 10 SDK, `net10.0-windows10.0.26100.0`, x64 only, C# latest, nullable on, warnings are errors.
- Windows App SDK 2.x: reference **only** `Microsoft.WindowsAppSDK.WinUI` (never the meta-package: it drags in AI/ML/Search/WebView2).
- UNPACKAGED app (`WindowsPackageType=None`), self-contained, **Native AOT** in Release publish. Ships via Inno Setup.
- PDF engine: PDFium native DLL from `bblanchon.PDFium.Win32`, called through our own `[LibraryImport]` bindings in `src/Leaf.Pdfium`. No managed PDF wrappers.

## Commands (details in the `leaf-build` skill)
- Build:   `dotnet build Leaf.slnx -c Debug -p:Platform=x64`
- Run:     `dotnet run --project src/Leaf -c Debug -p:Platform=x64 -- "path\to\file.pdf"`
- Test:    `dotnet test tests/Leaf.Pdfium.Tests -c Release`
- Publish: `dotnet publish src/Leaf -c Release -r win-x64 -o artifacts/publish` (AOT; takes minutes)
- Installer: `/leaf-installer build` (never run automatically)
- Perf:    `/leaf-perf` (scripts/measure-perf.ps1)

## Hard rules
- IMPORTANT: this app is unpackaged and installed by Inno Setup. Ignore `winapp run`, `Package.appxmanifest` and MSIX advice from the `winui` plugin skills; use the commands above.
- AOT/trim-safe only: `{x:Bind}` (never `{Binding}`), every class used from XAML or as a binding source is `partial`, no reflection, no `dynamic`, JSON via `System.Text.Json` source generators, P/Invoke via `[LibraryImport]`, native callbacks via `[UnmanagedCallersOnly]`, COM via `[GeneratedComInterface]`. Do not suppress IL2xxx/IL3xxx warnings without a comment explaining why.
- pdfium is not thread-safe: every `FPDF_*` call runs on the single `PdfiumThread`. Never call bindings from the UI thread or the thread pool.
- Rendering is tile-based (1024 px device tiles, LRU cache with a byte budget). Never allocate a whole-page bitmap at high zoom.
- Tiles are `WriteableBitmap`s. Never use `SoftwareBitmapSource`/`SoftwareBitmap` for anything an `Image` shows, and never `Dispose()` a WinRT bitmap XAML may still read: XAML re-reads sources on resize/re-bind and a closed object is a fatal RO_E_CLOSED (0xC000027B) crash.
- A bad PDF must never crash the process: engine errors become `PdfException`, the UI shows a dialog/InfoBar.
- Never lock the user's file: open with `FileShare.ReadWrite | FileShare.Delete`.
- Keep dependencies minimal. Adding a NuGet package needs a reason in docs/DECISIONS.md.

## Layout
- `src/Leaf.Pdfium` engine (bindings, PdfiumThread, PdfDocument, renderer, text, search) — no UI references.
- `src/Leaf` WinUI app (Program/App/MainWindow, Viewer/*, Services/*, Dialogs/*, Assets/*).
- `tests/Leaf.Pdfium.Tests` xunit; generated fixture PDFs; opt-in corpus smoke test via `LEAF_CORPUS_DIR`.
- `installer/Leaf.iss`, `scripts/*.ps1`, `docs/*.md`, `.claude/skills/*`.

## Perf budgets (Release AOT, this laptop; measure with /leaf-perf before changing rendering, startup or caching code)
- Warm time to window <= 500 ms, first painted page <= 1 s. Private working set <= 100 MB with one document idle; working set <= 200 MB after scrolling a 60-page PDF. Installer <= 40 MB. Baseline numbers in docs/PERF.md (WinUI 3 alone is ~113 MB WS).

## Etiquette
- Small focused commits with conventional-commit messages. Run build + tests before committing. Update docs/DECISIONS.md when a design decision changes.
