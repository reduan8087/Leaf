using System.Runtime.InteropServices.WindowsRuntime;
using Leaf.Pdfium;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Leaf.Viewer;

/// <summary>
/// Small page thumbnails for the Organize Pages grid and the thumbnails panel. Kept separate from
/// <see cref="TileCache"/> on purpose: it holds only a bounded number of small bitmaps, so a long document
/// browsed in the panel can never evict the tiles the reader is actually looking at.
/// </summary>
/// <remarks>
/// Renders run at a low priority so they always yield to visible tiles. Bitmaps are
/// <see cref="WriteableBitmap"/>s and are never disposed, because XAML re-reads an Image's source whenever it
/// re-measures and a closed WinRT bitmap is a fatal RO_E_CLOSED.
/// </remarks>
public sealed class ThumbnailCache
{
    /// <summary>Render priority. Well above the viewer's tiles (higher runs later) so reading never stutters.</summary>
    private const int RenderPriority = 800;

    private readonly Dictionary<Key, WriteableBitmap> _bitmaps = [];
    private readonly LinkedList<Key> _order = new();
    private readonly Dictionary<Key, Task<WriteableBitmap?>> _inFlight = [];

    /// <summary>How many thumbnails to keep. 64 covers several screenfuls of a grid at a few hundred KB each.</summary>
    public int Capacity { get; set; } = 64;

    public WriteableBitmap? TryGet(int pageIndex, int rotation, int widthPx)
    {
        var key = new Key(pageIndex, rotation & 3, widthPx);
        if (!_bitmaps.TryGetValue(key, out WriteableBitmap? bitmap))
        {
            return null;
        }

        Touch(key);
        return bitmap;
    }

    /// <summary>
    /// Renders a page thumbnail, or returns the cached one. Requests for the same page coalesce, so a grid that
    /// scrolls back and forth does not queue the same render twice.
    /// </summary>
    public Task<WriteableBitmap?> GetAsync(PdfDocument document, int pageIndex, int rotation, int widthPx, CancellationToken cancellationToken)
    {
        var key = new Key(pageIndex, rotation & 3, widthPx);
        if (_bitmaps.TryGetValue(key, out WriteableBitmap? cached))
        {
            Touch(key);
            return Task.FromResult<WriteableBitmap?>(cached);
        }

        if (_inFlight.TryGetValue(key, out Task<WriteableBitmap?>? pending))
        {
            return pending;
        }

        Task<WriteableBitmap?> task = RenderAsync(document, key, cancellationToken);
        _inFlight[key] = task;
        return task;
    }

    private async Task<WriteableBitmap?> RenderAsync(PdfDocument document, Key key, CancellationToken cancellationToken)
    {
        try
        {
            Rendered rendered = await PdfiumThread.Instance.RunAsync(
                () =>
                {
                    PdfPageInfo info = document.Pages[key.Page];
                    double scale = key.WidthPx / Math.Max(1, (key.Rotation & 1) == 1 ? info.HeightPt : info.WidthPt);
                    byte[] pixels = document.RenderPage(key.Page, key.Rotation, scale, out int width, out int height);
                    return new Rendered(pixels, width, height);
                },
                RenderPriority,
                cancellationToken);

            if (cancellationToken.IsCancellationRequested || rendered.Width <= 0 || rendered.Height <= 0)
            {
                return null;
            }

            var bitmap = new WriteableBitmap(rendered.Width, rendered.Height);
            using (Stream buffer = bitmap.PixelBuffer.AsStream())
            {
                buffer.Write(rendered.Pixels, 0, rendered.Pixels.Length);
            }

            bitmap.Invalidate();
            Add(key, bitmap);
            return bitmap;
        }
        catch (Exception ex) when (ex is PdfException or OperationCanceledException or ObjectDisposedException)
        {
            // A page that will not render simply shows the empty placeholder.
            return null;
        }
        finally
        {
            _inFlight.Remove(key);
        }
    }

    private void Add(Key key, WriteableBitmap bitmap)
    {
        _bitmaps[key] = bitmap;
        _order.AddLast(key);
        while (_order.Count > Capacity && _order.First is LinkedListNode<Key> oldest)
        {
            _order.RemoveFirst();
            _bitmaps.Remove(oldest.Value); // dropped, never disposed: XAML may still be showing it
        }
    }

    private void Touch(Key key)
    {
        if (_order.Remove(key))
        {
            _order.AddLast(key);
        }
    }

    /// <summary>Drops everything. Required after any edit that changes what a page index means.</summary>
    public void Clear()
    {
        _bitmaps.Clear();
        _order.Clear();
    }

    private readonly record struct Key(int Page, int Rotation, int WidthPx);

    private readonly record struct Rendered(byte[] Pixels, int Width, int Height);
}
