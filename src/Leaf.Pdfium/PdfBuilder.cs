using Leaf.Pdfium.Native;

namespace Leaf.Pdfium;

/// <summary>How an image is placed on the page it becomes.</summary>
/// <param name="PixelWidth">Decoded image width in pixels.</param>
/// <param name="PixelHeight">Decoded image height in pixels.</param>
/// <param name="PageWidthPt">Page width in points, or 0 to size the page to the image at 96 DPI.</param>
/// <param name="PageHeightPt">Page height in points, or 0 to size the page to the image at 96 DPI.</param>
public readonly record struct PdfImagePlacement(int PixelWidth, int PixelHeight, double PageWidthPt = 0, double PageHeightPt = 0);

/// <summary>
/// Builds a new PDF from scratch: the Combine Files engine. Owns an FPDF_CreateNewDocument handle, which has
/// no backing file, so it is a sibling of <see cref="PdfDocument"/> rather than another way to construct one.
/// Every member must run on <see cref="PdfiumThread"/>.
/// </summary>
public sealed unsafe class PdfBuilder : IDisposable
{
    private const double PxToPt = 72.0 / 96.0;

    private nint _doc;
    private bool _disposed;

    public PdfBuilder()
    {
        PdfiumThread.AssertCurrent();
        _doc = NativeMethods.FPDF_CreateNewDocument();
        if (_doc == 0)
        {
            throw new PdfException(PdfError.Unknown, "A new PDF could not be created.");
        }
    }

    public int PageCount
    {
        get
        {
            ThrowIfDisposed();
            return NativeMethods.FPDF_GetPageCount(_doc);
        }
    }

    /// <summary>Appends pages from an open document. An empty span appends every page, in order.</summary>
    public void AppendPages(PdfDocument source, ReadOnlySpan<int> pageIndices = default)
    {
        PdfiumThread.AssertCurrent();
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(source);

        int at = PageCount;
        bool ok;
        if (pageIndices.IsEmpty)
        {
            ok = NativeMethods.FPDF_ImportPagesByIndex(_doc, source.Handle, null, 0, at);
        }
        else
        {
            int[] indices = pageIndices.ToArray();
            foreach (int i in indices)
            {
                if ((uint)i >= (uint)source.PageCount)
                {
                    throw new ArgumentOutOfRangeException(nameof(pageIndices), $"Page index {i} is outside 0..{source.PageCount - 1}.");
                }
            }

            fixed (int* p = indices)
            {
                ok = NativeMethods.FPDF_ImportPagesByIndex(_doc, source.Handle, p, (uint)indices.Length, at);
            }
        }

        if (!ok)
        {
            throw new PdfException(PdfError.Page, $"Pages from \"{Path.GetFileName(source.Path)}\" could not be imported.");
        }
    }

    /// <summary>Copies the reading preferences (page layout, page mode) of a source document onto the result.</summary>
    public void CopyViewerPreferencesFrom(PdfDocument source)
    {
        PdfiumThread.AssertCurrent();
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(source);
        NativeMethods.FPDF_CopyViewerPreferences(_doc, source.Handle);
    }

    /// <summary>
    /// Appends one page holding a decoded image. <paramref name="bgra"/> is 4 bytes per pixel, top row first;
    /// any transparency must already be flattened by the caller because the page is drawn opaque.
    /// </summary>
    public void AppendImagePage(PdfImagePlacement placement, ReadOnlySpan<byte> bgra, int stride)
    {
        PdfiumThread.AssertCurrent();
        ThrowIfDisposed();
        if (placement.PixelWidth <= 0 || placement.PixelHeight <= 0)
        {
            throw new ArgumentException("The image has no pixels.", nameof(placement));
        }

        if (stride < placement.PixelWidth * 4 || bgra.Length < (long)stride * placement.PixelHeight)
        {
            throw new ArgumentException("The pixel buffer is smaller than the image.", nameof(bgra));
        }

        // alpha 0 gives an opaque BGRx bitmap; pdfium copies the pixels into the document when the object is set.
        nint bmp = NativeMethods.FPDFBitmap_Create(placement.PixelWidth, placement.PixelHeight, 0);
        if (bmp == 0)
        {
            throw new PdfException(PdfError.Unknown, "Could not create an image buffer.");
        }

        try
        {
            var dest = (byte*)NativeMethods.FPDFBitmap_GetBuffer(bmp);
            int destStride = NativeMethods.FPDFBitmap_GetStride(bmp);
            int rowBytes = placement.PixelWidth * 4;
            for (int y = 0; y < placement.PixelHeight; y++)
            {
                bgra.Slice(y * stride, rowBytes).CopyTo(new Span<byte>(dest + ((long)y * destStride), rowBytes));
            }

            AddImagePage(placement, bmp, null);
        }
        finally
        {
            NativeMethods.FPDFBitmap_Destroy(bmp);
        }
    }

    /// <summary>
    /// Appends one page holding a JPEG, embedded byte-for-byte rather than re-encoded, so a photo keeps its
    /// original quality and does not inflate the output.
    /// </summary>
    public void AppendJpegPage(string jpegPath, PdfImagePlacement placement)
    {
        PdfiumThread.AssertCurrent();
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrEmpty(jpegPath);

        PdfFileSource source;
        try
        {
            source = new PdfFileSource(jpegPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new PdfException(PdfError.File, ex.Message);
        }

        // "Inline" reads the whole JPEG now, so the file access can be released as soon as this returns.
        using (source)
        {
            AddImagePage(placement, 0, source);
        }
    }

    /// <summary>Creates the page, embeds the image (exactly one of <paramref name="bitmap"/> / <paramref name="jpeg"/>), places and commits it.</summary>
    private void AddImagePage(PdfImagePlacement placement, nint bitmap, PdfFileSource? jpeg)
    {
        (double pageW, double pageH) = PageSize(placement);
        int pageIndex = PageCount;
        nint page = NativeMethods.FPDFPage_New(_doc, pageIndex, pageW, pageH);
        if (page == 0)
        {
            throw new PdfException(PdfError.Page, "A page could not be created for the image.");
        }

        nint image = NativeMethods.FPDFPageObj_NewImageObj(_doc);
        if (image == 0)
        {
            NativeMethods.FPDF_ClosePage(page);
            NativeMethods.FPDFPage_Delete(_doc, pageIndex);
            throw new PdfException(PdfError.Unknown, "An image object could not be created.");
        }

        bool owned = false; // true once the page has taken the object over
        try
        {
            bool loaded = jpeg is null
                ? NativeMethods.FPDFImageObj_SetBitmap(&page, 1, image, bitmap)
                : NativeMethods.FPDFImageObj_LoadJpegFileInline(&page, 1, image, jpeg.Access);
            if (!loaded)
            {
                throw new PdfException(PdfError.Format, "The image could not be embedded.");
            }

            // An image object draws into the unit square, so the matrix is simply its target rectangle.
            (double w, double h, double x, double y) = Fit(placement.PixelWidth, placement.PixelHeight, pageW, pageH);
            var matrix = new FS_MATRIX { A = (float)w, B = 0, C = 0, D = (float)h, E = (float)x, F = (float)y };
            NativeMethods.FPDFPageObj_SetMatrix(image, &matrix);

            NativeMethods.FPDFPage_InsertObject(page, image);
            owned = true;
            if (!NativeMethods.FPDFPage_GenerateContent(page))
            {
                throw new PdfException(PdfError.Page, "The image page could not be written.");
            }
        }
        catch
        {
            if (!owned)
            {
                NativeMethods.FPDFPageObj_Destroy(image);
            }

            NativeMethods.FPDF_ClosePage(page);
            NativeMethods.FPDFPage_Delete(_doc, pageIndex);
            throw;
        }

        NativeMethods.FPDF_ClosePage(page);
    }

    private static (double Width, double Height) PageSize(PdfImagePlacement placement)
    {
        if (placement.PageWidthPt > 0 && placement.PageHeightPt > 0)
        {
            return (placement.PageWidthPt, placement.PageHeightPt);
        }

        // "Match image": one image pixel becomes one 96-DPI device pixel.
        return (Math.Max(1, placement.PixelWidth * PxToPt), Math.Max(1, placement.PixelHeight * PxToPt));
    }

    /// <summary>Largest centred rectangle inside the page that keeps the image's aspect ratio.</summary>
    private static (double Width, double Height, double X, double Y) Fit(int pixelWidth, int pixelHeight, double pageW, double pageH)
    {
        double scale = Math.Min(pageW / pixelWidth, pageH / pixelHeight);
        double w = pixelWidth * scale;
        double h = pixelHeight * scale;
        return (w, h, (pageW - w) / 2, (pageH - h) / 2);
    }

    /// <summary>Writes the combined document. Throws when nothing has been appended.</summary>
    public void Save(string path)
    {
        PdfiumThread.AssertCurrent();
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (PageCount == 0)
        {
            throw new PdfException(PdfError.Page, "There is nothing to combine.");
        }

        FileStream stream;
        try
        {
            stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new PdfException(PdfError.Write, ex.Message);
        }

        try
        {
            using (stream)
            {
                using var sink = new PdfFileSink(stream);
                if (!NativeMethods.FPDF_SaveAsCopy(_doc, sink.Write, NativeMethods.FPDF_NO_INCREMENTAL) || sink.Faulted)
                {
                    throw new PdfException(PdfError.Write, "The combined document could not be written.");
                }
            }
        }
        catch
        {
            PdfDocument.TryDelete(path);
            throw;
        }
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
        NativeMethods.FPDF_CloseDocument(_doc);
        _doc = 0;
    }
}
