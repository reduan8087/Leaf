using Leaf.Pdfium.Native;

namespace Leaf.Pdfium;

/// <summary>One entry in a document's outline (its table of contents).</summary>
/// <param name="Title">What the entry is called. Never null, but may be empty in a malformed file.</param>
/// <param name="PageIndex">Where it points, or -1 when the destination cannot be resolved.</param>
/// <param name="Children">Nested entries, in document order.</param>
public sealed record PdfOutlineNode(string Title, int PageIndex, IReadOnlyList<PdfOutlineNode> Children);

/// <summary>A clickable region on a page.</summary>
/// <param name="Bounds">Where it is, in PDF user space (points, origin bottom-left).</param>
/// <param name="TargetPage">Destination page for an internal jump, or -1.</param>
/// <param name="Uri">Destination for an external link, or null.</param>
public readonly record struct PdfLink(PdfRect Bounds, int TargetPage, string? Uri)
{
    public bool IsInternal => TargetPage >= 0;

    public bool IsExternal => !string.IsNullOrEmpty(Uri);
}

public sealed unsafe partial class PdfDocument
{
    /// <summary>Depth and size limits: a malformed outline can contain cycles, and this must never hang.</summary>
    private const int MaxOutlineDepth = 16;
    private const int MaxOutlineNodes = 5000;

    /// <summary>The document's outline, or an empty list when it has none.</summary>
    public IReadOnlyList<PdfOutlineNode> GetOutline()
    {
        PdfiumThread.AssertCurrent();
        ThrowIfDisposed();
        int budget = MaxOutlineNodes;
        return ReadOutlineLevel(0, 0, ref budget);
    }

    private IReadOnlyList<PdfOutlineNode> ReadOutlineLevel(nint parent, int depth, ref int budget)
    {
        if (depth >= MaxOutlineDepth || budget <= 0)
        {
            return [];
        }

        var nodes = new List<PdfOutlineNode>();
        var seen = new HashSet<nint>();
        nint bookmark = NativeMethods.FPDFBookmark_GetFirstChild(_doc, parent);
        while (bookmark != 0 && budget > 0)
        {
            // A sibling chain that loops back on itself would otherwise spin forever.
            if (!seen.Add(bookmark))
            {
                break;
            }

            budget--;
            nint current = bookmark;
            string title = NativeMethods.ReadUtf16((buffer, length) => NativeMethods.FPDFBookmark_GetTitle(current, (void*)buffer, length));
            nint dest = NativeMethods.FPDFBookmark_GetDest(_doc, current);
            int page = dest != 0 ? NativeMethods.FPDFDest_GetDestPageIndex(_doc, dest) : -1;
            if (page < 0)
            {
                page = ResolveActionPage(NativeMethods.FPDFBookmark_GetAction(current));
            }

            IReadOnlyList<PdfOutlineNode> children = ReadOutlineLevel(current, depth + 1, ref budget);
            nodes.Add(new PdfOutlineNode(title, page, children));
            bookmark = NativeMethods.FPDFBookmark_GetNextSibling(_doc, current);
        }

        return nodes;
    }

    private int ResolveActionPage(nint action)
    {
        if (action == 0 || NativeMethods.FPDFAction_GetType(action) != NativeMethods.PDFACTION_GOTO)
        {
            return -1;
        }

        nint dest = NativeMethods.FPDFAction_GetDest(_doc, action);
        return dest == 0 ? -1 : NativeMethods.FPDFDest_GetDestPageIndex(_doc, dest);
    }

    /// <summary>Every link on a page, with its rectangle in PDF points.</summary>
    public List<PdfLink> GetLinks(int pageIndex)
    {
        PdfiumThread.AssertCurrent();
        ThrowIfDisposed();
        var links = new List<PdfLink>();
        if ((uint)pageIndex >= (uint)PageCount)
        {
            return links;
        }

        PageHandle page = GetPage(pageIndex);
        int position = 0;
        nint annotation;
        while (NativeMethods.FPDFLink_Enumerate(page.Page, &position, &annotation))
        {
            FS_RECTF rect;
            if (!NativeMethods.FPDFLink_GetAnnotRect(annotation, &rect))
            {
                continue;
            }

            // FS_RECTF is top-down; PdfRect is bottom-up, and a malformed file may have them the wrong way round.
            var bounds = new PdfRect(
                Math.Min(rect.Left, rect.Right),
                Math.Min(rect.Top, rect.Bottom),
                Math.Max(rect.Left, rect.Right),
                Math.Max(rect.Top, rect.Bottom));
            if (bounds.Width <= 0 || bounds.Height <= 0)
            {
                continue;
            }

            nint dest = NativeMethods.FPDFLink_GetDest(_doc, annotation);
            int target = dest != 0 ? NativeMethods.FPDFDest_GetDestPageIndex(_doc, dest) : -1;
            string? uri = null;
            if (target < 0)
            {
                nint action = NativeMethods.FPDFLink_GetAction(annotation);
                if (action != 0)
                {
                    uint type = NativeMethods.FPDFAction_GetType(action);
                    if (type == NativeMethods.PDFACTION_URI)
                    {
                        uri = ReadUri(action);
                    }
                    else if (type == NativeMethods.PDFACTION_GOTO)
                    {
                        target = ResolveActionPage(action);
                    }
                }
            }

            if (target >= 0 || !string.IsNullOrEmpty(uri))
            {
                links.Add(new PdfLink(bounds, target, uri));
            }
        }

        return links;
    }

    /// <summary>Reads a URI action's path. It is a byte string, not UTF-16 like the other out-buffers.</summary>
    private string? ReadUri(nint action)
    {
        uint length = NativeMethods.FPDFAction_GetURIPath(_doc, action, null, 0);
        if (length <= 1)
        {
            return null;
        }

        byte[] buffer = new byte[length];
        fixed (byte* p = buffer)
        {
            NativeMethods.FPDFAction_GetURIPath(_doc, action, p, length);
        }

        return System.Text.Encoding.UTF8.GetString(buffer, 0, (int)length - 1); // drop the terminating NUL
    }
}
