# Leaf

A light, fast PDF reader for Windows 11. Native C# / .NET 10 + WinUI 3, rendering with PDFium, published as a
Native AOT executable and shipped as a per-user installer with no prerequisites.

![Leaf icon](src/Leaf/Assets/Leaf-256.png)

## Why
Adobe Acrobat spawns nine processes and ~540 MB to show a 60-page document, and takes almost three seconds to open.
Leaf opens the same file in about a quarter of a second in a single ~210 MB process (139 MB private), with one window and tabs.
See [docs/PERF.md](docs/PERF.md) for the measurements.

## Features (v0.1)
- Open from Explorer (file association), command line, drag-and-drop, or Ctrl+O; one window, one tab per document.
- Continuous scrolling, fit width / fit page / 10–800 % zoom (Ctrl+wheel, Ctrl+= / Ctrl+-, Ctrl+0), rotation, page box (Ctrl+G).
- Find (Ctrl+F, F3 / Shift+F3, match case) with highlights; text selection by drag, double-click word, Ctrl+A page, Ctrl+C copy.
- Password-protected PDFs, form fields rendered, unreadable files reported cleanly, files never locked (reload prompt when they change).
- Light/dark theme follows Windows; Mica title bar.

## Install
1. Build the installer (see below) or take `dist/Leaf-Setup-x64.exe`.
2. Run it: per-user, no admin. SmartScreen will show "More info → Run anyway" because the binary is not code-signed.
3. Tick "Open Windows Settings so I can make Leaf the default PDF app" on the last page (or use the Leaf menu later) and press **Set default**.

## Build
Requires the .NET 10 SDK and Visual Studio 2022 Build Tools with the C++ workload (for the Native AOT linker).
```
dotnet build Leaf.slnx -c Debug -p:Platform=x64          # debug build
dotnet test tests/Leaf.Pdfium.Tests -c Release             # engine tests (14) with generated fixture PDFs
pwsh -File scripts/publish.ps1                             # Native AOT publish -> artifacts/publish
pwsh -File scripts/make-installer.ps1                      # + Inno Setup -> dist/Leaf-Setup-x64.exe
pwsh -File scripts/measure-perf.ps1 -Pdf some.pdf -Acrobat # startup/memory table
```
`scripts/publish.ps1` works around two toolchain gaps on the development machine (missing `Windows Kits\10` import
libraries and a `vswhere` PATH quirk in `vcvarsall.bat`); see the script header.

## Layout
- `src/Leaf.Pdfium` — PDFium bindings and the single engine thread (no UI dependencies).
- `src/Leaf` — WinUI 3 app. `docs/ARCHITECTURE.md` explains the rendering pipeline.
- `tests/Leaf.Pdfium.Tests` — xunit; set `LEAF_CORPUS_DIR` to sweep a folder of real PDFs.
- `installer/Leaf.iss` — Inno Setup script (file association, Default Apps registration).

## Third-party
PDFium (Apache-2.0) via `bblanchon.PDFium.Win32`; Windows App SDK; .NET. See `docs/THIRD-PARTY-NOTICES.md`.
