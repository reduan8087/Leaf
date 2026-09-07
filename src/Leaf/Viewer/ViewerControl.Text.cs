using System.Text;
using Leaf.Pdfium;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.System;

namespace Leaf.Viewer;

/// <summary>Find (Ctrl+F) and text selection/copy. Coordinates: page-local DIPs ↔ PDF points via <see cref="PdfGeometry"/>.</summary>
public sealed partial class ViewerControl
{
    // ---- find state ----
    private readonly List<PdfSearchHit> _hits = new();
    private readonly Dictionary<int, List<PdfSearchHit>> _hitsByPage = new();
    private CancellationTokenSource? _searchCts;
    private DispatcherQueueTimerHolder? _findDebounce;
    private string _searchTerm = string.Empty;
    private int _currentHit = -1;

    // ---- selection state ----
    private (int Page, int Index)? _selAnchor;
    private (int Page, int Index)? _selFocus;
    private bool _selecting;
    private uint _selectingPointerId;
    private readonly InputCursor _ibeam = InputSystemCursor.Create(InputSystemCursorShape.IBeam);
    private readonly InputCursor _arrow = InputSystemCursor.Create(InputSystemCursorShape.Arrow);

    private void InitializeTextInput()
    {
        PageHost.PointerPressed += OnPagePointerPressed;
        PageHost.PointerMoved += OnPagePointerMoved;
        PageHost.PointerReleased += OnPagePointerReleased;
        PageHost.PointerCaptureLost += (_, _) => _selecting = false;
        PageHost.DoubleTapped += OnPageDoubleTapped;
    }

    // ================================================================== find

    public void ShowFind()
    {
        FindBar.Visibility = Visibility.Visible;
        FindBox.Focus(FocusState.Programmatic);
        FindBox.SelectAll();
    }

    /// <summary>Opens the find bar with a term and starts searching immediately.</summary>
    public void Find(string term)
    {
        FindBar.Visibility = Visibility.Visible;
        FindBox.Text = term;
        StartSearch(term);
    }

    public void FindStep(int delta) => FindStepCore(delta);

    public void HideFind()
    {
        FindBar.Visibility = Visibility.Collapsed;
        CancelSearch();
        _hits.Clear();
        _hitsByPage.Clear();
        _currentHit = -1;
        _searchTerm = string.Empty;
        foreach (PdfPageView view in PageHost.Realized.Values)
        {
            view.SetHighlights([], null);
        }

        Focus(FocusState.Programmatic);
    }

    private void OnFindButton(object sender, RoutedEventArgs e) => ShowFind();

    private void OnFindClose(object sender, RoutedEventArgs e) => HideFind();

    private void OnFindNext(object sender, RoutedEventArgs e) => FindStep(+1);

    private void OnFindPrev(object sender, RoutedEventArgs e) => FindStep(-1);

    private void OnMatchCaseToggled(object sender, RoutedEventArgs e) => StartSearch(FindBox.Text);

    private void OnFindTextChanged(object sender, TextChangedEventArgs e)
    {
        _findDebounce ??= new DispatcherQueueTimerHolder(DispatcherQueue, TimeSpan.FromMilliseconds(250), () => StartSearch(FindBox.Text));
        _findDebounce.Restart();
    }

    private void OnFindBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        bool shift = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        switch (e.Key)
        {
            case VirtualKey.Enter:
                if (_searchTerm != FindBox.Text)
                {
                    StartSearch(FindBox.Text);
                }
                else
                {
                    FindStep(shift ? -1 : +1);
                }

                e.Handled = true;
                break;
            case VirtualKey.Escape:
                HideFind();
                e.Handled = true;
                break;
        }
    }

    private void CancelSearch()
    {
        _searchCts?.Cancel();
        _searchCts = null;
    }

    private async void StartSearch(string term)
    {
        CancelSearch();
        _hits.Clear();
        _hitsByPage.Clear();
        _currentHit = -1;
        _searchTerm = term;
        foreach (PdfPageView view in PageHost.Realized.Values)
        {
            view.SetHighlights([], null);
        }

        if (_session is null || string.IsNullOrEmpty(term))
        {
            FindStatus.Text = string.Empty;
            return;
        }

        var cts = new CancellationTokenSource();
        _searchCts = cts;
        bool matchCase = MatchCaseToggle.IsChecked == true;
        FindStatus.Text = "Searching…";
        int count = _session.PageCount;
        int start = CurrentPage;
        try
        {
            for (int i = 0; i < count; i++)
            {
                int page = (start + i) % count;
                List<PdfSearchHit> hits = await _session.SearchPageAsync(page, term, matchCase, cts.Token);
                if (cts.IsCancellationRequested)
                {
                    return;
                }

                if (hits.Count > 0)
                {
                    _hitsByPage[page] = hits;
                    // keep _hits in document order
                    int insertAt = _hits.FindIndex(h => h.PageIndex > page);
                    if (insertAt < 0)
                    {
                        _hits.AddRange(hits);
                    }
                    else
                    {
                        _hits.InsertRange(insertAt, hits);
                    }

                    if (_currentHit < 0)
                    {
                        _currentHit = _hits.FindIndex(h => h.PageIndex == page);
                        await ScrollToHitAsync(_currentHit);
                    }

                    if (PageHost.GetView(page) is PdfPageView view)
                    {
                        await ApplyHighlightsAsync(view);
                    }

                    UpdateFindStatus();
                }
            }

            UpdateFindStatus(done: true);
        }
        catch (OperationCanceledException)
        {
            // superseded
        }
        catch (PdfException ex)
        {
            FindStatus.Text = "Error";
            ShowNotice(InfoBarSeverity.Warning, "Search failed", ex.Message);
        }
    }

    private void UpdateFindStatus(bool done = false)
    {
        if (_hits.Count == 0)
        {
            FindStatus.Text = done ? "No results" : "Searching…";
            return;
        }

        FindStatus.Text = $"{_currentHit + 1} of {_hits.Count}{(done ? string.Empty : "+")}";
    }

    private void FindStepCore(int delta)
    {
        if (_hits.Count == 0)
        {
            if (FindBar.Visibility != Visibility.Visible)
            {
                ShowFind();
            }

            return;
        }

        int previous = _currentHit;
        _currentHit = ((_currentHit + delta) % _hits.Count + _hits.Count) % _hits.Count;
        _ = ScrollToHitAsync(_currentHit);
        UpdateFindStatus(done: _searchCts is null || _searchCts.IsCancellationRequested);
        if (previous >= 0 && previous < _hits.Count && _hits[previous].PageIndex != _hits[_currentHit].PageIndex && PageHost.GetView(_hits[previous].PageIndex) is PdfPageView old)
        {
            _ = ApplyHighlightsAsync(old);
        }

        if (PageHost.GetView(_hits[_currentHit].PageIndex) is PdfPageView view)
        {
            _ = ApplyHighlightsAsync(view);
        }
    }

    private async Task ScrollToHitAsync(int hitIndex)
    {
        if (_session is null || _layout is null || hitIndex < 0 || hitIndex >= _hits.Count)
        {
            return;
        }

        PdfSearchHit hit = _hits[hitIndex];
        PdfTextLayout layout = await _session.GetTextLayoutAsync(hit.PageIndex, priority: 100);
        IReadOnlyList<PdfRect> rects = layout.GetRects(hit.CharIndex, hit.CharCount);
        Rect pageRect = _layout.PageRect(hit.PageIndex);
        double targetY = pageRect.Top;
        double targetX = pageRect.Left;
        if (rects.Count > 0)
        {
            Rect dip = PdfPageView.PdfRectToDip(rects[0], _session.Pages[hit.PageIndex], _rotation, _zoom);
            targetY = pageRect.Top + dip.Y - Scroller.ViewportHeight / 3;
            targetX = pageRect.Left + dip.X - Scroller.ViewportWidth / 2;
        }

        double? x = Scroller.ScrollableWidth > 0 ? Math.Clamp(targetX, 0, Scroller.ScrollableWidth) : null;
        Scroller.ChangeView(x, Math.Max(0, targetY), null, disableAnimation: false);
    }

    /// <summary>Re-applies highlights and selection for a page view (after realize, zoom, rotation or new results).</summary>
    private void ApplyOverlays(PdfPageView view)
    {
        _ = ApplyHighlightsAsync(view);
        ApplySelection(view);
    }

    private async Task ApplyHighlightsAsync(PdfPageView view)
    {
        if (_session is null)
        {
            return;
        }

        int page = view.PageIndex;
        if (page < 0 || !_hitsByPage.TryGetValue(page, out List<PdfSearchHit>? hits))
        {
            view.SetHighlights([], null);
            return;
        }

        PdfTextLayout layout = await _session.GetTextLayoutAsync(page, priority: 200);
        if (view.PageIndex != page)
        {
            return; // recycled meanwhile
        }

        var rects = new List<Rect>();
        Rect? current = null;
        PdfPageInfo info = _session.Pages[page];
        PdfSearchHit? currentHit = _currentHit >= 0 && _currentHit < _hits.Count ? _hits[_currentHit] : null;
        foreach (PdfSearchHit hit in hits)
        {
            foreach (PdfRect r in layout.GetRects(hit.CharIndex, hit.CharCount))
            {
                Rect dip = PdfPageView.PdfRectToDip(r, info, _rotation, _zoom);
                if (currentHit is { } ch && ch.PageIndex == hit.PageIndex && ch.CharIndex == hit.CharIndex)
                {
                    current = dip;
                }
                else
                {
                    rects.Add(dip);
                }
            }
        }

        view.SetHighlights(rects, current);
    }

    // ================================================================== selection

    public bool HasSelection => _selAnchor is not null && _selFocus is not null;

    public void ClearSelection()
    {
        _selAnchor = null;
        _selFocus = null;
        foreach (PdfPageView view in PageHost.Realized.Values)
        {
            view.SetSelection([]);
        }
    }

    private (int Page, Point PagePoint)? HitPage(Point pointInHost)
    {
        if (_layout is null || _session is null)
        {
            return null;
        }

        (int first, int last) = _layout.PagesIntersecting(pointInHost.Y, pointInHost.Y);
        for (int page = first; page <= last; page++)
        {
            Rect r = _layout.PageRect(page);
            if (pointInHost.X >= r.Left && pointInHost.X <= r.Right && pointInHost.Y >= r.Top && pointInHost.Y <= r.Bottom)
            {
                return (page, new Point(pointInHost.X - r.Left, pointInHost.Y - r.Top));
            }
        }

        return null;
    }

    private (double X, double Y) ToPagePoints(int page, Point dip)
    {
        PdfPageInfo info = _session!.Pages[page];
        return PdfGeometry.DeviceToPage(dip.X, dip.Y, info.WidthPt, info.HeightPt, _rotation, _zoom * PtToDip);
    }

    private async void OnPagePointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(PageHost);
        if (!point.Properties.IsLeftButtonPressed || _session is null)
        {
            return;
        }

        Focus(FocusState.Programmatic);
        (int page, Point local)? hit = HitPage(point.Position);
        if (hit is null)
        {
            ClearSelection();
            return;
        }

        // A press on a link is a click, not the start of a drag-selection.
        _pressedLink = LinkAt(hit.Value.page, hit.Value.local);
        _pressedAt = point.Position;
        if (_pressedLink is not null)
        {
            e.Handled = true;
            return;
        }

        PdfTextLayout layout = await _session.GetTextLayoutAsync(hit.Value.page, priority: 50);
        if (layout.CharCount == 0)
        {
            ClearSelection();
            return;
        }

        (double x, double y) = ToPagePoints(hit.Value.page, hit.Value.local);
        int index = layout.HitTest(x, y, 2);
        if (index < 0)
        {
            index = layout.NearestChar(x, y);
        }

        if (index < 0)
        {
            ClearSelection();
            return;
        }

        ClearSelection();
        _selAnchor = (hit.Value.page, index);
        _selFocus = _selAnchor;
        _selecting = true;
        _selectingPointerId = e.Pointer.PointerId;
        PageHost.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnPagePointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_session is null)
        {
            return;
        }

        var point = e.GetCurrentPoint(PageHost);
        (int page, Point local)? hit = HitPage(point.Position);

        if (_selecting && e.Pointer.PointerId == _selectingPointerId && _selAnchor is not null)
        {
            if (hit is null)
            {
                return;
            }

            PdfTextLayout? layout = _session.TryGetCachedTextLayout(hit.Value.page);
            if (layout is null)
            {
                _ = _session.GetTextLayoutAsync(hit.Value.page, priority: 50);
                return;
            }

            (double x, double y) = ToPagePoints(hit.Value.page, hit.Value.local);
            int index = layout.CharCount == 0 ? -1 : layout.NearestChar(x, y);
            if (index >= 0)
            {
                _selFocus = (hit.Value.page, index);
                UpdateSelectionVisuals();
            }

            e.Handled = true;
            return;
        }

        // hover cursor: a link takes precedence, since clicking it does something other than select
        if (hit is not null && LinkAt(hit.Value.page, hit.Value.local) is PdfLink hovered)
        {
            ProtectedCursor = _hand;
            ShowLinkTarget(hovered);
            return;
        }

        HideLinkTarget();
        bool overText = false;
        if (hit is not null && _session.TryGetCachedTextLayout(hit.Value.page) is PdfTextLayout cached && cached.CharCount > 0)
        {
            (double x, double y) = ToPagePoints(hit.Value.page, hit.Value.local);
            overText = cached.HitTest(x, y, 1) >= 0;
        }
        else if (hit is not null)
        {
            _ = _session.GetTextLayoutAsync(hit.Value.page, priority: 400);
        }

        ProtectedCursor = overText ? _ibeam : _arrow;
    }

    private async void OnPagePointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_pressedLink is PdfLink link)
        {
            _pressedLink = null;
            Point position = e.GetCurrentPoint(PageHost).Position;
            bool moved = Math.Abs(position.X - _pressedAt.X) > ClickSlopDip || Math.Abs(position.Y - _pressedAt.Y) > ClickSlopDip;
            e.Handled = true;
            if (!moved)
            {
                await ActivateLinkAsync(link);
            }

            return;
        }

        if (_selecting && e.Pointer.PointerId == _selectingPointerId)
        {
            _selecting = false;
            PageHost.ReleasePointerCapture(e.Pointer);
            if (_selAnchor is { } a && _selFocus is { } f && a == f)
            {
                ClearSelection(); // a click without drag selects nothing
            }

            e.Handled = true;
        }
    }

    private async void OnPageDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (_session is null)
        {
            return;
        }

        (int page, Point local)? hit = HitPage(e.GetPosition(PageHost));
        if (hit is null)
        {
            return;
        }

        PdfTextLayout layout = await _session.GetTextLayoutAsync(hit.Value.page, priority: 50);
        (double x, double y) = ToPagePoints(hit.Value.page, hit.Value.local);
        int index = layout.HitTest(x, y, 2);
        if (index < 0)
        {
            return;
        }

        (int start, int count) = layout.WordAt(index);
        if (count <= 0)
        {
            return;
        }

        _selAnchor = (hit.Value.page, start);
        _selFocus = (hit.Value.page, start + count - 1);
        UpdateSelectionVisuals();
        e.Handled = true;
    }

    public void SelectAllOnCurrentPage()
    {
        if (_session is null)
        {
            return;
        }

        int page = CurrentPage;
        PdfTextLayout? layout = _session.TryGetCachedTextLayout(page);
        if (layout is null)
        {
            _ = _session.GetTextLayoutAsync(page, priority: 50).ContinueWith(_ => DispatcherQueue.TryEnqueue(SelectAllOnCurrentPage), TaskScheduler.Default);
            return;
        }

        if (layout.CharCount == 0)
        {
            return;
        }

        _selAnchor = (page, 0);
        _selFocus = (page, layout.CharCount - 1);
        UpdateSelectionVisuals();
    }

    private ((int Page, int Index) Start, (int Page, int Index) End)? OrderedSelection()
    {
        if (_selAnchor is not { } a || _selFocus is not { } f)
        {
            return null;
        }

        bool anchorFirst = a.Page < f.Page || (a.Page == f.Page && a.Index <= f.Index);
        return anchorFirst ? (a, f) : (f, a);
    }

    private void UpdateSelectionVisuals()
    {
        foreach (PdfPageView view in PageHost.Realized.Values)
        {
            ApplySelection(view);
        }
    }

    private void ApplySelection(PdfPageView view)
    {
        if (_session is null || OrderedSelection() is not { } sel || view.PageIndex < 0)
        {
            view.SetSelection([]);
            return;
        }

        int page = view.PageIndex;
        if (page < sel.Start.Page || page > sel.End.Page)
        {
            view.SetSelection([]);
            return;
        }

        PdfTextLayout? layout = _session.TryGetCachedTextLayout(page);
        if (layout is null)
        {
            _ = _session.GetTextLayoutAsync(page, priority: 60).ContinueWith(_ => DispatcherQueue.TryEnqueue(() => { if (view.PageIndex == page) { ApplySelection(view); } }), TaskScheduler.Default);
            view.SetSelection([]);
            return;
        }

        int start = page == sel.Start.Page ? sel.Start.Index : 0;
        int end = page == sel.End.Page ? sel.End.Index : layout.CharCount - 1;
        if (end < start)
        {
            view.SetSelection([]);
            return;
        }

        PdfPageInfo info = _session.Pages[page];
        var rects = new List<Rect>();
        foreach (PdfRect r in layout.GetRects(start, end - start + 1))
        {
            rects.Add(PdfPageView.PdfRectToDip(r, info, _rotation, _zoom));
        }

        view.SetSelection(rects);
    }

    public async void CopySelection()
    {
        string text = await GetSelectedTextAsync();
        if (text.Length == 0)
        {
            return;
        }

        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
        Clipboard.Flush();
    }

    public async Task<string> GetSelectedTextAsync()
    {
        if (_session is null || OrderedSelection() is not { } sel)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        for (int page = sel.Start.Page; page <= sel.End.Page; page++)
        {
            PdfTextLayout layout = await _session.GetTextLayoutAsync(page, priority: 40);
            int start = page == sel.Start.Page ? sel.Start.Index : 0;
            int end = page == sel.End.Page ? sel.End.Index : layout.CharCount - 1;
            if (end >= start)
            {
                sb.Append(layout.GetText(start, end - start + 1).Replace("\0", string.Empty));
                if (page != sel.End.Page)
                {
                    sb.AppendLine();
                }
            }
        }

        return sb.ToString();
    }

    /// <summary>Tiny helper so a debounce timer can be created lazily on the UI thread.</summary>
    private sealed class DispatcherQueueTimerHolder
    {
        private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _timer;

        public DispatcherQueueTimerHolder(Microsoft.UI.Dispatching.DispatcherQueue queue, TimeSpan interval, Action tick)
        {
            _timer = queue.CreateTimer();
            _timer.Interval = interval;
            _timer.IsRepeating = false;
            _timer.Tick += (_, _) => tick();
        }

        public void Restart()
        {
            _timer.Stop();
            _timer.Start();
        }
    }
}
