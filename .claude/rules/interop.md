---
paths:
  - "src/Leaf.Pdfium/**/*.cs"
---
# Native interop rules (src/Leaf.Pdfium)
- Declare pdfium functions with `[LibraryImport("pdfium")]` on `static partial` methods inside a `static unsafe partial class`; project has `AllowUnsafeBlocks`.
- pdfium `unsigned long` is 4 bytes on Windows: use `uint`, never `ulong`/`nuint`. `FPDF_BOOL` is `int` (`[return: MarshalAs(UnmanagedType.Bool)]` is fine).
- Strings: paths/passwords are UTF-8 (`StringMarshalling.Utf8`), search terms UTF-16 (`StringMarshalling.Utf16`), out-buffers for titles/labels/meta are UTF-16LE sized in BYTES.
- Native callbacks are `[UnmanagedCallersOnly]` static methods referenced by function pointer; keep the owning struct in `NativeMemory` until the document is closed.
- Every `FPDF_*` call happens on `PdfiumThread`; wrapper methods assert `PdfiumThread.IsCurrent` in Debug.
- Pair handles: `FPDF_LoadPage`+`FORM_OnAfterLoadPage` ... `FORM_OnBeforeClosePage`+`FPDF_ClosePage`; `FPDFText_LoadPage`/`ClosePage`; `FPDFText_FindStart`/`FindClose`; `FPDFBitmap_CreateEx`/`Destroy`.
