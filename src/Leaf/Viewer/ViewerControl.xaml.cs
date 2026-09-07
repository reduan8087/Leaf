using Leaf.Pdfium;

using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.System;

namespace Leaf.Viewer;

public enum ZoomMode
{
    FitWidth,
    FitPage,
    Custom,
}

/// <summary>
/// The document viewer: continuous scroll of virtualized pages, tile rendering through <see cref="RenderScheduler"/>,
/// two-phase zoom (GPU preview, then crisp re-render), rotation, navigation, find and text selection.
/// </summary>
public sealed partial class ViewerControl : UserControl, IDisposable
{
    private const double MinZoom = 0.1;
    private const double MaxZoom = 8.0;
    private const double PtToDip = 96.0 / 72.0;
    private const int ThumbnailWidthPx = 256;
    private static bool s_firstPageStamped;

    private readonly TileCache _cache = new();
    private readonly ScaleTransform _previewTransform = new();
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _commitTimer;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _prefetchTimer;

    private DocumentSession? _session;
    private RenderScheduler? _scheduler;
    private PageLayout? _layout;
    private Services.DocumentWatcher? _watcher;
    private double _zoom = 1.0;
    private double _pendingZoom = 1.0;
    private Point _zoomAnchor;
    private int _rotation;
    private ZoomMode _zoomMode = ZoomMode.FitWidth;
    private double _rasterizationScale = 1.0;
    private bool _disposed;
    private bool _suppressPageBox;

    public ViewerControl()
    {
        InitializeComponent();
        PageHost.RenderTransform = _previewTransform;

        _commitTimer = DispatcherQueue.CreateTimer();
        _commitTimer.Interval = TimeSpan.FromMilliseconds(150);
        _commitTimer.IsRepeating = false;
        _commitTimer.Tick += (_, _) => CommitZoom();

        _prefetchTimer = DispatcherQueue.CreateTimer();
        _prefetchTimer.Interval = TimeSpan.FromMilliseconds(120);
        _prefetchTimer.IsRepeating = false;
        _prefetchTimer.Tick += (_, _) => RefreshVisible(prefetch: true);

        Scroller.AddHandler(PointerWheelChangedEvent, new PointerEventHandler(OnPointerWheel), handledEventsToo: true);
        Loaded += OnLoaded;
        Unloaded += (_, _) => { _commitTimer.Stop(); _prefetchTimer.Stop(); };
        KeyDown += OnViewerKeyDown;
        InitializeTextInput();
        WireAccelerators();
    }

    public DocumentSession? Session => _session;

    public int PageCount => _session?.PageCount ?? 0;

    public int CurrentPage { get; private set; }

    public double Zoom => _zoom;

    public event Action<ViewerControl>? StateChanged;

    // ------------------------------------------------------------------ lifecycle

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _rasterizationScale = XamlRoot?.RasterizationScale ?? 1.0;
        if (XamlRoot is not null)
        {
            XamlRoot.Changed += OnXamlRootChanged;
        }

        Focus(FocusState.Programmatic);
        RefreshVisible(prefetch: false);
    }

    private void OnXamlRootChanged(XamlRoot sender, XamlRootChangedEventArgs args)
    {
        if (Math.Abs(sender.RasterizationScale - _rasterizationScale) < 1e-6)
        {
            return;
        }

        _rasterizationScale = sender.RasterizationScale;
        if (_scheduler is null)
        {
            return;
        }

        foreach (PdfPageView view in PageHost.Realized.Values)
        {
            view.DemoteTilesToStale(1.0);
        }

        _scheduler.BumpGeneration();
        RefreshVisible(prefetch: false);
    }

    /// <summary>Set by the host: reopens the current file (handles password prompts). Used by "Reload".</summary>
    public Func<Task<DocumentSession?>>? Reopen { get; set; }

    public async Task LoadAsync(DocumentSession session)
    {
        AttachSession(session);

        // Wait for a real viewport size before choosing the fit-width zoom.
        if (Scroller.ViewportWidth <= 0)
        {
            var tcs = new TaskCompletionSource();
            void Handler(object s, SizeChangedEventArgs e) { Scroller.SizeChanged -= Handler; tcs.TrySetResult(); }
            Scroller.SizeChanged += Handler;
            await Task.WhenAny(tcs.Task, Task.Delay(500));
            Scroller.SizeChanged -= Handler;
        }

        _zoomMode = ZoomMode.FitWidth;
        ApplyZoom(ComputeModeZoom(), keepTop: true);
        RefreshVisible(prefetch: true);
        Focus(FocusState.Programmatic);
    }

    private void AttachSession(DocumentSession session)
    {
        _session = session;
        _layout = new PageLayout(session.Pages);
        PageHost.Layout = _layout;
        _scheduler = new RenderScheduler(session, _cache, DispatcherQueue);
        _scheduler.TileReady += OnTileReady;
        _scheduler.RenderFailed += ex => ShowNotice(InfoBarSeverity.Warning, "Rendering problem", ex.Message);
        PageCountText.Text = $"/ {session.PageCount}";
        Busy.IsActive = false;

        _watcher?.Dispose();
        _watcher = new Services.DocumentWatcher(session.Path, DispatcherQueue);
        _watcher.Changed += (_, _) => OfferReload();
    }

    private void DetachSession()
    {
        _watcher?.Dispose();
        _watcher = null;
        CancelSearch();
        ClearSelection();
        _hits.Clear();
        _hitsByPage.Clear();
        _currentHit = -1;
        PageHost.RecycleAll();
        _scheduler?.BumpGeneration();
        Retire(_cache.Clear());
        _session?.Dispose();
        _session = null;
        _scheduler = null;
    }

    private void OfferReload()
    {
        if (_disposed || _session is null)
        {
            return;
        }

        var button = new Button { Content = "Reload" };
        button.Click += (_, _) => { ViewerNotice.IsOpen = false; _ = ReloadAsync(); };
        ViewerNotice.ActionButton = button;
        ShowNotice(InfoBarSeverity.Informational, "This file changed on disk", "Reload to see the latest version.");
    }

    /// <summary>Reopens the file and restores zoom, rotation and scroll position.</summary>
    public async Task ReloadAsync()
    {
        if (_disposed || Reopen is null || _session is null)
        {
            return;
        }

        double zoom = _zoom;
        ZoomMode mode = _zoomMode;
        int rotation = _rotation;
        double x = Scroller.HorizontalOffset;
        double y = Scroller.VerticalOffset;

        DocumentSession? fresh = await Reopen();
        if (fresh is null || _disposed)
        {
            fresh?.Dispose();
            return;
        }

        DetachSession();
        AttachSession(fresh);
        ViewerNotice.ActionButton = null;
        ViewerNotice.IsOpen = false;
        _rotation = rotation;
        _zoomMode = mode;
        _zoom = _pendingZoom = mode == ZoomMode.Custom ? zoom : ComputeModeZoom();
        _layout!.Update(_zoom, _rotation, Scroller.ViewportWidth);
        PageHost.InvalidateMeasure();
        PageHost.UpdateLayout();
        ZoomText.Text = $"{Math.Round(_zoom * 100)}%";
        Scroller.ChangeView(x, Math.Min(y, Scroller.ScrollableHeight), null, disableAnimation: true);
        RefreshVisible(prefetch: true);
        StateChanged?.Invoke(this);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _commitTimer.Stop();
        _prefetchTimer.Stop();
        if (XamlRoot is not null)
        {
            XamlRoot.Changed -= OnXamlRootChanged;
        }

        DetachSession();
    }

    // ------------------------------------------------------------------ viewport → tiles

    private double RenderScale => _zoom * _rasterizationScale * PtToDip;

    private static int ScaleMilli(double renderScale) => (int)Math.Round(renderScale * 1000);

    private Rect ViewportRect => new(Scroller.HorizontalOffset, Scroller.VerticalOffset, Math.Max(1, Scroller.ViewportWidth), Math.Max(1, Scroller.ViewportHeight));

    private void OnViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        RefreshVisible(prefetch: false);
        _prefetchTimer.Stop();
        _prefetchTimer.Start();
    }

    private void OnScrollerSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_layout is null)
        {
            return;
        }

        if (_zoomMode != ZoomMode.Custom)
        {
            double z = ComputeModeZoom();
            if (Math.Abs(z - _zoom) > 1e-3)
            {
                ApplyZoom(z, keepTop: true);
            }
        }
        else
        {
            _layout.Update(_zoom, _rotation, Scroller.ViewportWidth);
            PageHost.InvalidateMeasure();
        }

        // Each cached tile costs CPU (WriteableBitmap) + GPU (XAML surface) memory, so keep the budget modest.
        _cache.BudgetBytes = Math.Clamp((long)(2 * Scroller.ViewportWidth * Scroller.ViewportHeight * _rasterizationScale * _rasterizationScale * 4), 32L << 20, 64L << 20);
        RefreshVisible(prefetch: false);
    }

    /// <summary>Realizes the pages around the viewport and makes sure every visible tile is shown or requested.</summary>
    private void RefreshVisible(bool prefetch)
    {
        if (_layout is null || _scheduler is null || _session is null || _disposed)
        {
            return;
        }

        Rect vp = ViewportRect;
        double aheadFactor = prefetch ? 0.75 : 0.0;
        double behindFactor = prefetch ? 0.25 : 0.0;
        (int first, int last) = _layout.PagesIntersecting(vp.Top - vp.Height * Math.Max(behindFactor, 0.25), vp.Bottom + vp.Height * Math.Max(aheadFactor, 0.25));
        if (last < first)
        {
            return;
        }

        List<PdfPageView> added = PageHost.SetRealizedRange(first, last);
        foreach (PdfPageView view in added)
        {
            ApplyOverlays(view);
        }

        double renderScale = RenderScale;
        int scaleMilli = ScaleMilli(renderScale);
        var pinned = new HashSet<TileKey>();
        var wanted = new HashSet<TileKey>();
        var requests = new List<TileRequest>();
        double cx = vp.Left + vp.Width / 2;
        double cy = vp.Top + vp.Height / 2;

        for (int page = first; page <= last; page++)
        {
            PdfPageView? view = PageHost.GetView(page);
            if (view is null)
            {
                continue;
            }

            Rect pageRect = _layout.PageRect(page);
            Rect visible = Intersect(pageRect, vp);
            Rect reach = Intersect(pageRect, new Rect(vp.Left, vp.Top - vp.Height * behindFactor, vp.Width, vp.Height * (1 + aheadFactor + behindFactor)));
            PdfPageInfo info = _session.Pages[page];
            (int dispW, int dispH) = PdfGeometry.DisplayedSize(info.WidthPt, info.HeightPt, _rotation, renderScale);

            var present = new List<(TileKey, Rect, TileEntry)>();
            bool complete = true;
            foreach ((TileKey key, Rect dipRect, TileRequest req) in TilesFor(page, pageRect, reach, dispW, dispH, scaleMilli, cx, cy))
            {
                bool isVisible = !visible.IsEmpty && Overlaps(dipRect, new Rect(visible.X - pageRect.X, visible.Y - pageRect.Y, visible.Width, visible.Height));
                wanted.Add(key);
                if (isVisible)
                {
                    pinned.Add(key);
                }

                if (_cache.TryGet(key, out TileEntry entry))
                {
                    if (isVisible || view.HasTiles)
                    {
                        present.Add((key, dipRect, entry));
                    }
                }
                else
                {
                    if (isVisible)
                    {
                        complete = false;
                    }

                    requests.Add(req with { Priority = isVisible ? req.Priority : req.Priority + 1000 });
                }
            }

            foreach (TileKey stale in view.StaleKeys)
            {
                pinned.Add(stale);
            }

            view.SetTiles(present);
            if (complete && !visible.IsEmpty)
            {
                view.ClearStaleTiles();
                StampFirstPage();
            }

            EnsurePlaceholder(view, page, info, wanted, requests);
        }

        _cache.SetPinned(pinned);
        _scheduler.SetWanted(wanted);   // publish before queuing so the pdfium thread never sees a stale snapshot
        foreach (TileRequest req in requests)
        {
            _scheduler.Request(req);
        }

        Retire(_cache.EvictOverBudget());
        UpdatePageIndicator(vp);
    }

    /// <summary>
    /// Unbinds retired cache entries from every page view before they are released. Entries are plain
    /// WriteableBitmaps (never disposed), so this only lets XAML drop its surfaces and keeps GC accounting honest.
    /// </summary>
    private void Retire(List<(TileKey Key, TileEntry Entry)> retired)
    {
        if (retired.Count == 0)
        {
            return;
        }

        foreach ((TileKey key, TileEntry entry) in retired)
        {
            foreach (PdfPageView view in PageHost.Realized.Values)
            {
                view.DropTile(key);
            }

            GC.RemoveMemoryPressure(entry.Bytes);
        }
    }

    private IEnumerable<(TileKey Key, Rect DipRect, TileRequest Request)> TilesFor(int page, Rect pageRect, Rect reach, int dispW, int dispH, int scaleMilli, double cx, double cy)
    {
        if (reach.IsEmpty || reach.Width <= 0 || reach.Height <= 0)
        {
            yield break;
        }

        const int T = TileCache.TileSize;
        double rs = _rasterizationScale;
        // reach in page-local device pixels
        double px0 = (reach.X - pageRect.X) * rs;
        double py0 = (reach.Y - pageRect.Y) * rs;
        double px1 = px0 + reach.Width * rs;
        double py1 = py0 + reach.Height * rs;
        int col0 = Math.Max(0, (int)Math.Floor(px0 / T));
        int row0 = Math.Max(0, (int)Math.Floor(py0 / T));
        int col1 = Math.Min((dispW - 1) / T, (int)Math.Floor((px1 - 1) / T));
        int row1 = Math.Min((dispH - 1) / T, (int)Math.Floor((py1 - 1) / T));
        for (int row = row0; row <= row1; row++)
        {
            for (int col = col0; col <= col1; col++)
            {
                int x = col * T;
                int y = row * T;
                int w = Math.Min(T, dispW - x);
                int h = Math.Min(T, dispH - y);
                if (w <= 0 || h <= 0)
                {
                    continue;
                }

                var key = new TileKey(page, col, row, scaleMilli, _rotation);
                var dip = new Rect(x / rs, y / rs, w / rs, h / rs);
                double tcx = pageRect.X + dip.X + dip.Width / 2;
                double tcy = pageRect.Y + dip.Y + dip.Height / 2;
                int priority = (int)(Math.Sqrt((tcx - cx) * (tcx - cx) + (tcy - cy) * (tcy - cy)) / 64);
                yield return (key, dip, new TileRequest(key, dispW, dispH, x, y, w, h, priority));
            }
        }
    }

    private void EnsurePlaceholder(PdfPageView view, int page, PdfPageInfo info, HashSet<TileKey> wanted, List<TileRequest> requests)
    {
        if (_scheduler is null || view.HasTiles)
        {
            return;
        }

        double widthPt = (_rotation & 1) == 1 ? info.HeightPt : info.WidthPt;
        double thumbScale = ThumbnailWidthPx / Math.Max(1, widthPt);
        (int w, int h) = PdfGeometry.DisplayedSize(info.WidthPt, info.HeightPt, _rotation, thumbScale);
        var key = new TileKey(page, 0, 0, ScaleMilli(thumbScale), _rotation);
        if (_cache.TryGet(key, out TileEntry entry))
        {
            if (view.PlaceholderKey != key)
            {
                view.SetPlaceholder(key, entry.Source);
            }
        }
        else
        {
            // Highest priority: a 256 px placeholder is cheap and makes the page appear immediately; crisp tiles follow.
            wanted.Add(key);
            requests.Add(new TileRequest(key, w, h, 0, 0, w, h, -5));
        }
    }

    private void OnTileReady(TileKey key)
    {
        if (_disposed || _layout is null)
        {
            return;
        }

        if (PageHost.GetView(key.Page) is not null)
        {
            RefreshVisible(prefetch: true);
        }
    }

    private void StampFirstPage()
    {
        if (s_firstPageStamped)
        {
            return;
        }

        s_firstPageStamped = true;
        PerfLog.Stamp("first-page");
    }

    private void UpdatePageIndicator(Rect vp)
    {
        if (_layout is null)
        {
            return;
        }

        int page = _layout.DominantPage(vp.Top, vp.Bottom);
        if (page != CurrentPage || PageBox.Text.Length == 0)
        {
            CurrentPage = page;
            _suppressPageBox = true;
            PageBox.Text = (page + 1).ToString();
            _suppressPageBox = false;
            StateChanged?.Invoke(this);
        }
    }

    // ------------------------------------------------------------------ zoom

    private double ComputeModeZoom()
    {
        if (_layout is null)
        {
            return 1;
        }

        double vw = Scroller.ViewportWidth > 0 ? Scroller.ViewportWidth : ActualWidth;
        double vh = Scroller.ViewportHeight > 0 ? Scroller.ViewportHeight : ActualHeight;
        return _zoomMode switch
        {
            ZoomMode.FitWidth => _layout.FitWidthZoom(vw - 2, _rotation),
            ZoomMode.FitPage => _layout.FitPageZoom(vw - 2, vh, CurrentPage, _rotation),
            _ => _zoom,
        };
    }

    private void OnPointerWheel(object sender, PointerRoutedEventArgs e)
    {
        if (!e.KeyModifiers.HasFlag(VirtualKeyModifiers.Control) || _layout is null)
        {
            return;
        }

        var point = e.GetCurrentPoint(Scroller);
        int notches = point.Properties.MouseWheelDelta / 120;
        if (notches == 0)
        {
            notches = Math.Sign(point.Properties.MouseWheelDelta);
        }

        BeginZoom(_pendingZoom * Math.Pow(1.1, notches), point.Position);
        e.Handled = true;
    }

    /// <summary>Phase 1 of a zoom: GPU-scale the current content around the anchor and schedule the commit.</summary>
    private void BeginZoom(double target, Point anchorInViewport)
    {
        if (_layout is null)
        {
            return;
        }

        _zoomMode = ZoomMode.Custom;
        _pendingZoom = Math.Clamp(target, MinZoom, MaxZoom);
        _zoomAnchor = anchorInViewport;
        double ratio = _pendingZoom / _zoom;
        _previewTransform.CenterX = Scroller.HorizontalOffset + anchorInViewport.X;
        _previewTransform.CenterY = Scroller.VerticalOffset + anchorInViewport.Y;
        _previewTransform.ScaleX = ratio;
        _previewTransform.ScaleY = ratio;
        ZoomText.Text = $"{Math.Round(_pendingZoom * 100)}%";
        _commitTimer.Stop();
        _commitTimer.Start();
    }

    /// <summary>Phase 2: re-layout at the new zoom, keep the anchor point fixed, re-render crisp tiles.</summary>
    private void CommitZoom()
    {
        if (_layout is null || Math.Abs(_pendingZoom - _zoom) < 1e-6)
        {
            _previewTransform.ScaleX = _previewTransform.ScaleY = 1;
            return;
        }

        double z0 = _zoom;
        double z1 = _pendingZoom;
        double nx = (Scroller.HorizontalOffset + _zoomAnchor.X) * (z1 / z0) - _zoomAnchor.X;
        double ny = (Scroller.VerticalOffset + _zoomAnchor.Y) * (z1 / z0) - _zoomAnchor.Y;
        ApplyZoomCore(z1, nx, ny);
    }

    private void ApplyZoom(double zoom, bool keepTop)
    {
        if (_layout is null)
        {
            return;
        }

        zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        _pendingZoom = zoom;
        double ratio = zoom / _zoom;
        double ny = keepTop ? Scroller.VerticalOffset * ratio : (Scroller.VerticalOffset + Scroller.ViewportHeight / 2) * ratio - Scroller.ViewportHeight / 2;
        double nx = (Scroller.HorizontalOffset + Scroller.ViewportWidth / 2) * ratio - Scroller.ViewportWidth / 2;
        ApplyZoomCore(zoom, nx, ny);
    }

    private void ApplyZoomCore(double zoom, double newX, double newY)
    {
        if (_layout is null || _scheduler is null)
        {
            return;
        }

        double ratio = zoom / _zoom;
        _zoom = zoom;
        _pendingZoom = zoom;
        _layout.Update(zoom, _rotation, Scroller.ViewportWidth);
        foreach (PdfPageView view in PageHost.Realized.Values)
        {
            view.DemoteTilesToStale(ratio);
            ApplyOverlays(view);
        }

        PageHost.InvalidateMeasure();
        PageHost.UpdateLayout();
        _previewTransform.ScaleX = _previewTransform.ScaleY = 1;
        Scroller.ChangeView(Math.Max(0, newX), Math.Max(0, newY), null, disableAnimation: true);
        _scheduler.BumpGeneration();
        ZoomText.Text = $"{Math.Round(zoom * 100)}%";
        RefreshVisible(prefetch: false);
        StateChanged?.Invoke(this);
    }

    public void ZoomIn() => BeginZoom(_pendingZoom * 1.25, new Point(Scroller.ViewportWidth / 2, Scroller.ViewportHeight / 2));

    public void ZoomOut() => BeginZoom(_pendingZoom / 1.25, new Point(Scroller.ViewportWidth / 2, Scroller.ViewportHeight / 2));

    public void SetZoom(double zoom)
    {
        _zoomMode = ZoomMode.Custom;
        ApplyZoom(zoom, keepTop: false);
    }

    public double ViewportHeight => Scroller.ViewportHeight;

    public double VerticalOffset => Scroller.VerticalOffset;

    public double ScrollableHeight => Scroller.ScrollableHeight;

    public bool CanScrollDown => Scroller.VerticalOffset < Scroller.ScrollableHeight - 1;

    /// <summary>Hides the toolbar in full screen; the window reveals it again near the top edge.</summary>
    public void SetToolbarVisible(bool visible) => Toolbar.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

    public void FitWidth()
    {
        _zoomMode = ZoomMode.FitWidth;
        ApplyZoom(ComputeModeZoom(), keepTop: true);
    }

    public void FitPage()
    {
        _zoomMode = ZoomMode.FitPage;
        ApplyZoom(ComputeModeZoom(), keepTop: true);
        GoToPage(CurrentPage);
    }

    private void OnZoomIn(object sender, RoutedEventArgs e) => ZoomIn();

    private void OnZoomOut(object sender, RoutedEventArgs e) => ZoomOut();

    private void OnFitWidth(object sender, RoutedEventArgs e) => FitWidth();

    private void OnFitPage(object sender, RoutedEventArgs e) => FitPage();

    private void OnZoomPreset(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem { Tag: string tag } && double.TryParse(tag, System.Globalization.CultureInfo.InvariantCulture, out double z))
        {
            _zoomMode = ZoomMode.Custom;
            ApplyZoom(z, keepTop: false);
        }
    }

    // ------------------------------------------------------------------ rotation

    public void Rotate(int quarterTurns)
    {
        if (_layout is null || _scheduler is null || _session is null)
        {
            return;
        }

        int page = CurrentPage;
        double pageTop = _layout.PageRect(page).Top;
        double fraction = _layout.PageRect(page).Height > 0 ? (Scroller.VerticalOffset - pageTop) / _layout.PageRect(page).Height : 0;

        _rotation = (_rotation + quarterTurns + 4) & 3;
        foreach (PdfPageView view in PageHost.Realized.Values)
        {
            view.ClearTiles();
            view.ClearStaleTiles();
            view.ClearPlaceholder();
        }

        _scheduler.BumpGeneration();
        Retire(_cache.Clear());
        double zoom = _zoomMode == ZoomMode.Custom ? _zoom : ComputeModeZoomForRotation();
        _zoom = _pendingZoom = zoom;
        _layout.Update(zoom, _rotation, Scroller.ViewportWidth);
        PageHost.InvalidateMeasure();
        PageHost.UpdateLayout();
        Rect newRect = _layout.PageRect(page);
        Scroller.ChangeView(null, Math.Max(0, newRect.Top + fraction * newRect.Height), null, disableAnimation: true);
        ZoomText.Text = $"{Math.Round(zoom * 100)}%";
        foreach (PdfPageView view in PageHost.Realized.Values)
        {
            ApplyOverlays(view);
        }

        RefreshVisible(prefetch: false);
    }

    private double ComputeModeZoomForRotation() => ComputeModeZoom();

    private void OnRotateLeft(object sender, RoutedEventArgs e) => Rotate(-1);

    private void OnRotateRight(object sender, RoutedEventArgs e) => Rotate(+1);

    // ------------------------------------------------------------------ navigation

    public void GoToPage(int index)
    {
        if (_layout is null || _session is null)
        {
            return;
        }

        index = Math.Clamp(index, 0, _session.PageCount - 1);
        Rect r = _layout.PageRect(index);
        Scroller.ChangeView(null, Math.Max(0, r.Top - PageLayout.GapDip), null, disableAnimation: false);
    }

    public void ScrollBy(double dx, double dy, bool animate = true)
    {
        Scroller.ChangeView(Scroller.HorizontalOffset + dx, Scroller.VerticalOffset + dy, null, disableAnimation: !animate);
    }

    private void OnPrevPage(object sender, RoutedEventArgs e) => GoToPage(CurrentPage - 1);

    private void OnNextPage(object sender, RoutedEventArgs e) => GoToPage(CurrentPage + 1);

    private void OnPageBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            CommitPageBox();
            Focus(FocusState.Programmatic);
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Escape)
        {
            PageBox.Text = (CurrentPage + 1).ToString();
            Focus(FocusState.Programmatic);
            e.Handled = true;
        }
    }

    private void OnPageBoxLostFocus(object sender, RoutedEventArgs e)
    {
        if (!_suppressPageBox)
        {
            PageBox.Text = (CurrentPage + 1).ToString();
        }
    }

    private void CommitPageBox()
    {
        if (int.TryParse(PageBox.Text.Trim(), out int page))
        {
            GoToPage(page - 1);
        }
    }

    public void FocusPageBox()
    {
        PageBox.Focus(FocusState.Programmatic);
        PageBox.SelectAll();
    }

    // ------------------------------------------------------------------ keyboard

    private void WireAccelerators()
    {
        void Add(VirtualKey key, VirtualKeyModifiers mods, Action action)
        {
            var acc = new KeyboardAccelerator { Key = key, Modifiers = mods };
            acc.Invoked += (_, args) => { action(); args.Handled = true; };
            KeyboardAccelerators.Add(acc);
        }

        Add(VirtualKey.F, VirtualKeyModifiers.Control, ShowFind);
        Add(VirtualKey.G, VirtualKeyModifiers.Control, FocusPageBox);
        Add(VirtualKey.Number0, VirtualKeyModifiers.Control, FitWidth);
        Add(VirtualKey.Add, VirtualKeyModifiers.Control, ZoomIn);
        Add(VirtualKey.Subtract, VirtualKeyModifiers.Control, ZoomOut);
        Add((VirtualKey)0xBB, VirtualKeyModifiers.Control, ZoomIn);      // main-row '='/'+'
        Add((VirtualKey)0xBD, VirtualKeyModifiers.Control, ZoomOut);     // main-row '-'
        Add(VirtualKey.R, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, () => Rotate(+1));
        Add(VirtualKey.L, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, () => Rotate(-1));
        Add(VirtualKey.F3, VirtualKeyModifiers.None, () => FindStep(+1));
        Add(VirtualKey.F3, VirtualKeyModifiers.Shift, () => FindStep(-1));
        Add(VirtualKey.A, VirtualKeyModifiers.Control, SelectAllOnCurrentPage);
        Add(VirtualKey.C, VirtualKeyModifiers.Control, CopySelection);
    }

    private void OnViewerKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (FocusManager.GetFocusedElement(XamlRoot) is TextBox)
        {
            return; // typing in the page box or find box
        }

        double vh = Scroller.ViewportHeight;
        bool shift = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        switch (e.Key)
        {
            case VirtualKey.PageDown:
                ScrollBy(0, vh - 40);
                break;
            case VirtualKey.PageUp:
                ScrollBy(0, -(vh - 40));
                break;
            case VirtualKey.Space:
                ScrollBy(0, shift ? -(vh - 40) : vh - 40);
                break;
            case VirtualKey.Down:
                ScrollBy(0, 60, animate: false);
                break;
            case VirtualKey.Up:
                ScrollBy(0, -60, animate: false);
                break;
            case VirtualKey.Right:
                ScrollBy(60, 0, animate: false);
                break;
            case VirtualKey.Left:
                ScrollBy(-60, 0, animate: false);
                break;
            case VirtualKey.Home:
                Scroller.ChangeView(null, 0, null, disableAnimation: false);
                break;
            case VirtualKey.End:
                Scroller.ChangeView(null, Scroller.ScrollableHeight, null, disableAnimation: false);
                break;
            case VirtualKey.Escape:
                if (FindBar.Visibility == Visibility.Visible)
                {
                    HideFind();
                }
                else if (HasSelection)
                {
                    ClearSelection();
                }
                else
                {
                    return; // nothing to dismiss here: let the window leave full screen
                }

                break;
            default:
                return;
        }

        e.Handled = true;
    }

    // ------------------------------------------------------------------ helpers

    private void ShowNotice(InfoBarSeverity severity, string title, string message)
    {
        ViewerNotice.Severity = severity;
        ViewerNotice.Title = title;
        ViewerNotice.Message = message;
        ViewerNotice.IsOpen = true;
    }

    private static Rect Intersect(Rect a, Rect b)
    {
        double x1 = Math.Max(a.Left, b.Left);
        double y1 = Math.Max(a.Top, b.Top);
        double x2 = Math.Min(a.Right, b.Right);
        double y2 = Math.Min(a.Bottom, b.Bottom);
        return x2 <= x1 || y2 <= y1 ? Rect.Empty : new Rect(x1, y1, x2 - x1, y2 - y1);
    }

    private static bool Overlaps(Rect a, Rect b) => a.Left < b.Right && a.Right > b.Left && a.Top < b.Bottom && a.Bottom > b.Top;
}
