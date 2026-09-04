using System.Runtime.InteropServices;
using System.Text;
using Leaf.Pdfium.Native;

namespace Leaf.Pdfium;

/// <summary>
/// An open PDF. Every member must be called on <see cref="PdfiumThread"/> (the viewer marshals through
/// <see cref="PdfiumThread.RunAsync{T}"/>). Page handles are cached in a small LRU so scrolling does not
/// reparse pages, while memory stays bounded.
/// </summary>
public sealed unsafe class PdfDocument : IDisposable
{
    private const int PageCacheSize = 8;

    private readonly PdfFileSource _source;
    private readonly void* _formInfo;
    private readonly List<PageHandle> _pages = new(PageCacheSize);
    private nint _doc;
    private nint _form;
    private bool _disposed;

    private PdfDocument(string path, PdfFileSource source, nint doc, void* formInfo, nint form)
    {
        Path = path;
        _source = source;
        _doc = doc;
        _formInfo = formInfo;
        _form = form;

        int count = NativeMethods.FPDF_GetPageCount(doc);
        var pages = new PdfPageInfo[count];
        for (int i = 0; i < count; i++)
        {
            FS_SIZEF size;
            if (!NativeMethods.FPDF_GetPageSizeByIndexF(doc, i, &size) || size.Width <= 0 || size.Height <= 0)
            {
                size = new FS_SIZEF { Width = 612, Height = 792 }; // damaged page: pretend Letter so layout stays sane
            }

            pages[i] = new PdfPageInfo(i, size.Width, size.Height);
        }

        Pages = pages;
        Title = NativeMethods.ReadUtf16((buf, len) => NativeMethods.FPDF_GetMetaText(doc, "Title", (void*)buf, len));
    }

    public string Path { get; }
    public IReadOnlyList<PdfPageInfo> Pages { get; }
    public int PageCount => Pages.Count;
    public string Title { get; }

    /// <summary>True when the backing file changed or disappeared and a read failed.</summary>
    public bool SourceFaulted => _source.Faulted;

    /// <summary>Opens a document. Throws <see cref="PdfException"/> (Error == Password when a password is required or wrong).</summary>
    public static PdfDocument Open(string path, string? password = null)
    {
        PdfiumThread.AssertCurrent();
        PdfFileSource? source = null;
        try
        {
            source = new PdfFileSource(path);
        }
        catch (PdfException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new PdfException(PdfError.File, ex.Message);
        }

        try
        {
            nint doc = LoadWithPassword(source, password, Encoding.UTF8);
            if (doc == 0)
            {
                uint err = NativeMethods.FPDF_GetLastError();
                if (err == NativeMethods.FPDF_ERR_PASSWORD && !string.IsNullOrEmpty(password))
                {
                    doc = LoadWithPassword(source, password, Encoding.Latin1);
                    err = doc == 0 ? NativeMethods.FPDF_GetLastError() : 0;
                }

                if (doc == 0)
                {
                    throw PdfException.FromLastError(err);
                }
            }

            // Form environment: without it, filled form fields are invisible. A zeroed version-1 struct is enough for drawing.
            void* formInfo = NativeMemory.AllocZeroed(1024);
            *(int*)formInfo = 1;
            nint form = NativeMethods.FPDFDOC_InitFormFillEnvironment(doc, formInfo);
            if (form != 0)
            {
                NativeMethods.FPDF_SetFormFieldHighlightAlpha(form, 0);
            }

            return new PdfDocument(path, source, doc, formInfo, form);
        }
        catch
        {
            source.Dispose();
            throw;
        }
    }

    private static nint LoadWithPassword(PdfFileSource source, string? password, Encoding encoding)
    {
        if (string.IsNullOrEmpty(password))
        {
            return NativeMethods.FPDF_LoadCustomDocument(source.Access, null);
        }

        byte[] bytes = encoding.GetBytes(password + '\0');
        fixed (byte* p = bytes)
        {
            return NativeMethods.FPDF_LoadCustomDocument(source.Access, p);
        }
    }

    // ---------------------------------------------------------------- rendering

    /// <summary>
    /// Renders one device-pixel tile of a page into a caller-owned BGRA buffer (stride bytes per row).
    /// <paramref name="displayedW"/>/<paramref name="displayedH"/> are the full page size in pixels for
    /// this <paramref name="rotation"/> (0..3) and scale; the tile starts at (tileX, tileY) within it.
    /// </summary>
    public void RenderTile(int pageIndex, int rotation, int displayedW, int displayedH, int tileX, int tileY, int width, int height, byte* buffer, int stride, bool limitedImageCache = false)
    {
        PdfiumThread.AssertCurrent();
        ThrowIfDisposed();
        PageHandle page = GetPage(pageIndex);

        nint bmp = NativeMethods.FPDFBitmap_CreateEx(width, height, NativeMethods.FPDFBitmap_BGRA, buffer, stride);
        if (bmp == 0)
        {
            throw new PdfException(PdfError.Unknown, "Could not create a render target.");
        }

        try
        {
            NativeMethods.FPDFBitmap_FillRect(bmp, 0, 0, width, height, 0xFFFFFFFF);
            int flags = NativeMethods.FPDF_ANNOT | (limitedImageCache ? NativeMethods.FPDF_RENDER_LIMITEDIMAGECACHE : 0);
            NativeMethods.FPDF_RenderPageBitmap(bmp, page.Page, -tileX, -tileY, displayedW, displayedH, rotation & 3, flags);
            if (_form != 0)
            {
                NativeMethods.FPDF_FFLDraw(_form, bmp, page.Page, -tileX, -tileY, displayedW, displayedH, rotation & 3, flags);
            }
        }
        finally
        {
            NativeMethods.FPDFBitmap_Destroy(bmp);
        }

        if (_source.Faulted)
        {
            throw new PdfException(PdfError.File, "The file changed or was removed while it was open.");
        }
    }

    /// <summary>Convenience: renders the whole page at <paramref name="scale"/> px/pt into a new BGRA array.</summary>
    public byte[] RenderPage(int pageIndex, int rotation, double scale, out int width, out int height)
    {
        PdfPageInfo info = Pages[pageIndex];
        (width, height) = PdfGeometry.DisplayedSize(info.WidthPt, info.HeightPt, rotation, scale);
        byte[] pixels = new byte[width * height * 4];
        fixed (byte* p = pixels)
        {
            RenderTile(pageIndex, rotation, width, height, 0, 0, width, height, p, width * 4);
        }

        return pixels;
    }

    // ---------------------------------------------------------------- text

    public PdfTextLayout GetTextLayout(int pageIndex)
    {
        PdfiumThread.AssertCurrent();
        ThrowIfDisposed();
        PageHandle page = GetPage(pageIndex);
        PdfPageInfo info = Pages[pageIndex];
        nint tp = page.TextPage;
        if (tp == 0)
        {
            return PdfTextLayout.Empty(pageIndex, info.WidthPt, info.HeightPt);
        }

        int count = NativeMethods.FPDFText_CountChars(tp);
        if (count <= 0)
        {
            return PdfTextLayout.Empty(pageIndex, info.WidthPt, info.HeightPt);
        }

        char[] chars = new char[count + 1];
        int written;
        fixed (char* c = chars)
        {
            written = NativeMethods.FPDFText_GetText(tp, 0, count, c);
        }

        // written includes the NUL terminator; some builds return 0 for pages with only control chars
        int textLength = Math.Clamp(written - 1, 0, count);
        var text = new string(chars, 0, textLength);
        if (textLength < count)
        {
            text = text.PadRight(count, '\0');
        }

        var boxes = new PdfRect[count];
        for (int i = 0; i < count; i++)
        {
            double l, r, b, t;
            if (NativeMethods.FPDFText_GetCharBox(tp, i, &l, &r, &b, &t) && r > l && t > b)
            {
                boxes[i] = new PdfRect(l, b, r, t);
            }
            else
            {
                boxes[i] = default; // empty box: generated whitespace/newline
            }
        }

        return new PdfTextLayout(pageIndex, info.WidthPt, info.HeightPt, text, boxes);
    }

    /// <summary>Finds every occurrence of <paramref name="term"/> on one page.</summary>
    public List<PdfSearchHit> Search(int pageIndex, string term, bool matchCase = false, bool wholeWord = false)
    {
        PdfiumThread.AssertCurrent();
        ThrowIfDisposed();
        var hits = new List<PdfSearchHit>();
        if (string.IsNullOrEmpty(term))
        {
            return hits;
        }

        PageHandle page = GetPage(pageIndex);
        if (page.TextPage == 0)
        {
            return hits;
        }

        uint flags = (matchCase ? NativeMethods.FPDF_MATCHCASE : 0) | (wholeWord ? NativeMethods.FPDF_MATCHWHOLEWORD : 0);
        nint handle = NativeMethods.FPDFText_FindStart(page.TextPage, term, flags, 0);
        if (handle == 0)
        {
            return hits;
        }

        try
        {
            while (NativeMethods.FPDFText_FindNext(handle))
            {
                int index = NativeMethods.FPDFText_GetSchResultIndex(handle);
                int count = NativeMethods.FPDFText_GetSchCount(handle);
                if (count > 0)
                {
                    hits.Add(new PdfSearchHit(pageIndex, index, count));
                }
            }
        }
        finally
        {
            NativeMethods.FPDFText_FindClose(handle);
        }

        return hits;
    }

    /// <summary>Uses pdfium's own mapping, for tests that validate <see cref="PdfGeometry"/>.</summary>
    public (int X, int Y) PageToDeviceNative(int pageIndex, int rotation, int displayedW, int displayedH, double xPt, double yPt)
    {
        PdfiumThread.AssertCurrent();
        PageHandle page = GetPage(pageIndex);
        int dx, dy;
        NativeMethods.FPDF_PageToDevice(page.Page, 0, 0, displayedW, displayedH, rotation & 3, xPt, yPt, &dx, &dy);
        return (dx, dy);
    }

    // ---------------------------------------------------------------- page cache

    /// <summary>Releases cached page handles (and their pdfium image caches) without closing the document.</summary>
    public void TrimPageCache(int keep = 0)
    {
        PdfiumThread.AssertCurrent();
        while (_pages.Count > keep)
        {
            ClosePage(_pages[0]);
            _pages.RemoveAt(0);
        }
    }

    private PageHandle GetPage(int pageIndex)
    {
        if ((uint)pageIndex >= (uint)Pages.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(pageIndex));
        }

        for (int i = 0; i < _pages.Count; i++)
        {
            if (_pages[i].Index == pageIndex)
            {
                PageHandle hit = _pages[i];
                if (i != _pages.Count - 1)
                {
                    _pages.RemoveAt(i);
                    _pages.Add(hit); // most recently used at the end
                }

                return hit;
            }
        }

        nint page = NativeMethods.FPDF_LoadPage(_doc, pageIndex);
        if (page == 0)
        {
            throw PdfException.FromLastError(NativeMethods.FPDF_GetLastError(), $"page {pageIndex + 1}");
        }

        if (_form != 0)
        {
            NativeMethods.FORM_OnAfterLoadPage(page, _form);
        }

        nint textPage = NativeMethods.FPDFText_LoadPage(page);
        var handle = new PageHandle(pageIndex, page, textPage);
        if (_pages.Count >= PageCacheSize)
        {
            ClosePage(_pages[0]);
            _pages.RemoveAt(0);
        }

        _pages.Add(handle);
        return handle;
    }

    private void ClosePage(PageHandle handle)
    {
        if (handle.TextPage != 0)
        {
            NativeMethods.FPDFText_ClosePage(handle.TextPage);
        }

        if (_form != 0)
        {
            NativeMethods.FORM_OnBeforeClosePage(handle.Page, _form);
        }

        NativeMethods.FPDF_ClosePage(handle.Page);
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        PdfiumThread.AssertCurrent();
        _disposed = true;
        TrimPageCache(0);
        if (_form != 0)
        {
            NativeMethods.FPDFDOC_ExitFormFillEnvironment(_form);
            _form = 0;
        }

        NativeMethods.FPDF_CloseDocument(_doc);
        _doc = 0;
        NativeMemory.Free(_formInfo);
        _source.Dispose();
    }

    private readonly record struct PageHandle(int Index, nint Page, nint TextPage);
}
