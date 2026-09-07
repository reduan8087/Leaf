# Leaf scope

Goal: replace Adobe Acrobat Reader for everyday reading on one Windows 11 laptop. Light, fast, stable.

## Shipped in v0.1
- Open PDFs from Explorer (file association), command line, drag-and-drop, Ctrl+O picker. Single process; each file is a tab in one window.
- Continuous vertical scrolling with page gaps, fit-width default, fit-page, zoom 10-800 % (Ctrl+wheel, Ctrl+= / Ctrl+-, Ctrl+0), view rotation.
- Page navigation: page box (Ctrl+G), PgUp/PgDn/Home/End/arrows/Space, prev/next buttons, "n / total" indicator.
- Password-protected PDFs (prompt and retry), unreadable files show an error, never a crash. File is not locked; external changes offer reload.
- Find (Ctrl+F): match case, next/previous (Enter/F3, Shift+F3), "n of m", highlights on the page.
- Text selection with the mouse across pages, double-click word, Ctrl+A page, Ctrl+C copy.
- Form fields render (static appearance). Light/dark theme follows Windows. Mica title bar with tabs.
- Installer: per-user setup.exe (Inno Setup), no admin, no prerequisites, registers "Open with" and Default apps, one-click "Set default" link.

## Shipped in v0.2
- App menu (Alt+F): Open, Open Recent, Save, Save As, Revert, Combine Files, Organize Pages, Document Properties, View, default app, About.
- **Combine Files** (Ctrl+Shift+M): merge PDFs and images (jpg/jpeg/png/bmp/tif/tiff/webp) into one document. Drag to reorder, sort by
  name/date/size/folder, choose whether image pages match the image or fit A4 or Letter. The result opens unsaved for review.
- **Organize Pages** (Ctrl+Shift+O): a full-window grid. Drag to reorder, rotate, delete, multi-select, move a selection to the start or end,
  insert pages from another PDF, extract a selection to a new file. Undo and redo. Nothing touches the document until Done.
- **Save** (Ctrl+S) and **Save As** (Ctrl+Shift+S), with a modified marker on the tab and prompts before changes can be lost. Revert reloads
  from disk. Saving always writes a temp file and swaps, so a failed write cannot damage the original.
- **View modes** as two independent axes: columns (1 / 2 / N) and continuous scrolling on or off, plus "cover page on its own".
  Fit width, fit height, fit page and actual size. All of it on the toolbar, not buried in a menu.
  With scrolling off, the mouse wheel scrolls the page and turns it at the edge, as the keyboard already did.
- **Full screen** on F11 or Ctrl+L, Esc to leave; the chrome returns when the pointer reaches the top edge.
- **Thumbnails and bookmarks panels** (F4), resizable, with the page being read highlighted.
- **Clickable links**: internal jumps, and web links opened only after the address has been shown.
- **Recent files**, and remembered window position, view preferences and panel state.

## Out of scope for v0.2 (planned)
- v0.3: **printing** (print dialog, page ranges, scaling, preview).
- v1.0: printing plus **code signing**, so SmartScreen stops warning on every install. This is the point at which Acrobat can be uninstalled
  for good, which is why 1.0 is not this release.
- Later or unplanned: remember last page, annotations, form filling, touch pinch-zoom, zero-copy Direct2D tiles, auto-update, ARM64,
  Explorer preview handler, localization.

## Non-goals
XFA forms and PDF JavaScript (would require the V8 pdfium build), authoring or content editing, cloud features.

## Success criteria
- Opens every PDF in a real folder without crashing; a damaged one is reported cleanly (corpus smoke test, `LEAF_CORPUS_DIR`).
- Meets the performance budgets in `docs/PERF.md`, which supersede the numbers originally guessed here.
- A document can be reorganized, combined and saved without ever damaging the source file.
- Adobe can be uninstalled and every PDF still opens in Leaf by double-click. (Until v0.3, printing is the one thing that still needs it.)
