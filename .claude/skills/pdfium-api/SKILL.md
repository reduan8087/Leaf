---
name: pdfium-api
description: >-
  Quick reference for the PDFium C API as used by Leaf through LibraryImport bindings in src/Leaf.Pdfium: library init,
  FPDF_LoadCustomDocument/FPDF_FILEACCESS, password errors, page handles, FPDFBitmap_CreateEx BGRA tiles,
  FPDF_RenderPageBitmap flags, FPDF_FFLDraw form fields, FPDFText_* extraction/char boxes/search, bookmarks, links,
  metadata, the single-thread rule and handle lifetimes. Use whenever writing or reviewing code that calls pdfium,
  renders pages or tiles, maps device pixels to PDF points, handles passwords or errors, or when the user mentions
  pdfium, FPDF, bblanchon.PDFium, PdfiumThread or PdfDocument.
user-invocable: false
paths:
  - "src/Leaf.Pdfium/**"
  - "src/Leaf/Viewer/**"
metadata:
  owner: leaf
---

# PDFium in Leaf

Binary: `bblanchon.PDFium.Win32` 154.0.8035 (chromium/8035, non-V8, non-XFA, Apache-2.0) -> `pdfium.dll` next to the exe.
Bindings: `src/Leaf.Pdfium/Native/Pdfium.cs` (`[LibraryImport("pdfium")]`, `static unsafe partial class`).

## Non-negotiables
1. **Single thread.** "None of the PDFium APIs are thread-safe." Every call goes through `PdfiumThread` (priority queue). Debug builds assert it.
2. **`unsigned long` = `uint`** on Windows (`FPDF_GetLastError`, `FPDF_FILEACCESS` fields, buffer lengths, search flags). `FPDF_BOOL` = `int`.
3. **Form fields are invisible** without the form environment: `FPDFDOC_InitFormFillEnvironment(doc, &info{version=1, all NULL})`,
   `FORM_OnAfterLoadPage` after `FPDF_LoadPage`, `FPDF_FFLDraw` after every render, `FORM_OnBeforeClosePage` before `FPDF_ClosePage`,
   `FPDFDOC_ExitFormFillEnvironment` before `FPDF_CloseDocument`. `FPDF_SetFormFieldHighlightAlpha(form, 0)` for Acrobat-like look.
4. **Pair every handle:** LoadPage/ClosePage, FPDFText_LoadPage/ClosePage, FindStart/FindClose, FPDFBitmap_CreateEx/Destroy (external buffers are not freed).
5. **Never lock the user's file:** `PdfFileSource` opens with `FileShare.ReadWrite | FileShare.Delete`; read failures become `PdfException`.

## Lifetimes and errors
- `FPDF_InitLibraryWithConfig(&cfg{version=2})` once on the pdfium thread; `FPDF_DestroyLibrary` at exit.
- `FPDF_LoadCustomDocument(&access, passwordUtf8)` returns 0 on failure; `FPDF_GetLastError()`: 0 ok, 1 unknown, 2 file, 3 format, 4 **password**, 5 security, 6 page.
  Retry a password as UTF-8 then Latin-1.
- Page sizes for layout without loading pages: `FPDF_GetPageSizeByIndexF(doc, i, &size)` (points, /Rotate applied).

## Rendering a tile (device pixels)
```
bmp = FPDFBitmap_CreateEx(w, h, FPDFBitmap_BGRA(4), buffer, stride);
FPDFBitmap_FillRect(bmp, 0, 0, w, h, 0xFFFFFFFF);          // opaque white
FPDF_RenderPageBitmap(bmp, page, -tileX, -tileY, pagePxW, pagePxH, rotate, FPDF_ANNOT | FPDF_RENDER_LIMITEDIMAGECACHE);
FPDF_FFLDraw(form, bmp, page, -tileX, -tileY, pagePxW, pagePxH, rotate, FPDF_ANNOT);
FPDFBitmap_Destroy(bmp);
```
`pagePxW = ceil(widthPt * scale)`, `scale = zoom * rasterizationScale * 96/72`. `rotate`: 0, 1=90cw, 2=180, 3=270. Flags: FPDF_ANNOT 0x01,
FPDF_LCD_TEXT 0x02 (avoid, tiles get GPU-scaled), FPDF_GRAYSCALE 0x08, FPDF_RENDER_LIMITEDIMAGECACHE 0x200, FPDF_PRINTING 0x800. Do not set
FPDF_REVERSE_BYTE_ORDER (0x10): BGRA already matches WinUI Bgra8. Coordinate helpers: `FPDF_PageToDevice` / `FPDF_DeviceToPage` with the same
start/size/rotate arguments as the render call.

## Text
- `tp = FPDFText_LoadPage(page)`; `n = FPDFText_CountChars(tp)`; `FPDFText_GetText(tp, 0, n, buf[n+1])` (UTF-16, returns count incl. NUL).
- Per char: `FPDFText_GetCharBox(tp, i, &l, &r, &b, &t)` (note L,R,B,T; PDF user space, origin bottom-left, points) or `GetLooseCharBox(tp, i, &rect)`.
- Hit test: `FPDFText_GetCharIndexAtPos(tp, x, y, xTol, yTol)` -> index or -1. Leaf caches char boxes per realized page (`PdfTextLayout`) so hit-testing runs on the UI thread.
- Search: `h = FPDFText_FindStart(tp, utf16Term, flags(1=MatchCase, 2=WholeWord), startIndex)`; loop `FPDFText_FindNext(h)`;
  `FPDFText_GetSchResultIndex(h)`, `FPDFText_GetSchCount(h)`; `FPDFText_FindClose(h)`. Rects for a range: `FPDFText_CountRects(tp, start, count)` then `FPDFText_GetRect(tp, i, &l, &t, &r, &b)`.
- Point -> DIP on an unrotated page: `x_dip = x_pt * k`, `y_dip = (pageHeightPt - y_pt) * k`, `k = zoom * 96/72`; for rotated views use `FPDF_PageToDevice`.

## Outline, links, metadata (v0.2)
`FPDFBookmark_GetFirstChild/GetNextSibling/GetTitle(UTF-16 bytes)/GetDest` + `FPDFDest_GetDestPageIndex`; `FPDFLink_GetLinkAtPoint`,
`FPDFLink_GetDest/GetAction`, `FPDFAction_GetType` (1 goto, 3 uri), `FPDFAction_GetURIPath`; `FPDF_GetMetaText(doc, "Title", buf, len)`; `FPDF_GetPageLabel`.
Out-buffer convention: call with `(NULL, 0)` to get the byte length (UTF-16LE incl. 2-byte NUL), then again with the buffer.

## Not supported by this build
JavaScript form calculations and XFA forms (need the V8 variant, 3x larger). XFA-only PDFs show pdfium's placeholder page.
