using Leaf.Pdfium;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using XamlPath = Microsoft.UI.Xaml.Shapes.Path;
using Windows.Foundation;
using Windows.UI;

namespace Leaf.Viewer;

/// <summary>
/// One page on screen: white paper, a canvas of tile images (device-pixel aligned), a stretched low-res
/// placeholder shown until tiles arrive, and two overlay paths (search highlights, text selection).
/// All coordinates inside are page-local DIPs.
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
    private readonly Stack<Image> _imagePool = new();

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

    public void Bind(int pageIndex)
    {
        PageIndex = pageIndex;
    }

    public void Recycle()
    {
        PageIndex = -1;
        ClearTiles();
        _staleTiles.Children.Clear();
        _placeholder.Source = null;
        _highlights.Data = null;
        _currentHit.Data = null;
        _selection.Data = null;
    }

    public void SetPlaceholder(SoftwareBitmapSource? source)
    {
        _placeholder.Source = source;
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

        _staleTiles.Children.Clear();
        foreach (Image img in _tileImages.Values)
        {
            _tiles.Children.Remove(img);
            img.Width *= ratio;
            img.Height *= ratio;
            Canvas.SetLeft(img, Canvas.GetLeft(img) * ratio);
            Canvas.SetTop(img, Canvas.GetTop(img) * ratio);
            _staleTiles.Children.Add(img);
        }

        _tileImages.Clear();
    }

    public void ClearStaleTiles()
    {
        foreach (UIElement child in _staleTiles.Children)
        {
            if (child is Image img)
            {
                img.Source = null;
                _imagePool.Push(img);
            }
        }

        _staleTiles.Children.Clear();
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
