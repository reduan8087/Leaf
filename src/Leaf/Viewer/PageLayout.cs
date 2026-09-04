using Leaf.Pdfium;
using Windows.Foundation;

namespace Leaf.Viewer;

/// <summary>
/// Pure layout math for continuous vertical scrolling: page rectangles in DIPs for a zoom and view rotation,
/// prefix sums for O(log n) lookups, fit-width / fit-page zoom computation. No XAML types except Rect/Size.
/// </summary>
public sealed class PageLayout
{
    public const double GapDip = 8;
    public const double MarginDip = 16;
    private const double PtToDip = 96.0 / 72.0;

    private readonly PdfPageInfo[] _pages;
    private double[] _tops = [];
    private double[] _widths = [];
    private double[] _heights = [];

    public PageLayout(IReadOnlyList<PdfPageInfo> pages)
    {
        _pages = pages.ToArray();
        Update(1.0, 0, 800);
    }

    public int PageCount => _pages.Length;
    public double Zoom { get; private set; }
    public int Rotation { get; private set; }
    public double ViewportWidth { get; private set; }
    public double ExtentWidth { get; private set; }
    public double ExtentHeight { get; private set; }

    public void Update(double zoom, int rotation, double viewportWidth)
    {
        Zoom = zoom;
        Rotation = rotation & 3;
        ViewportWidth = viewportWidth;
        int n = _pages.Length;
        _tops = new double[n];
        _widths = new double[n];
        _heights = new double[n];
        double maxWidth = 0;
        double y = MarginDip;
        bool swap = (Rotation & 1) == 1;
        for (int i = 0; i < n; i++)
        {
            PdfPageInfo p = _pages[i];
            double w = (swap ? p.HeightPt : p.WidthPt) * PtToDip * zoom;
            double h = (swap ? p.WidthPt : p.HeightPt) * PtToDip * zoom;
            _widths[i] = w;
            _heights[i] = h;
            _tops[i] = y;
            y += h + GapDip;
            maxWidth = Math.Max(maxWidth, w);
        }

        ExtentWidth = Math.Max(viewportWidth, maxWidth + 2 * MarginDip);
        ExtentHeight = n == 0 ? 0 : y - GapDip + MarginDip;
    }

    public Rect PageRect(int index)
    {
        double w = _widths[index];
        double x = Math.Max(MarginDip, (ExtentWidth - w) / 2);
        return new Rect(x, _tops[index], w, _heights[index]);
    }

    /// <summary>Index of the page containing or nearest below the given document Y (DIPs).</summary>
    public int PageAt(double y)
    {
        int lo = 0, hi = _pages.Length - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (_tops[mid] <= y)
            {
                lo = mid;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return lo;
    }

    public (int First, int Last) PagesIntersecting(double top, double bottom)
    {
        if (_pages.Length == 0)
        {
            return (0, -1);
        }

        int first = PageAt(top);
        if (_tops[first] + _heights[first] < top && first < _pages.Length - 1)
        {
            first++;
        }

        int last = PageAt(bottom);
        return (first, Math.Max(first, last));
    }

    /// <summary>The page whose area covers most of the viewport, used for the "n / total" indicator.</summary>
    public int DominantPage(double top, double bottom)
    {
        (int first, int last) = PagesIntersecting(top, bottom);
        int best = first;
        double bestCover = -1;
        for (int i = first; i <= last; i++)
        {
            double cover = Math.Min(bottom, _tops[i] + _heights[i]) - Math.Max(top, _tops[i]);
            if (cover > bestCover)
            {
                bestCover = cover;
                best = i;
            }
        }

        return best;
    }

    public double FitWidthZoom(double viewportWidth, int rotation)
    {
        double maxPt = 0;
        bool swap = (rotation & 1) == 1;
        foreach (PdfPageInfo p in _pages)
        {
            maxPt = Math.Max(maxPt, swap ? p.HeightPt : p.WidthPt);
        }

        if (maxPt <= 0)
        {
            return 1;
        }

        return Math.Clamp((viewportWidth - 2 * MarginDip) / (maxPt * PtToDip), 0.1, 8);
    }

    public double FitPageZoom(double viewportWidth, double viewportHeight, int pageIndex, int rotation)
    {
        if (_pages.Length == 0)
        {
            return 1;
        }

        PdfPageInfo p = _pages[Math.Clamp(pageIndex, 0, _pages.Length - 1)];
        bool swap = (rotation & 1) == 1;
        double wPt = swap ? p.HeightPt : p.WidthPt;
        double hPt = swap ? p.WidthPt : p.HeightPt;
        double zw = (viewportWidth - 2 * MarginDip) / (wPt * PtToDip);
        double zh = (viewportHeight - 2 * MarginDip) / (hPt * PtToDip);
        return Math.Clamp(Math.Min(zw, zh), 0.1, 8);
    }
}
