using Microsoft.UI.Xaml.Media.Imaging;

namespace Leaf.Viewer;

/// <summary>Identity of one rendered tile. ScaleMilli = round(renderScale * 1000) where renderScale is device px per PDF point.</summary>
public readonly record struct TileKey(int Page, int Col, int Row, int ScaleMilli, int Rotation);

public sealed class TileEntry
{
    public TileEntry(SoftwareBitmapSource source, int pixelWidth, int pixelHeight)
    {
        Source = source;
        PixelWidth = pixelWidth;
        PixelHeight = pixelHeight;
        Bytes = pixelWidth * pixelHeight * 4;
    }

    public SoftwareBitmapSource Source { get; }
    public int PixelWidth { get; }
    public int PixelHeight { get; }
    public int Bytes { get; }
    public long LastUse { get; set; }
}

/// <summary>
/// UI-thread LRU cache of rendered tiles bounded by bytes. Visible tiles are pinned and never evicted;
/// everything else goes in least-recently-used order once the budget is exceeded.
/// </summary>
public sealed class TileCache
{
    public const int TileSize = 1024;

    private readonly Dictionary<TileKey, TileEntry> _entries = new();
    private HashSet<TileKey> _pinned = new();
    private long _clock;

    public long BudgetBytes { get; set; } = 64L << 20;
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

    public void Add(TileKey key, TileEntry entry)
    {
        if (_entries.TryGetValue(key, out TileEntry? old))
        {
            TotalBytes -= old.Bytes;
            old.Source.Dispose();
        }

        entry.LastUse = ++_clock;
        _entries[key] = entry;
        TotalBytes += entry.Bytes;
        EvictOverBudget();
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

    public void EvictOverBudget()
    {
        if (TotalBytes <= BudgetBytes)
        {
            return;
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
            kv.Value.Source.Dispose();
        }
    }

    /// <summary>Drops every tile that does not belong to the given generation key set (zoom/rotation change) except pinned ones.</summary>
    public void DropWhere(Func<TileKey, bool> predicate)
    {
        var doomed = new List<TileKey>();
        foreach (KeyValuePair<TileKey, TileEntry> kv in _entries)
        {
            if (predicate(kv.Key) && !_pinned.Contains(kv.Key))
            {
                doomed.Add(kv.Key);
            }
        }

        foreach (TileKey key in doomed)
        {
            TotalBytes -= _entries[key].Bytes;
            _entries[key].Source.Dispose();
            _entries.Remove(key);
        }
    }

    public void Clear()
    {
        foreach (TileEntry e in _entries.Values)
        {
            e.Source.Dispose();
        }

        _entries.Clear();
        _pinned = new HashSet<TileKey>();
        TotalBytes = 0;
    }
}
