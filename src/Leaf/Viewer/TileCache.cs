using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Leaf.Viewer;

/// <summary>Identity of one rendered tile. ScaleMilli = round(renderScale * 1000) where renderScale is device px per PDF point.</summary>
public readonly record struct TileKey(int Page, int Col, int Row, int ScaleMilli, int Rotation);

/// <summary>
/// A cached tile. The bitmap is a <see cref="WriteableBitmap"/> so XAML owns the pixels; entries are never disposed,
/// only dropped (after the viewer unbinds them from any Image) and left to the garbage collector.
/// </summary>
public sealed class TileEntry
{
    public TileEntry(WriteableBitmap bitmap, int pixelWidth, int pixelHeight)
    {
        Bitmap = bitmap;
        PixelWidth = pixelWidth;
        PixelHeight = pixelHeight;
        Bytes = pixelWidth * pixelHeight * 4;
    }

    public WriteableBitmap Bitmap { get; }
    public ImageSource Source => Bitmap;
    public int PixelWidth { get; }
    public int PixelHeight { get; }
    public int Bytes { get; }
    public long LastUse { get; set; }
}

/// <summary>
/// UI-thread LRU cache of rendered tiles bounded by bytes. Visible tiles are pinned and never evicted;
/// everything else goes in least-recently-used order once the budget is exceeded. Eviction returns the
/// retired entries so the caller can unbind them from Images before they are released.
/// </summary>
public sealed class TileCache
{
    public const int TileSize = 1024;

    private readonly Dictionary<TileKey, TileEntry> _entries = new();
    private HashSet<TileKey> _pinned = new();
    private long _clock;

    public long BudgetBytes { get; set; } = 48L << 20;
    public long TotalBytes { get; private set; }
    public int Count => _entries.Count;

    public bool TryGet(TileKey key, out TileEntry entry)
    {
        if (_entries.TryGetValue(key, out TileEntry? e))
        {
            e.LastUse = ++_clock;
            entry = e;
            return true;
        }

        entry = null!;
        return false;
    }

    public bool Contains(TileKey key) => _entries.ContainsKey(key);

    /// <summary>Inserts an entry; returns the entry it replaced, if any. Does not evict (call <see cref="EvictOverBudget"/>).</summary>
    public TileEntry? Add(TileKey key, TileEntry entry)
    {
        _entries.TryGetValue(key, out TileEntry? old);
        if (old is not null)
        {
            TotalBytes -= old.Bytes;
        }

        entry.LastUse = ++_clock;
        _entries[key] = entry;
        TotalBytes += entry.Bytes;
        return old;
    }

    public void SetPinned(HashSet<TileKey> pinned)
    {
        _pinned = pinned;
        foreach (TileKey key in pinned)
        {
            if (_entries.TryGetValue(key, out TileEntry? e))
            {
                e.LastUse = ++_clock;
            }
        }
    }

    /// <summary>Removes least-recently-used unpinned entries until under budget; returns what was removed.</summary>
    public List<(TileKey Key, TileEntry Entry)> EvictOverBudget()
    {
        var retired = new List<(TileKey, TileEntry)>();
        if (TotalBytes <= BudgetBytes)
        {
            return retired;
        }

        var candidates = new List<KeyValuePair<TileKey, TileEntry>>();
        foreach (KeyValuePair<TileKey, TileEntry> kv in _entries)
        {
            if (!_pinned.Contains(kv.Key))
            {
                candidates.Add(kv);
            }
        }

        candidates.Sort((a, b) => a.Value.LastUse.CompareTo(b.Value.LastUse));
        foreach (KeyValuePair<TileKey, TileEntry> kv in candidates)
        {
            if (TotalBytes <= BudgetBytes)
            {
                break;
            }

            _entries.Remove(kv.Key);
            TotalBytes -= kv.Value.Bytes;
            retired.Add((kv.Key, kv.Value));
        }

        return retired;
    }

    /// <summary>Removes every unpinned entry matching the predicate; returns what was removed.</summary>
    public List<(TileKey Key, TileEntry Entry)> DropWhere(Func<TileKey, bool> predicate)
    {
        var retired = new List<(TileKey, TileEntry)>();
        foreach (KeyValuePair<TileKey, TileEntry> kv in _entries)
        {
            if (predicate(kv.Key) && !_pinned.Contains(kv.Key))
            {
                retired.Add((kv.Key, kv.Value));
            }
        }

        foreach ((TileKey key, TileEntry entry) in retired)
        {
            _entries.Remove(key);
            TotalBytes -= entry.Bytes;
        }

        return retired;
    }

    /// <summary>Removes everything; returns the removed entries.</summary>
    public List<(TileKey Key, TileEntry Entry)> Clear()
    {
        var retired = new List<(TileKey, TileEntry)>(_entries.Count);
        foreach (KeyValuePair<TileKey, TileEntry> kv in _entries)
        {
            retired.Add((kv.Key, kv.Value));
        }

        _entries.Clear();
        _pinned = new HashSet<TileKey>();
        TotalBytes = 0;
        return retired;
    }
}
