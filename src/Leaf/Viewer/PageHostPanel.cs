using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Leaf.Viewer;

/// <summary>
/// The scrollable document surface: measures to the whole document extent (so the ScrollViewer gets the right
/// scrollbars) but only holds realized <see cref="PdfPageView"/> children for pages near the viewport.
/// </summary>
public sealed partial class PageHostPanel : Panel
{
    private readonly Dictionary<int, PdfPageView> _realized = new();
    private readonly Stack<PdfPageView> _pool = new();

    public PageLayout? Layout { get; set; }

    public IReadOnlyDictionary<int, PdfPageView> Realized => _realized;

    public PdfPageView? GetView(int pageIndex) => _realized.TryGetValue(pageIndex, out PdfPageView? v) ? v : null;

    /// <summary>Realizes pages [first, last] and recycles the rest. Returns views that were newly created or reused.</summary>
    public List<PdfPageView> SetRealizedRange(int first, int last)
    {
        var added = new List<PdfPageView>();
        if (Layout is null)
        {
            return added;
        }

        var doomed = new List<int>();
        foreach (KeyValuePair<int, PdfPageView> kv in _realized)
        {
            if (kv.Key < first || kv.Key > last)
            {
                doomed.Add(kv.Key);
            }
        }

        foreach (int page in doomed)
        {
            PdfPageView view = _realized[page];
            _realized.Remove(page);
            Children.Remove(view);
            view.Recycle();
            if (_pool.Count < 6)
            {
                _pool.Push(view);
            }
        }

        for (int page = first; page <= last; page++)
        {
            if (_realized.ContainsKey(page))
            {
                continue;
            }

            PdfPageView view = _pool.Count > 0 ? _pool.Pop() : new PdfPageView();
            view.Bind(page);
            _realized[page] = view;
            Children.Add(view);
            added.Add(view);
        }

        if (doomed.Count > 0 || added.Count > 0)
        {
            InvalidateArrange();
        }

        return added;
    }

    public void RecycleAll()
    {
        foreach (PdfPageView view in _realized.Values)
        {
            Children.Remove(view);
            view.Recycle();
        }

        _realized.Clear();
        _pool.Clear();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Layout is null)
        {
            return new Size(0, 0);
        }

        foreach (KeyValuePair<int, PdfPageView> kv in _realized)
        {
            Rect r = Layout.PageRect(kv.Key);
            kv.Value.Measure(new Size(r.Width, r.Height));
        }

        return new Size(Layout.ExtentWidth, Layout.ExtentHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Layout is null)
        {
            return finalSize;
        }

        foreach (KeyValuePair<int, PdfPageView> kv in _realized)
        {
            kv.Value.Arrange(Layout.PageRect(kv.Key));
        }

        return finalSize;
    }
}
