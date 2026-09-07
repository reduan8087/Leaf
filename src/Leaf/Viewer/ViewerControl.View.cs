using Leaf.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Leaf.Viewer;

/// <summary>
/// View modes: the fit mode and the page arrangement (column count, continuous scrolling, cover page), plus
/// the toolbar state that reflects them. Both axes are chosen independently, which together cover the page
/// display modes a reader expects.
/// </summary>
public sealed partial class ViewerControl
{
    /// <summary>Below this width the right-hand toolbar zone folds into a single overflow button.</summary>
    private const double OverflowWidthDip = 900;

    private bool _updatingToolbar;
    private int _gridColumns = 4;

    /// <summary>Raised when the user asks for full screen from the toolbar; the window owns the presenter.</summary>
    public event Action? FullScreenRequested;

    public FitMode Fit => _fit;

    public PageArrangement Arrangement => _arrangement;

    // ------------------------------------------------------------------ preferences

    private void ApplyPreferences(ViewPreferences preferences)
    {
        _fit = preferences.Fit;
        _arrangement = preferences.Arrangement();
        UpdateViewToolbar();
    }

    private void SavePreferences()
    {
        ViewPreferences preferences = SettingsStore.Current.View;
        preferences.Fit = _fit;
        preferences.SetArrangement(_arrangement);
        SettingsStore.Save();
    }

    /// <summary>The one place the layout is recomputed, so the arrangement and anchor can never be forgotten.</summary>
    private void UpdateLayout(double zoom) =>
        _layout?.Update(zoom, _rotation, Scroller.ViewportWidth, _arrangement, CurrentPage);

    // ------------------------------------------------------------------ fit mode

    public void SetFit(FitMode mode)
    {
        if (_layout is null)
        {
            return;
        }

        SetFitMode(mode);
        ApplyZoom(ComputeModeZoom(), keepTop: true);

        // Fitting a page or a height is only meaningful if that page is actually in view.
        if (mode is FitMode.FitPage or FitMode.FitHeight)
        {
            GoToPage(CurrentPage);
        }
    }

    private void SetFitMode(FitMode mode)
    {
        if (_fit == mode)
        {
            return;
        }

        _fit = mode;
        SavePreferences();
        UpdateViewToolbar();
    }

    // ------------------------------------------------------------------ arrangement

    public void SetColumns(int columns) => SetArrangement(_arrangement with { Columns = columns });

    public void SetContinuous(bool continuous) => SetArrangement(_arrangement with { Continuous = continuous });

    public void SetCoverPageSeparate(bool separate) => SetArrangement(_arrangement with { CoverPageSeparate = separate });

    public void ToggleContinuous() => SetContinuous(!_arrangement.Continuous);

    /// <summary>
    /// Switches the page arrangement, keeping the reader on the page they were looking at. Tile keys are
    /// per page and scale, so the cache survives a column change; only a zoom change demotes tiles.
    /// </summary>
    public void SetArrangement(PageArrangement arrangement)
    {
        arrangement = arrangement.Normalized();
        if (_layout is null || _scheduler is null || arrangement == _arrangement)
        {
            return;
        }

        int anchor = CurrentPage;
        _arrangement = arrangement;
        if (arrangement.Columns >= 3)
        {
            _gridColumns = arrangement.Columns; // the split button's primary action reuses the last grid size
        }

        SavePreferences();

        double zoom = _fit == FitMode.Custom ? _zoom : ComputeModeZoom();
        double ratio = zoom / _zoom;
        _zoom = _pendingZoom = zoom;
        UpdateLayout(zoom);

        foreach (PdfPageView view in PageHost.Realized.Values)
        {
            view.DemoteTilesToStale(ratio);
            ApplyOverlays(view);
        }

        PageHost.InvalidateMeasure();
        PageHost.UpdateLayout();
        _previewTransform.ScaleX = _previewTransform.ScaleY = 1;
        ZoomText.Text = $"{Math.Round(zoom * 100)}%";
        _scheduler.BumpGeneration();
        ScrollToPage(anchor);
        RefreshVisible(prefetch: true);
        UpdateViewToolbar();
        StateChanged?.Invoke(this);
    }

    // ------------------------------------------------------------------ paging

    /// <summary>
    /// Puts a page in view. With continuous scrolling that is a scroll; without it, the surface only ever holds
    /// one row, so it is a re-layout around a new anchor.
    /// </summary>
    private void ScrollToPage(int index)
    {
        if (_layout is null)
        {
            return;
        }

        if (_arrangement.Continuous)
        {
            Rect rect = _layout.PageRect(index);
            Scroller.ChangeView(null, Math.Max(0, rect.Top - PageLayout.GapDip), null, disableAnimation: true);
        }
        else
        {
            SetAnchorRow(index, toBottom: false);
        }
    }

    /// <summary>Re-lays out around <paramref name="page"/> and shows the top (or bottom, when paging backwards).</summary>
    private void SetAnchorRow(int page, bool toBottom)
    {
        if (_layout is null || _session is null)
        {
            return;
        }

        CurrentPage = Math.Clamp(page, 0, _session.PageCount - 1); // read by UpdateLayout as the anchor
        UpdateLayout(_zoom);
        PageHost.InvalidateMeasure();
        PageHost.UpdateLayout();
        Scroller.ChangeView(null, toBottom ? Scroller.ScrollableHeight : 0, null, disableAnimation: true);

        _suppressPageBox = true;
        PageBox.Text = (CurrentPage + 1).ToString();
        _suppressPageBox = false;

        RefreshVisible(prefetch: true);
        StateChanged?.Invoke(this);
    }

    /// <summary>
    /// One screenful in the reading direction. When scrolling is off, this scrolls a zoomed-in page first and
    /// only turns the page once there is nothing left to scroll, which is what a page-at-a-time reader expects.
    /// </summary>
    private void PageStep(int direction)
    {
        if (_layout is null)
        {
            return;
        }

        double step = Math.Max(40, Scroller.ViewportHeight - 40);
        if (_arrangement.Continuous)
        {
            ScrollBy(0, direction * step);
            return;
        }

        bool moreToScroll = direction > 0
            ? Scroller.VerticalOffset < Scroller.ScrollableHeight - 1
            : Scroller.VerticalOffset > 1;
        if (moreToScroll)
        {
            ScrollBy(0, direction * step);
            return;
        }

        int target = _layout.PageByRowStep(CurrentPage, direction);
        if (target != CurrentPage)
        {
            SetAnchorRow(target, toBottom: direction < 0);
        }
    }

    /// <summary>Previous/next move by a row, so a two-page spread advances by a spread rather than half of one.</summary>
    private void RowStep(int direction)
    {
        if (_layout is null)
        {
            return;
        }

        GoToPage(_layout.PageByRowStep(CurrentPage, direction));
    }

    // ------------------------------------------------------------------ after a page edit

    /// <summary>
    /// Rebuilds everything that depends on page indices after pages were rotated, reordered, deleted or
    /// imported. Unlike <see cref="ReloadAsync"/> this keeps the open document, because the edits live in
    /// memory and have not been written to the file yet.
    /// </summary>
    public void RefreshAfterEdit()
    {
        if (_disposed || _session is not DocumentSession session || _scheduler is null)
        {
            return;
        }

        // Every cached tile, highlight and text layout is now about the wrong page.
        CancelSearch();
        ClearSelection();
        _hits.Clear();
        _hitsByPage.Clear();
        _currentHit = -1;
        PageHost.RecycleAll();
        _scheduler.BumpGeneration();
        Retire(_cache.Clear());
        ClearLinkCache();
        if (PanelKind != SidePanelKind.None)
        {
            Panel.Load(session);
        }

        int page = Math.Clamp(CurrentPage, 0, Math.Max(0, session.PageCount - 1));
        _layout = new PageLayout(session.Pages);
        PageHost.Layout = _layout;
        PageCountText.Text = $"/ {session.PageCount}";
        CurrentPage = page;

        double zoom = _fit == FitMode.Custom ? _zoom : ComputeModeZoom();
        _zoom = _pendingZoom = zoom;
        UpdateLayout(zoom);
        PageHost.InvalidateMeasure();
        PageHost.UpdateLayout();
        _previewTransform.ScaleX = _previewTransform.ScaleY = 1;
        ZoomText.Text = $"{Math.Round(zoom * 100)}%";

        _suppressPageBox = true;
        PageBox.Text = (page + 1).ToString();
        _suppressPageBox = false;

        ScrollToPage(page);
        RefreshVisible(prefetch: true);
        StateChanged?.Invoke(this);
    }

    // ------------------------------------------------------------------ toolbar state

    /// <summary>Pushes the current mode onto the toggles. Guarded because setting IsChecked raises Click handlers.</summary>
    private void UpdateViewToolbar()
    {
        if (_updatingToolbar)
        {
            return;
        }

        _updatingToolbar = true;
        try
        {
            FitWidthToggle.IsChecked = _fit == FitMode.FitWidth;
            FitHeightToggle.IsChecked = _fit == FitMode.FitHeight;
            FitPageToggle.IsChecked = _fit == FitMode.FitPage;

            int columns = _arrangement.Columns;
            OneUpToggle.IsChecked = columns == 1;
            TwoUpToggle.IsChecked = columns == 2;
            GridToggle.IsChecked = columns >= 3;
            CoverToggle.IsChecked = _arrangement.CoverPageSeparate;
            CoverToggle.IsEnabled = columns > 1; // a cover page means nothing in a single column
            ScrollToggle.IsChecked = _arrangement.Continuous;
        }
        finally
        {
            _updatingToolbar = false;
        }
    }

    private void OnToolbarSizeChanged(object sender, SizeChangedEventArgs e)
    {
        bool narrow = e.NewSize.Width < OverflowWidthDip;
        RightZone.Visibility = narrow ? Visibility.Collapsed : Visibility.Visible;
        OverflowButton.Visibility = narrow ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnOverflowOpening(object? sender, object e)
    {
        _updatingToolbar = true;
        try
        {
            int columns = _arrangement.Columns;
            OverflowOneUp.IsChecked = columns == 1;
            OverflowTwoUp.IsChecked = columns == 2;
            OverflowGrid.IsChecked = columns >= 3;
            OverflowCover.IsChecked = _arrangement.CoverPageSeparate;
            OverflowCover.IsEnabled = columns > 1;
            OverflowScroll.IsChecked = _arrangement.Continuous;
        }
        finally
        {
            _updatingToolbar = false;
        }
    }

    // ------------------------------------------------------------------ handlers

    private void OnFitHeight(object sender, RoutedEventArgs e) => Guarded(() => SetFit(FitMode.FitHeight));

    private void OnActualSize(object sender, RoutedEventArgs e) => Guarded(ShowActualSize);

    private void OnOneUp(object sender, RoutedEventArgs e) => Guarded(() => SetColumns(1));

    private void OnTwoUp(object sender, RoutedEventArgs e) => Guarded(() => SetColumns(2));

    private void OnColumnCount(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && int.TryParse(tag, out int columns))
        {
            Guarded(() => SetColumns(columns));
        }
    }

    /// <summary>
    /// The grid split button. Checking it applies the last grid size (or four columns); unchecking it goes
    /// back to a single page, which is what pressing an active mode button should do.
    /// </summary>
    private void OnGridToggleChanged(ToggleSplitButton sender, ToggleSplitButtonIsCheckedChangedEventArgs args)
        => Guarded(() => SetColumns(sender.IsChecked ? Math.Max(3, _gridColumns) : 1));

    private void OnCoverToggle(object sender, RoutedEventArgs e) => Guarded(() => SetCoverPageSeparate(!_arrangement.CoverPageSeparate));

    private void OnScrollToggle(object sender, RoutedEventArgs e) => Guarded(ToggleContinuous);

    private void OnFullScreen(object sender, RoutedEventArgs e) => FullScreenRequested?.Invoke();

    /// <summary>
    /// Ignores the Click that <see cref="UpdateViewToolbar"/> itself causes, and restores the toggles
    /// afterwards so a toggle can never end up showing a state the viewer is not in.
    /// </summary>
    private void Guarded(Action action)
    {
        if (_updatingToolbar)
        {
            return;
        }

        action();
        UpdateViewToolbar();
    }
}
