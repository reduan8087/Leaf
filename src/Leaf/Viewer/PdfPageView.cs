using Leaf.Pdfium;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using XamlPath = Microsoft.UI.Xaml.Shapes.Path;
using Windows.Foundation;
using Windows.UI;

namespace Leaf.Viewer;

/// <summary>
/// One page on screen: white paper, a canvas of tile images (device-pixel aligned), a stretched low-res
/// placeholder shown until tiles arrive, and two overlay paths (search highlights, text selection).
/// All coordinates inside are page-local DIPs. Image sources are only ever assigned or cleared here,
/// so the viewer can unbind a cache entry before it is released.
/// </summary>
public sealed partial class PdfPageView : Grid
{
    private static readonly SolidColorBrush PaperBrush = new(Colors.White);
    private static readonly SolidColorBrush ShadowBrush = new(Color.FromArgb(0x30, 0, 0, 0));
    private static readonly SolidColorBrush HighlightBrush = new(Color.FromArgb(0x66, 0xFF, 0xE0, 0x30));
    private static readonly SolidColorBrush CurrentHitBrush = new(Color.FromArgb(0x99, 0xFF, 0x9F, 0x1A));
    private static readonly SolidColorBrush SelectionBrush = new(Color.FromArgb(0x55, 0x33, 0x99, 0xFF));

    private readonly Canvas _staleTiles = new() { UseLayoutRounding = false, IsHitTestVisible = false };
    private readonly Canvas _tiles = new() { UseLayoutRounding = false, IsHitTestVisible = false };
    private readonly Image _placeholder = new() { Stretch = Stretch.Fill, IsHitTestVisible = false };
    private readonly XamlPath _highlights = new() { IsHitTestVisible = false, Fill = HighlightBrush };
    private readonly XamlPath _currentHit = new() { IsHitTestVisible = false, Fill = CurrentHitBrush };
    private readonly XamlPath _selection = new() { IsHitTestVisible = false, Fill = SelectionBrush };
    private readonly Dictionary<TileKey, Image> _tileImages = new();
    private readonly Dictionary<TileKey, Image> _staleImages = new();
    private readonly Stack<Image> _imagePool = new();
    private TileKey? _placeholderKey;

    public PdfPageView()
    {
        Background = PaperBrush;
        BorderBrush = ShadowBrush;
        BorderThickness = new Thickness(1);
        UseLayoutRounding = false;
        Children.Add(_placeholder);
        Children.Add(_staleTiles);
        Children.Add(_tiles);
        Children.Add(_highlights);
        Children.Add(_currentHit);
        Children.Add(_selection);
    }

    public int PageIndex { get; private set; } = -1;

    public bool HasTiles => _tileImages.Count > 0;

    /// <summary>Cache keys of tiles kept on screen from a previous scale; they must stay pinned in the cache until cleared.</summary>
    public IReadOnlyCollection<TileKey> StaleKeys => _staleImages.Keys;

    public TileKey? PlaceholderKey => _placeholderKey;

    public void Bind(int pageIndex)
    {
        PageIndex = pageIndex;
    }

    public void Recycle()
    {
        PageIndex = -1;
        ClearTiles();
        ClearStaleTiles();
        ClearPlaceholder();
        _highlights.Data = null;
        _currentHit.Data = null;
        _selection.Data = null;
    }

    public void SetPlaceholder(TileKey key, ImageSource source)
    {
        _placeholderKey = key;
        _placeholder.Source = source;
    }

    public void ClearPlaceholder()
    {
        _placeholderKey = null;
        _placeholder.Source = null;
    }

    /// <summary>Unbinds a cache entry that is about to be released, wherever this page shows it.</summary>
    public void DropTile(TileKey key)
    {
        if (_tileImages.Remove(key, out Image? img))
        {
            _tiles.Children.Remove(img);
            img.Source = null;
            _imagePool.Push(img);
        }

        if (_staleImages.Remove(key, out Image? stale))
        {
            _staleTiles.Children.Remove(stale);
            stale.Source = null;
            _imagePool.Push(stale);
        }

        if (_placeholderKey == key)
        {
            ClearPlaceholder();
        }
    }

    /// <summary>
    /// Keeps the current tiles on screen (scaled by <paramref name="ratio"/>) while new tiles at another scale are rendered.
    /// </summary>
    public void DemoteTilesToStale(double ratio)
    {
        if (_tileImages.Count == 0)
        {
            return;
        }

        ClearStaleTiles();
        foreach (KeyValuePair<TileKey, Image> kv in _tileImages)
        {
            Image img = kv.Value;
            _tiles.Children.Remove(img);
            img.Width *= ratio;
            img.Height *= ratio;
            Canvas.SetLeft(img, Canvas.GetLeft(img) * ratio);
            Canvas.SetTop(img, Canvas.GetTop(img) * ratio);
            _staleTiles.Children.Add(img);
            _staleImages[kv.Key] = img;
        }

        _tileImages.Clear();
    }

    public void ClearStaleTiles()
    {
        foreach (Image img in _staleImages.Values)
        {
            img.Source = null;
            _imagePool.Push(img);
        }

        _staleTiles.Children.Clear();
        _staleImages.Clear();
    }

    /// <summary>Shows exactly the given tiles; anything else currently shown is removed.</summary>
    public void SetTiles(IReadOnlyList<(TileKey Key, Rect DipRect, TileEntry Entry)> tiles)
    {
        var keep = new HashSet<TileKey>();
        foreach ((TileKey key, Rect rect, TileEntry entry) in tiles)
        {
            keep.Add(key);
            if (!_tileImages.TryGetValue(key, out Image? img))
            {
                img = _imagePool.Count > 0 ? _imagePool.Pop() : new Image { Stretch = Stretch.Fill, IsHitTestVisible = false };
                img.Source = entry.Source;
                _tileImages[key] = img;
                _tiles.Children.Add(img);
            }

            img.Width = rect.Width;
            img.Height = rect.Height;
            Canvas.SetLeft(img, rect.X);
            Canvas.SetTop(img, rect.Y);
        }

        var doomed = new List<TileKey>();
        foreach (KeyValuePair<TileKey, Image> kv in _tileImages)
        {
            if (!keep.Contains(kv.Key))
            {
                doomed.Add(kv.Key);
            }
        }

        foreach (TileKey key in doomed)
        {
            Image img = _tileImages[key];
            _tileImages.Remove(key);
            _tiles.Children.Remove(img);
            img.Source = null;
            _imagePool.Push(img);
        }
    }

    public void ClearTiles()
    {
        foreach (Image img in _tileImages.Values)
        {
            _tiles.Children.Remove(img);
            img.Source = null;
            _imagePool.Push(img);
        }

        _tileImages.Clear();
    }

    // ---- overlays (rects in page-local DIPs) ----

    public void SetHighlights(IEnumerable<Rect> rects, Rect? current)
    {
        _highlights.Data = BuildGeometry(rects);
        _currentHit.Data = current is { } c ? BuildGeometry([c]) : null;
    }

    public void SetSelection(IEnumerable<Rect> rects)
    {
        _selection.Data = BuildGeometry(rects);
    }

    private static GeometryGroup? BuildGeometry(IEnumerable<Rect> rects)
    {
        var group = new GeometryGroup { FillRule = FillRule.Nonzero };
        foreach (Rect r in rects)
        {
            if (r.Width > 0 && r.Height > 0)
            {
                group.Children.Add(new RectangleGeometry { Rect = r });
            }
        }

        return group.Children.Count == 0 ? null : group;
    }

    /// <summary>Converts a rectangle in PDF points on this page to page-local DIPs for the given zoom/rotation.</summary>
    public static Rect PdfRectToDip(PdfRect r, PdfPageInfo page, int rotation, double zoom)
    {
        double scale = zoom * 96.0 / 72.0;
        (double l, double t, double rr, double b) = PdfGeometry.RectToDevice(r, page.WidthPt, page.HeightPt, rotation, scale);
        return new Rect(l, t, Math.Max(0, rr - l), Math.Max(0, b - t));
    }
}
