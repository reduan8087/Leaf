using System.Diagnostics.CodeAnalysis;
using Leaf.Pdfium.Native;

namespace Leaf.Pdfium;

/// <summary>
/// Structural editing (rotate, delete, reorder, import) and saving. Like the rest of <see cref="PdfDocument"/>,
/// every member here must run on <see cref="PdfiumThread"/>.
/// </summary>
public sealed unsafe partial class PdfDocument
{
    /// <summary>True once a structural edit has been applied but not yet written to disk.</summary>
    public bool IsModified { get; private set; }

    /// <summary>
    /// Re-reads the page table from pdfium. Every structural edit must call this: reported page sizes already
    /// have the page's own /Rotate applied, so a rotation changes them too, not only the count.
    /// </summary>
    [MemberNotNull(nameof(Pages))]
    private void RebuildPages()
    {
        int count = NativeMethods.FPDF_GetPageCount(_doc);
        var pages = new PdfPageInfo[count];
        for (int i = 0; i < count; i++)
        {
            FS_SIZEF size;
            if (!NativeMethods.FPDF_GetPageSizeByIndexF(_doc, i, &size) || size.Width <= 0 || size.Height <= 0)
            {
                size = new FS_SIZEF { Width = 612, Height = 792 }; // damaged page: pretend Letter so layout stays sane
            }

            pages[i] = new PdfPageInfo(i, size.Width, size.Height);
        }

        Pages = pages;
    }

    /// <summary>The page's stored /Rotate in quarter-turns clockwise (0..3). Unrelated to the viewer's view rotation.</summary>
    public int GetPageRotation(int pageIndex)
    {
        PdfiumThread.AssertCurrent();
        ThrowIfDisposed();
        return NativeMethods.FPDFPage_GetRotation(GetPage(pageIndex).Page) & 3;
    }

    /// <summary>Writes /Rotate into the document. Unlike the viewer's rotation, this is saved with the file.</summary>
    public void SetPageRotation(int pageIndex, int quarterTurns)
    {
        PdfiumThread.AssertCurrent();
        ThrowIfDisposed();
        if ((uint)pageIndex >= (uint)PageCount)
        {
            throw new ArgumentOutOfRangeException(nameof(pageIndex));
        }

        NativeMethods.FPDFPage_SetRotation(GetPage(pageIndex).Page, quarterTurns & 3);
        InvalidateAfterEdit();
    }

    /// <summary>Deletes the given pages. Indices refer to the current order; duplicates and ordering do not matter.</summary>
    public void DeletePages(ReadOnlySpan<int> pageIndices)
    {
        PdfiumThread.AssertCurrent();
        ThrowIfDisposed();
        int[] ordered = Distinct(pageIndices, PageCount);
        if (ordered.Length == 0)
        {
            return;
        }

        if (ordered.Length >= PageCount)
        {
            throw new PdfException(PdfError.Page, "A document must keep at least one page.");
        }

        // Cached handles would dangle and their indices would be wrong once pages shift.
        TrimPageCache(0);
        Array.Sort(ordered);
        for (int i = ordered.Length - 1; i >= 0; i--)
        {
            NativeMethods.FPDFPage_Delete(_doc, ordered[i]); // descending, so the earlier indices stay valid
        }

        InvalidateAfterEdit();
    }

    /// <summary>
    /// Applies a whole permutation: <paramref name="newOrder"/>[i] is the current index of the page that should
    /// end up at position i. Every page must appear exactly once.
    /// </summary>
    public void ReorderPages(ReadOnlySpan<int> newOrder)
    {
        PdfiumThread.AssertCurrent();
        ThrowIfDisposed();
        int count = PageCount;
        if (newOrder.Length != count)
        {
            throw new ArgumentException($"Expected {count} indices, got {newOrder.Length}.", nameof(newOrder));
        }

        if (Distinct(newOrder, count).Length != count)
        {
            throw new ArgumentException("Every page must appear exactly once.", nameof(newOrder));
        }

        bool identity = true;
        for (int i = 0; i < count && identity; i++)
        {
            identity = newOrder[i] == i;
        }

        if (identity)
        {
            return;
        }

        TrimPageCache(0);
        int[] order = newOrder.ToArray();
        fixed (int* p = order)
        {
            // Moving every page, in the wanted order, to index 0 realises the permutation in a single call.
            if (!NativeMethods.FPDF_MovePages(_doc, p, (uint)order.Length, 0))
            {
                throw new PdfException(PdfError.Page, "The pages could not be reordered.");
            }
        }

        InvalidateAfterEdit();
    }

    /// <summary>Copies pages from another open document in at <paramref name="atIndex"/>. An empty span imports every page.</summary>
    public void ImportPages(PdfDocument source, ReadOnlySpan<int> pageIndices, int atIndex)
    {
        PdfiumThread.AssertCurrent();
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(source);
        source.ThrowIfDisposed();
        atIndex = Math.Clamp(atIndex, 0, PageCount);

        TrimPageCache(0);
        bool ok;
        if (pageIndices.IsEmpty)
        {
            ok = NativeMethods.FPDF_ImportPagesByIndex(_doc, source._doc, null, 0, atIndex);
        }
        else
        {
            int[] indices = pageIndices.ToArray();
            _ = Distinct(indices, source.PageCount); // range check only; order and repeats are meaningful here
            fixed (int* p = indices)
            {
                ok = NativeMethods.FPDF_ImportPagesByIndex(_doc, source._doc, p, (uint)indices.Length, atIndex);
            }
        }

        if (!ok)
        {
            throw new PdfException(PdfError.Page, "The pages could not be imported.");
        }

        InvalidateAfterEdit();
    }

    private void InvalidateAfterEdit()
    {
        TrimPageCache(0);
        RebuildPages();
        IsModified = true;
    }

    private static int[] Distinct(ReadOnlySpan<int> indices, int limit)
    {
        var seen = new HashSet<int>(indices.Length);
        foreach (int i in indices)
        {
            if ((uint)i >= (uint)limit)
            {
                throw new ArgumentOutOfRangeException(nameof(indices), $"Page index {i} is outside 0..{limit - 1}.");
            }

            seen.Add(i);
        }

        return [.. seen];
    }

    // ---------------------------------------------------------------- saving

    /// <summary>The PDF version of the document as an integer such as 17 for 1.7, or 0 when pdfium does not report one.</summary>
    public int FileVersion
    {
        get
        {
            PdfiumThread.AssertCurrent();
            ThrowIfDisposed();
            int version;
            return NativeMethods.FPDF_GetFileVersion(_doc, &version) ? version : 0;
        }
    }

    /// <summary>
    /// Writes the current state of the document to <paramref name="path"/>. Saving straight over <see cref="Path"/>
    /// is unsafe: pages are read lazily from the still-open source handle, so the caller must write to a temporary
    /// file, replace the original, and reopen.
    /// </summary>
    public void SaveAs(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
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
                SaveTo(stream);
            }
        }
        catch
        {
            TryDelete(path); // never leave a half-written PDF behind
            throw;
        }
    }

    /// <summary>Writes the current state of the document to a stream. The stream is not closed.</summary>
    public void SaveTo(Stream stream)
    {
        PdfiumThread.AssertCurrent();
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(stream);

        // Page handles carry pdfium-side caches; drop them so what is written matches what was edited.
        TrimPageCache(0);
        int version = FileVersion;
        using var sink = new PdfFileSink(stream);
        bool ok = version >= 10
            ? NativeMethods.FPDF_SaveWithVersion(_doc, sink.Write, NativeMethods.FPDF_NO_INCREMENTAL, version)
            : NativeMethods.FPDF_SaveAsCopy(_doc, sink.Write, NativeMethods.FPDF_NO_INCREMENTAL);

        if (!ok || sink.Faulted)
        {
            throw new PdfException(PdfError.Write, "The document could not be written.");
        }
    }

    /// <summary>Clears <see cref="IsModified"/> once the caller has persisted the document.</summary>
    public void MarkSaved() => IsModified = false;

    internal static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort only.
        }
    }
}
