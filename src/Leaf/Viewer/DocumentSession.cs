using Leaf.Pdfium;

namespace Leaf.Viewer;

/// <summary>
/// One open document as seen by the UI: owns the <see cref="PdfDocument"/> (which lives on the pdfium thread),
/// caches per-page text layouts, and exposes async wrappers for search and thumbnails.
/// </summary>
public sealed class DocumentSession : IDisposable
{
    private readonly Dictionary<int, Task<PdfTextLayout>> _textLayouts = new();
    private bool _disposed;

    private DocumentSession(string path, PdfDocument document)
    {
        Path = path;
        Document = document;
        Pages = document.Pages;
    }

    public string Path { get; }
    public string FileName => System.IO.Path.GetFileName(Path);
    public PdfDocument Document { get; }
    public IReadOnlyList<PdfPageInfo> Pages { get; }
    public int PageCount => Pages.Count;

    public static async Task<DocumentSession> OpenAsync(string path, string? password = null)
    {
        PdfDocument doc = await PdfiumThread.Instance.RunAsync(() => PdfDocument.Open(path, password), priority: -100);
        return new DocumentSession(path, doc);
    }

    /// <summary>Text layout for a page, fetched once. Safe to await from the UI thread.</summary>
    public Task<PdfTextLayout> GetTextLayoutAsync(int pageIndex, int priority = 300)
    {
        if (_textLayouts.TryGetValue(pageIndex, out Task<PdfTextLayout>? existing))
        {
            return existing;
        }

        Task<PdfTextLayout> task = PdfiumThread.Instance.RunAsync(() => Document.GetTextLayout(pageIndex), priority);
        _textLayouts[pageIndex] = task;
        return task;
    }

    public PdfTextLayout? TryGetCachedTextLayout(int pageIndex)
        => _textLayouts.TryGetValue(pageIndex, out Task<PdfTextLayout>? t) && t.IsCompletedSuccessfully ? t.Result : null;

    public Task<List<PdfSearchHit>> SearchPageAsync(int pageIndex, string term, bool matchCase, CancellationToken cancellationToken)
        => PdfiumThread.Instance.RunAsync(() => Document.Search(pageIndex, term, matchCase), priority: 2000, cancellationToken);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _textLayouts.Clear();
        PdfDocument doc = Document;
        _ = PdfiumThread.Instance.RunAsync(doc.Dispose, priority: 50);
    }
}
