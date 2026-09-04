# Leaf MVP (v0.1) scope

Goal: replace Adobe Acrobat Reader for everyday reading on one Windows 11 laptop. Light, fast, stable.

## In scope
- Open PDFs from Explorer (file association), command line, drag-and-drop, Ctrl+O picker. Single process; each file is a tab in one window.
- Continuous vertical scrolling with page gaps, fit-width default, fit-page, zoom 10-800 % (Ctrl+wheel, Ctrl+= / Ctrl+-, Ctrl+0), view rotation.
- Page navigation: page box (Ctrl+G), PgUp/PgDn/Home/End/arrows/Space, prev/next buttons, "n / total" indicator.
- Password-protected PDFs (prompt and retry), unreadable files show an error, never a crash. File is not locked; external changes offer reload.
- Find (Ctrl+F): match case, next/previous (Enter/F3, Shift+F3), "n of m", highlights on the page.
- Text selection with the mouse across pages, double-click word, Ctrl+A page, Ctrl+C copy.
- Form fields render (static appearance). Light/dark theme follows Windows. Mica title bar with tabs.
- Installer: per-user setup.exe (Inno Setup), no admin, no prerequisites, registers "Open with" and Default apps, one-click "Set default" link.

## Out of scope for v0.1 (planned v0.2+)
Print, bookmarks/outline pane, thumbnails pane, clickable links, remember last page, recent files UI, annotations, form filling,
touch pinch-zoom, zero-copy Direct2D tiles, code signing, auto-update, ARM64, Explorer preview handler, localization.

## Non-goals
XFA forms and PDF JavaScript (would require the V8 pdfium build), editing PDFs, cloud features.

## Success criteria
- Opens every one of the ~1,749 local PDFs without crashing (corpus smoke test).
- Release build: warm start to first painted page <= 400 ms, idle working set <= 90 MB, installer <= 35 MB.
- Adobe can be uninstalled and every PDF still opens in Leaf by double-click.
