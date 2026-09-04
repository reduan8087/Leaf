using System.Buffers;
using System.Runtime.InteropServices.WindowsRuntime;
using Leaf.Pdfium;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;

namespace Leaf.Viewer;

/// <summary>
/// Turns tile requests from the UI into pdfium work and delivers <see cref="SoftwareBitmapSource"/>s back on the UI thread.
/// A generation counter drops work that became stale because of a zoom, rotation or DPI change; a "wanted" snapshot lets
/// the pdfium thread skip tiles that scrolled out of view before they were rendered.
/// </summary>
public sealed class RenderScheduler
{
    private readonly DocumentSession _session;
    private readonly DispatcherQueue _dispatcher;
    private readonly TileCache _cache;
    private readonly Dictionary<TileKey, TileRequest> _inFlight = new();
    private CancellationTokenSource _generationCts = new();
    private HashSet<TileKey> _wanted = new();

    public RenderScheduler(DocumentSession session, TileCache cache, DispatcherQueue dispatcher)
    {
        _session = session;
        _cache = cache;
        _dispatcher = dispatcher;
    }

    public int Generation { get; private set; }

    /// <summary>Raised on the UI thread when a tile has been added to the cache.</summary>
    public event Action<TileKey>? TileReady;

    public event Action<Exception>? RenderFailed;

    public void BumpGeneration()
    {
        Generation++;
        _generationCts.Cancel();
        _generationCts = new CancellationTokenSource();
        _inFlight.Clear();
    }

    /// <summary>Snapshot of the tiles that are worth rendering right now (visible + prefetch).</summary>
    public void SetWanted(HashSet<TileKey> wanted)
    {
        Volatile.Write(ref _wanted, wanted);
    }

    public void Request(in TileRequest request)
    {
        TileKey key = request.Key;
        if (_cache.Contains(key) || _inFlight.ContainsKey(key))
        {
            return;
        }

        _inFlight[key] = request;
        int generation = Generation;
        CancellationToken ct = _generationCts.Token;
        TileRequest req = request;
        PdfDocument doc = _session.Document;

        Task<SoftwareBitmap?> work = PdfiumThread.Instance.RunAsync(() =>
        {
            if (ct.IsCancellationRequested || !Volatile.Read(ref _wanted).Contains(key))
            {
                return null;
            }

            return RenderToSoftwareBitmap(doc, req);
        }, req.Priority, ct);

        work.ContinueWith(t =>
        {
            _dispatcher.TryEnqueue(() => Deliver(key, generation, t));
        }, TaskContinuationOptions.ExecuteSynchronously);
    }

    private void Deliver(TileKey key, int generation, Task<SoftwareBitmap?> task)
    {
        _inFlight.Remove(key, out TileRequest request);
        if (task.IsCanceled)
        {
            return;
        }

        if (task.IsFaulted)
        {
            RenderFailed?.Invoke(task.Exception!.GetBaseException());
            return;
        }

        SoftwareBitmap? bitmap = task.Result;
        if (bitmap is null)
        {
            // Skipped because it was not (yet) in the wanted snapshot. If it is wanted now, try again.
            if (generation == Generation && _wanted.Contains(key) && !_cache.Contains(key))
            {
                Request(request);
            }

            return;
        }

        if (generation != Generation)
        {
            bitmap.Dispose();
            return;
        }

        _ = PresentAsync(key, bitmap);
    }

    private async Task PresentAsync(TileKey key, SoftwareBitmap bitmap)
    {
        var source = new SoftwareBitmapSource();
        try
        {
            await source.SetBitmapAsync(bitmap);
        }
        catch (Exception ex)
        {
            source.Dispose();
            bitmap.Dispose();
            RenderFailed?.Invoke(ex);
            return;
        }

        int w = bitmap.PixelWidth;
        int h = bitmap.PixelHeight;
        bitmap.Dispose(); // XAML owns its own copy now
        _cache.Add(key, new TileEntry(source, w, h));
        TileReady?.Invoke(key);
    }

    private static unsafe SoftwareBitmap RenderToSoftwareBitmap(PdfDocument doc, in TileRequest req)
    {
        int stride = req.PixelWidth * 4;
        int bytes = stride * req.PixelHeight;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(bytes);
        try
        {
            fixed (byte* p = buffer)
            {
                doc.RenderTile(req.Key.Page, req.Key.Rotation, req.DisplayedWidth, req.DisplayedHeight, req.TileX, req.TileY, req.PixelWidth, req.PixelHeight, p, stride);
            }

            var bitmap = new SoftwareBitmap(BitmapPixelFormat.Bgra8, req.PixelWidth, req.PixelHeight, BitmapAlphaMode.Premultiplied);
            bitmap.CopyFromBuffer(buffer.AsBuffer(0, bytes));
            return bitmap;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}

/// <summary>Everything the pdfium thread needs to render one tile.</summary>
public readonly record struct TileRequest(TileKey Key, int DisplayedWidth, int DisplayedHeight, int TileX, int TileY, int PixelWidth, int PixelHeight, int Priority);
