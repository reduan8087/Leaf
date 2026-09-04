using System.Buffers;
using System.Runtime.InteropServices.WindowsRuntime;
using Leaf.Pdfium;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Leaf.Viewer;

/// <summary>
/// Turns tile requests from the UI into pdfium work and delivers <see cref="WriteableBitmap"/>s back on the UI thread.
/// A generation counter drops work that became stale because of a zoom, rotation or DPI change; a "wanted" snapshot lets
/// the pdfium thread skip tiles that scrolled out of view before they were rendered.
///
/// Tiles are WriteableBitmaps on purpose: XAML owns their pixel memory outright, so there is no WinRT object Leaf could
/// close while XAML still needs it (disposing a SoftwareBitmap behind a SoftwareBitmapSource was a fatal RO_E_CLOSED crash).
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

    /// <summary>Snapshot of the tiles that are worth rendering right now (visible + prefetch + placeholders).</summary>
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

        Task<TileBuffer?> work = PdfiumThread.Instance.RunAsync(() =>
        {
            if (ct.IsCancellationRequested || !Volatile.Read(ref _wanted).Contains(key))
            {
                return null;
            }

            return Render(doc, req);
        }, req.Priority, ct);

        work.ContinueWith(t =>
        {
            _dispatcher.TryEnqueue(() => Deliver(key, generation, t));
        }, TaskContinuationOptions.ExecuteSynchronously);
    }

    private void Deliver(TileKey key, int generation, Task<TileBuffer?> task)
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

        TileBuffer? tile = task.Result;
        if (tile is null)
        {
            // Skipped because it was not (yet) in the wanted snapshot. If it is wanted now, try again.
            if (generation == Generation && _wanted.Contains(key) && !_cache.Contains(key))
            {
                Request(request);
            }

            return;
        }

        try
        {
            if (generation != Generation || _cache.Contains(key))
            {
                return; // stale, or a duplicate delivery
            }

            var bitmap = new WriteableBitmap(tile.Width, tile.Height);
            using (Stream pixels = bitmap.PixelBuffer.AsStream())
            {
                pixels.Write(tile.Buffer, 0, tile.Bytes);
            }

            bitmap.Invalidate();
            var entry = new TileEntry(bitmap, tile.Width, tile.Height);
            GC.AddMemoryPressure(entry.Bytes);
            TileEntry? replaced = _cache.Add(key, entry);
            if (replaced is not null)
            {
                GC.RemoveMemoryPressure(replaced.Bytes);
            }

            TileReady?.Invoke(key);
        }
        catch (Exception ex)
        {
            RenderFailed?.Invoke(ex);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(tile.Buffer);
        }
    }

    private static unsafe TileBuffer Render(PdfDocument doc, in TileRequest req)
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
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(buffer);
            throw;
        }

        return new TileBuffer(buffer, req.PixelWidth, req.PixelHeight);
    }
}

/// <summary>Rendered BGRA pixels in a pooled array (returned to the pool by the scheduler after upload).</summary>
public sealed record TileBuffer(byte[] Buffer, int Width, int Height)
{
    public int Bytes => Width * Height * 4;
}

/// <summary>Everything the pdfium thread needs to render one tile.</summary>
public readonly record struct TileRequest(TileKey Key, int DisplayedWidth, int DisplayedHeight, int TileX, int TileY, int PixelWidth, int PixelHeight, int Priority);
