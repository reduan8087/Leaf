using Leaf.Pdfium;
using Windows.Foundation;

namespace Leaf.Viewer;

/// <summary>
/// Pure layout math: page rectangles in DIPs for a zoom, view rotation and <see cref="PageArrangement"/>,
/// plus the fit-zoom calculations. No XAML types except Rect.
/// </summary>
/// <remarks>
/// Pages are grouped into rows of <see cref="PageArrangement.Columns"/>. Rows are always contiguous ranges of
/// pages in order, which is what lets viewport queries stay a binary search over row tops and lets the panel
/// realize a simple first..last range. When scrolling is not continuous only the anchor row is laid out, so the
/// extent is one row tall and paging is a re-layout rather than a scroll.
/// </remarks>
public sealed class PageLayout
{
    public const double GapDip = 8;
    public const double MarginDip = 16;
    private const double PtToDip = 96.0 / 72.0;
    private const double MinZoom = 0.1;
    private const double MaxZoom = 8.0;

    private readonly PdfPageInfo[] _pages;
    private double[] _widths = [];
    private double[] _heights = [];
    private double[] _pageX = [];
    private double[] _pageY = [];

    // Row tables. _rowOf maps a page to its row; the rest are indexed by row.
    private int[] _rowOf = [];
    private int[] _rowFirst = [];
    private int[] _rowLast = [];
    private double[] _rowTop = [];
    private double[] _rowHeight = [];

    public PageLayout(IReadOnlyList<PdfPageInfo> pages)
    {
        _pages = [.. pages];
        Update(1.0, 0, 800, PageArrangement.Default, 0);
    }

    public int PageCount => _pages.Length;
    public double Zoom { get; private set; }
    public int Rotation { get; private set; }
    public double ViewportWidth { get; private set; }
    public double ExtentWidth { get; private set; }
    public double ExtentHeight { get; private set; }
    public PageArrangement Arrangement { get; private set; } = PageArrangement.Default;

    /// <summary>The row shown when scrolling is not continuous. Ignored in continuous mode.</summary>
    public int AnchorPage { get; private set; }

    public int RowCount => _rowFirst.Length;

    public void Update(double zoom, int rotation, double viewportWidth, PageArrangement arrangement, int anchorPage)
    {
        Zoom = zoom;
        Rotation = rotation & 3;
        ViewportWidth = viewportWidth;
        Arrangement = arrangement.Normalized();

        int n = _pages.Length;
        _widths = new double[n];
        _heights = new double[n];
        _pageX = new double[n];
        _pageY = new double[n];
        _rowOf = new int[n];

        bool swap = (Rotation & 1) == 1;
        for (int i = 0; i < n; i++)
        {
            PdfPageInfo p = _pages[i];
            _widths[i] = (swap ? p.HeightPt : p.WidthPt) * PtToDip * zoom;
            _heights[i] = (swap ? p.WidthPt : p.HeightPt) * PtToDip * zoom;
        }

        BuildRows();
        AnchorPage = n == 0 ? 0 : Math.Clamp(anchorPage, 0, n - 1);

        // The extent is as wide as the widest row, and never narrower than the viewport so rows stay centred.
        double widest = 0;
        for (int r = 0; r < _rowFirst.Length; r++)
        {
            widest = Math.Max(widest, RowWidth(r));
        }

        ExtentWidth = Math.Max(viewportWidth, widest + (2 * MarginDip));

        if (_rowFirst.Length == 0)
        {
            ExtentHeight = 0;
            return;
        }

        if (Arrangement.Continuous)
        {
            double y = MarginDip;
            for (int r = 0; r < _rowFirst.Length; r++)
            {
                _rowTop[r] = y;
                PlaceRow(r, y);
                y += _rowHeight[r] + GapDip;
            }

            ExtentHeight = y - GapDip + MarginDip;
        }
        else
        {
            // One row at a time: only the anchor's row gets a position, and the extent is just that row.
            int row = _rowOf[AnchorPage];
            for (int r = 0; r < _rowFirst.Length; r++)
            {
                _rowTop[r] = MarginDip;
            }

            PlaceRow(row, MarginDip);
            ExtentHeight = _rowHeight[row] + (2 * MarginDip);
        }
    }

    /// <summary>
    /// Groups pages into rows. With a separate cover page the first row holds page 1 alone, so that every later
    /// spread pairs an even and an odd page the way a printed book does.
    /// </summary>
    private void BuildRows()
    {
        int n = _pages.Length;
        if (n == 0)
        {
            _rowFirst = [];
            _rowLast = [];
            _rowTop = [];
            _rowHeight = [];
            return;
        }

        int columns = Arrangement.Columns;
        var first = new List<int>((n / columns) + 2);
        var last = new List<int>((n / columns) + 2);

        int page = 0;
        if (Arrangement.HasCover)
        {
            first.Add(0);
            last.Add(0);
            page = 1;
        }

        while (page < n)
        {
            int end = Math.Min(page + columns - 1, n - 1);
            first.Add(page);
            last.Add(end);
            page = end + 1;
        }

        _rowFirst = [.. first];
        _rowLast = [.. last];
        _rowTop = new double[_rowFirst.Length];
        _rowHeight = new double[_rowFirst.Length];

        for (int r = 0; r < _rowFirst.Length; r++)
        {
            double height = 0;
            for (int i = _rowFirst[r]; i <= _rowLast[r]; i++)
            {
                _rowOf[i] = r;
                height = Math.Max(height, _heights[i]);
            }

            _rowHeight[r] = height;
        }
    }

    private double RowWidth(int row)
    {
        double width = 0;
        for (int i = _rowFirst[row]; i <= _rowLast[row]; i++)
        {
            width += _widths[i];
        }

        return width + ((_rowLast[row] - _rowFirst[row]) * GapDip);
    }

    /// <summary>Centres a row horizontally and vertically centres each page within the row's height.</summary>
    private void PlaceRow(int row, double top)
    {
        double x = Math.Max(MarginDip, (ExtentWidth - RowWidth(row)) / 2);
        for (int i = _rowFirst[row]; i <= _rowLast[row]; i++)
        {
            _pageX[i] = x;
            _pageY[i] = top + ((_rowHeight[row] - _heights[i]) / 2);
            x += _widths[i] + GapDip;
        }
    }

    public Rect PageRect(int index) => new(_pageX[index], _pageY[index], _widths[index], _heights[index]);

    public int RowOf(int pageIndex) => _pages.Length == 0 ? 0 : _rowOf[Math.Clamp(pageIndex, 0, _pages.Length - 1)];

    public int FirstPageOfRow(int row) => _rowFirst[Math.Clamp(row, 0, _rowFirst.Length - 1)];

    public int LastPageOfRow(int row) => _rowLast[Math.Clamp(row, 0, _rowLast.Length - 1)];

    /// <summary>The first page of the row <paramref name="delta"/> rows away from the one holding <paramref name="pageIndex"/>.</summary>
    public int PageByRowStep(int pageIndex, int delta)
    {
        if (_rowFirst.Length == 0)
        {
            return 0;
        }

        int row = Math.Clamp(RowOf(pageIndex) + delta, 0, _rowFirst.Length - 1);
        return _rowFirst[row];
    }

    /// <summary>Index of the row containing or nearest below the given document Y (DIPs).</summary>
    private int RowAt(double y)
    {
        int lo = 0;
        int hi = _rowFirst.Length - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (_rowTop[mid] <= y)
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

    /// <summary>Index of the page containing or nearest below the given document Y (DIPs).</summary>
    public int PageAt(double y) => _pages.Length == 0 ? 0 : _rowFirst[RowAt(y)];

    public (int First, int Last) PagesIntersecting(double top, double bottom)
    {
        if (_pages.Length == 0)
        {
            return (0, -1);
        }

        if (!Arrangement.Continuous)
        {
            // Only one row exists on the surface, so it is always the answer.
            int only = _rowOf[AnchorPage];
            return (_rowFirst[only], _rowLast[only]);
        }

        int firstRow = RowAt(top);
        if (_rowTop[firstRow] + _rowHeight[firstRow] < top && firstRow < _rowFirst.Length - 1)
        {
            firstRow++;
        }

        int lastRow = Math.Max(firstRow, RowAt(bottom));
        return (_rowFirst[firstRow], _rowLast[lastRow]);
    }

    /// <summary>The page whose row covers most of the viewport, used for the "n / total" indicator.</summary>
    public int DominantPage(double top, double bottom)
    {
        if (_pages.Length == 0)
        {
            return 0;
        }

        if (!Arrangement.Continuous)
        {
            return _rowFirst[_rowOf[AnchorPage]];
        }

        int firstRow = RowAt(top);
        int lastRow = Math.Max(firstRow, RowAt(bottom));
        int best = firstRow;
        double bestCover = double.NegativeInfinity;
        for (int r = firstRow; r <= lastRow; r++)
        {
            double cover = Math.Min(bottom, _rowTop[r] + _rowHeight[r]) - Math.Max(top, _rowTop[r]);
            if (cover > bestCover)
            {
                bestCover = cover;
                best = r;
            }
        }

        return _rowFirst[best];
    }

    // ------------------------------------------------------------------ fit zooms

    /// <summary>
    /// Fits the widest possible row across the viewport. Based on the widest page in the document rather than
    /// the current one, so the zoom does not jump around as you scroll.
    /// </summary>
    public double FitWidthZoom(double viewportWidth, int rotation, PageArrangement arrangement)
    {
        double maxPt = MaxPageWidthPt(rotation);
        if (maxPt <= 0)
        {
            return 1;
        }

        int columns = arrangement.Normalized().Columns;
        double available = viewportWidth - (2 * MarginDip) - ((columns - 1) * GapDip);
        return Clamp(available / (columns * maxPt * PtToDip));
    }

    /// <summary>Fits the tallest page in the document to the viewport height, for the same stability reason.</summary>
    public double FitHeightZoom(double viewportHeight, int rotation)
    {
        double maxPt = MaxPageHeightPt(rotation);
        return maxPt <= 0 ? 1 : Clamp((viewportHeight - (2 * MarginDip)) / (maxPt * PtToDip));
    }

    /// <summary>Fits the whole row holding <paramref name="pageIndex"/> into the viewport.</summary>
    public double FitPageZoom(double viewportWidth, double viewportHeight, int pageIndex, int rotation, PageArrangement arrangement)
    {
        if (_pages.Length == 0)
        {
            return 1;
        }

        PageArrangement normalized = arrangement.Normalized();
        bool swap = (rotation & 1) == 1;
        pageIndex = Math.Clamp(pageIndex, 0, _pages.Length - 1);

        // Measure the row this page would fall into for the requested arrangement, not the one currently applied.
        (int first, int last) = RowRangeFor(pageIndex, normalized);
        double widthPt = 0;
        double heightPt = 0;
        for (int i = first; i <= last; i++)
        {
            PdfPageInfo p = _pages[i];
            widthPt += swap ? p.HeightPt : p.WidthPt;
            heightPt = Math.Max(heightPt, swap ? p.WidthPt : p.HeightPt);
        }

        if (widthPt <= 0 || heightPt <= 0)
        {
            return 1;
        }

        double zw = (viewportWidth - (2 * MarginDip) - ((last - first) * GapDip)) / (widthPt * PtToDip);
        double zh = (viewportHeight - (2 * MarginDip)) / (heightPt * PtToDip);
        return Clamp(Math.Min(zw, zh));
    }

    /// <summary>The page range of the row that would hold <paramref name="pageIndex"/> under an arrangement.</summary>
    private (int First, int Last) RowRangeFor(int pageIndex, PageArrangement arrangement)
    {
        int columns = arrangement.Columns;
        if (columns == 1)
        {
            return (pageIndex, pageIndex);
        }

        int offset = arrangement.HasCover ? 1 : 0;
        if (arrangement.HasCover && pageIndex == 0)
        {
            return (0, 0);
        }

        int slot = (pageIndex - offset) / columns;
        int first = (slot * columns) + offset;
        return (first, Math.Min(first + columns - 1, _pages.Length - 1));
    }

    private double MaxPageWidthPt(int rotation)
    {
        bool swap = (rotation & 1) == 1;
        double max = 0;
        foreach (PdfPageInfo p in _pages)
        {
            max = Math.Max(max, swap ? p.HeightPt : p.WidthPt);
        }

        return max;
    }

    private double MaxPageHeightPt(int rotation)
    {
        bool swap = (rotation & 1) == 1;
        double max = 0;
        foreach (PdfPageInfo p in _pages)
        {
            max = Math.Max(max, swap ? p.WidthPt : p.HeightPt);
        }

        return max;
    }

    private static double Clamp(double zoom) => double.IsFinite(zoom) ? Math.Clamp(zoom, MinZoom, MaxZoom) : 1;
}
