using System.Runtime.InteropServices;

namespace Leaf.Pdfium.Native;

// Windows x64 only. pdfium's "unsigned long" is 4 bytes on MSVC, so it maps to uint everywhere below.
// Every function here must be called on PdfiumThread; the public wrappers assert that in Debug builds.

[StructLayout(LayoutKind.Sequential)]
public struct FS_MATRIX { public float A, B, C, D, E, F; }

[StructLayout(LayoutKind.Sequential)]
public struct FS_RECTF { public float Left, Top, Right, Bottom; }

[StructLayout(LayoutKind.Sequential)]
public struct FS_SIZEF { public float Width, Height; }

/// <summary>typedef struct { unsigned long m_FileLen; int (*m_GetBlock)(void*, unsigned long, unsigned char*, unsigned long); void* m_Param; } FPDF_FILEACCESS;</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct FPDF_FILEACCESS
{
    public uint FileLen;
    public delegate* unmanaged<void*, uint, byte*, uint, int> GetBlock;
    public void* Param;
}

/// <summary>FPDF_LIBRARY_CONFIG, version 2 (later fields are only read for higher versions).</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct FPDF_LIBRARY_CONFIG
{
    public int Version;
    public byte** UserFontPaths;
    public void* Isolate;
    public uint V8EmbedderSlot;
    public void* Platform;
    public int RendererType;
    public int FontLibraryType;
    public int BrotliEnabled;
}

public static unsafe partial class NativeMethods
{
    private const string Lib = "pdfium";

    // Bitmap formats
    public const int FPDFBitmap_Gray = 1;
    public const int FPDFBitmap_BGR = 2;
    public const int FPDFBitmap_BGRx = 3;
    public const int FPDFBitmap_BGRA = 4;

    // Render flags
    public const int FPDF_ANNOT = 0x01;
    public const int FPDF_LCD_TEXT = 0x02;
    public const int FPDF_NO_NATIVETEXT = 0x04;
    public const int FPDF_GRAYSCALE = 0x08;
    public const int FPDF_REVERSE_BYTE_ORDER = 0x10;
    public const int FPDF_RENDER_LIMITEDIMAGECACHE = 0x200;
    public const int FPDF_RENDER_FORCEHALFTONE = 0x400;
    public const int FPDF_PRINTING = 0x800;
    public const int FPDF_RENDER_NO_SMOOTHTEXT = 0x1000;
    public const int FPDF_RENDER_NO_SMOOTHIMAGE = 0x2000;
    public const int FPDF_RENDER_NO_SMOOTHPATH = 0x4000;

    // Errors (FPDF_GetLastError)
    public const uint FPDF_ERR_SUCCESS = 0;
    public const uint FPDF_ERR_UNKNOWN = 1;
    public const uint FPDF_ERR_FILE = 2;
    public const uint FPDF_ERR_FORMAT = 3;
    public const uint FPDF_ERR_PASSWORD = 4;
    public const uint FPDF_ERR_SECURITY = 5;
    public const uint FPDF_ERR_PAGE = 6;

    // Search flags
    public const uint FPDF_MATCHCASE = 1;
    public const uint FPDF_MATCHWHOLEWORD = 2;
    public const uint FPDF_CONSECUTIVE = 4;

    // Action types
    public const uint PDFACTION_UNSUPPORTED = 0;
    public const uint PDFACTION_GOTO = 1;
    public const uint PDFACTION_REMOTEGOTO = 2;
    public const uint PDFACTION_URI = 3;
    public const uint PDFACTION_LAUNCH = 4;

    // ---- Library ----
    [LibraryImport(Lib)] public static partial void FPDF_InitLibraryWithConfig(FPDF_LIBRARY_CONFIG* config);
    [LibraryImport(Lib)] public static partial void FPDF_DestroyLibrary();

    // ---- Document ----
    [LibraryImport(Lib)] public static partial nint FPDF_LoadCustomDocument(FPDF_FILEACCESS* fileAccess, byte* password);
    [LibraryImport(Lib)] public static partial nint FPDF_LoadMemDocument64(void* dataBuf, nuint size, byte* password);
    [LibraryImport(Lib)] public static partial uint FPDF_GetLastError();
    [LibraryImport(Lib)] public static partial void FPDF_CloseDocument(nint document);
    [LibraryImport(Lib)] public static partial int FPDF_GetPageCount(nint document);
    [LibraryImport(Lib)] public static partial uint FPDF_GetDocPermissions(nint document);
    [LibraryImport(Lib)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool FPDF_GetPageSizeByIndexF(nint document, int pageIndex, FS_SIZEF* size);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial uint FPDF_GetMetaText(nint document, string tag, void* buffer, uint buflen);
    [LibraryImport(Lib)] public static partial uint FPDF_GetPageLabel(nint document, int pageIndex, void* buffer, uint buflen);

    // ---- Pages ----
    [LibraryImport(Lib)] public static partial nint FPDF_LoadPage(nint document, int pageIndex);
    [LibraryImport(Lib)] public static partial void FPDF_ClosePage(nint page);
    [LibraryImport(Lib)] public static partial float FPDF_GetPageWidthF(nint page);
    [LibraryImport(Lib)] public static partial float FPDF_GetPageHeightF(nint page);
    [LibraryImport(Lib)] public static partial int FPDFPage_GetRotation(nint page);

    // ---- Bitmaps ----
    [LibraryImport(Lib)] public static partial nint FPDFBitmap_CreateEx(int width, int height, int format, void* firstScan, int stride);
    [LibraryImport(Lib)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool FPDFBitmap_FillRect(nint bitmap, int left, int top, int width, int height, uint argb);
    [LibraryImport(Lib)] public static partial void* FPDFBitmap_GetBuffer(nint bitmap);
    [LibraryImport(Lib)] public static partial int FPDFBitmap_GetStride(nint bitmap);
    [LibraryImport(Lib)] public static partial void FPDFBitmap_Destroy(nint bitmap);

    // ---- Rendering ----
    [LibraryImport(Lib)] public static partial void FPDF_RenderPageBitmap(nint bitmap, nint page, int startX, int startY, int sizeX, int sizeY, int rotate, int flags);
    [LibraryImport(Lib)] public static partial void FPDF_RenderPageBitmapWithMatrix(nint bitmap, nint page, FS_MATRIX* matrix, FS_RECTF* clipping, int flags);
    [LibraryImport(Lib)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool FPDF_PageToDevice(nint page, int startX, int startY, int sizeX, int sizeY, int rotate, double pageX, double pageY, int* deviceX, int* deviceY);
    [LibraryImport(Lib)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool FPDF_DeviceToPage(nint page, int startX, int startY, int sizeX, int sizeY, int rotate, int deviceX, int deviceY, double* pageX, double* pageY);

    // ---- Forms (needed to DRAW widget annotations; a zeroed version-1 FPDF_FORMFILLINFO is enough) ----
    [LibraryImport(Lib)] public static partial nint FPDFDOC_InitFormFillEnvironment(nint document, void* formInfo);
    [LibraryImport(Lib)] public static partial void FPDFDOC_ExitFormFillEnvironment(nint formHandle);
    [LibraryImport(Lib)] public static partial void FORM_OnAfterLoadPage(nint page, nint formHandle);
    [LibraryImport(Lib)] public static partial void FORM_OnBeforeClosePage(nint page, nint formHandle);
    [LibraryImport(Lib)] public static partial void FPDF_FFLDraw(nint formHandle, nint bitmap, nint page, int startX, int startY, int sizeX, int sizeY, int rotate, int flags);
    [LibraryImport(Lib)] public static partial void FPDF_SetFormFieldHighlightAlpha(nint formHandle, byte alpha);

    // ---- Text ----
    [LibraryImport(Lib)] public static partial nint FPDFText_LoadPage(nint page);
    [LibraryImport(Lib)] public static partial void FPDFText_ClosePage(nint textPage);
    [LibraryImport(Lib)] public static partial int FPDFText_CountChars(nint textPage);
    [LibraryImport(Lib)] public static partial int FPDFText_GetText(nint textPage, int startIndex, int count, char* result);
    [LibraryImport(Lib)] public static partial uint FPDFText_GetUnicode(nint textPage, int index);
    [LibraryImport(Lib)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool FPDFText_GetCharBox(nint textPage, int index, double* left, double* right, double* bottom, double* top);
    [LibraryImport(Lib)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool FPDFText_GetLooseCharBox(nint textPage, int index, FS_RECTF* rect);
    [LibraryImport(Lib)] public static partial int FPDFText_GetCharIndexAtPos(nint textPage, double x, double y, double xTolerance, double yTolerance);
    [LibraryImport(Lib)] public static partial int FPDFText_CountRects(nint textPage, int startIndex, int count);
    [LibraryImport(Lib)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool FPDFText_GetRect(nint textPage, int rectIndex, double* left, double* top, double* right, double* bottom);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf16)] public static partial nint FPDFText_FindStart(nint textPage, string findWhat, uint flags, int startIndex);
    [LibraryImport(Lib)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool FPDFText_FindNext(nint handle);
    [LibraryImport(Lib)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool FPDFText_FindPrev(nint handle);
    [LibraryImport(Lib)] public static partial int FPDFText_GetSchResultIndex(nint handle);
    [LibraryImport(Lib)] public static partial int FPDFText_GetSchCount(nint handle);
    [LibraryImport(Lib)] public static partial void FPDFText_FindClose(nint handle);

    // ---- Outline / links (v0.2) ----
    [LibraryImport(Lib)] public static partial nint FPDFBookmark_GetFirstChild(nint document, nint bookmark);
    [LibraryImport(Lib)] public static partial nint FPDFBookmark_GetNextSibling(nint document, nint bookmark);
    [LibraryImport(Lib)] public static partial uint FPDFBookmark_GetTitle(nint bookmark, void* buffer, uint buflen);
    [LibraryImport(Lib)] public static partial nint FPDFBookmark_GetDest(nint document, nint bookmark);
    [LibraryImport(Lib)] public static partial int FPDFDest_GetDestPageIndex(nint document, nint dest);
    [LibraryImport(Lib)] public static partial nint FPDFLink_GetLinkAtPoint(nint page, double x, double y);
    [LibraryImport(Lib)] public static partial nint FPDFLink_GetDest(nint document, nint link);
    [LibraryImport(Lib)] public static partial nint FPDFLink_GetAction(nint link);
    [LibraryImport(Lib)] public static partial uint FPDFAction_GetType(nint action);
    [LibraryImport(Lib)] public static partial uint FPDFAction_GetURIPath(nint document, nint action, void* buffer, uint buflen);

    /// <summary>Reads a UTF-16LE out-buffer using pdfium's "call with NULL to get the byte length" convention.</summary>
    public static string ReadUtf16(Func<nint, uint, uint> call)
    {
        uint bytes = call(0, 0);
        if (bytes <= 2)
        {
            return string.Empty;
        }

        byte[] buffer = new byte[bytes];
        fixed (byte* p = buffer)
        {
            call((nint)p, bytes);
        }

        int chars = (int)(bytes / 2) - 1; // drop the terminating NUL
        return System.Text.Encoding.Unicode.GetString(buffer, 0, chars * 2);
    }
}
