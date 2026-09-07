using Leaf.Pdfium;
using Leaf.Services;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;

namespace Leaf.Viewer;

/// <summary>
/// The side panel (thumbnails and bookmarks), its resize handle, and the hint that shows where a hovered
/// link points.
/// </summary>
public sealed partial class ViewerControl
{
    private const double MinPanelWidth = 130;
    private const double MaxPanelWidth = 420;
    private const double SplitterWidth = 6;

    private readonly InputCursor _resizeCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);

    private double _panelWidth = 200;
    private bool _draggingSplitter;
    private double _dragStartX;
    private double _dragStartWidth;

    /// <summary>Which panel is showing, or None when it is closed.</summary>
    public SidePanelKind PanelKind { get; private set; } = SidePanelKind.None;

    private void InitializePanel()
    {
        ViewPreferences preferences = SettingsStore.Current.View;
        _panelWidth = Math.Clamp(preferences.PanelWidth, MinPanelWidth, MaxPanelWidth);
        Panel.PageRequested += page =>
        {
            GoToPage(page);
            Focus(FocusState.Programmatic);
        };

        if (preferences.Panel != SidePanelKind.None)
        {
            Panel.Mode = preferences.Panel;
        }

        ApplyPanelLayout(preferences.Panel);
    }

    /// <summary>F4 and the toolbar button: closes the panel if it is open, otherwise opens the last one used.</summary>
    public void TogglePanel()
    {
        SidePanelKind next = PanelKind == SidePanelKind.None
            ? (SettingsStore.Current.View.Panel == SidePanelKind.Outline ? SidePanelKind.Outline : SidePanelKind.Thumbnails)
            : SidePanelKind.None;
        SetPanel(next);
    }

    public void SetPanel(SidePanelKind kind)
    {
        if (kind == PanelKind)
        {
            return;
        }

        if (kind != SidePanelKind.None)
        {
            Panel.Mode = kind;
            if (_session is DocumentSession session)
            {
                Panel.Load(session);
                Panel.SetCurrentPage(CurrentPage);
            }
        }

        ApplyPanelLayout(kind);

        ViewPreferences preferences = SettingsStore.Current.View;
        preferences.Panel = kind;
        preferences.PanelWidth = _panelWidth;
        SettingsStore.Save();

        // The document area just changed width, so a width-based fit has to be recomputed.
        if (_layout is not null && _fit != FitMode.Custom)
        {
            ApplyZoom(ComputeModeZoom(), keepTop: true);
        }
    }

    private void ApplyPanelLayout(SidePanelKind kind)
    {
        PanelKind = kind;
        bool open = kind != SidePanelKind.None;
        PanelColumn.Width = new GridLength(open ? _panelWidth : 0);
        SplitterColumn.Width = new GridLength(open ? SplitterWidth : 0);
        Panel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        PanelSplitter.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        PanelToggle.IsChecked = open;
    }

    private void OnTogglePanel(object sender, RoutedEventArgs e) => Guarded(TogglePanel);

    // ------------------------------------------------------------------ resizing

    private void OnSplitterPressed(object sender, PointerRoutedEventArgs e)
    {
        _draggingSplitter = true;
        _dragStartX = e.GetCurrentPoint(this).Position.X;
        _dragStartWidth = _panelWidth;
        PanelSplitter.CapturePointer(e.Pointer);
        ProtectedCursor = _resizeCursor;
        e.Handled = true;
    }

    private void OnSplitterMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_draggingSplitter)
        {
            ProtectedCursor = _resizeCursor;
            return;
        }

        double delta = e.GetCurrentPoint(this).Position.X - _dragStartX;
        _panelWidth = Math.Clamp(_dragStartWidth + delta, MinPanelWidth, MaxPanelWidth);
        PanelColumn.Width = new GridLength(_panelWidth);
        e.Handled = true;
    }

    private void OnSplitterReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_draggingSplitter)
        {
            return;
        }

        _draggingSplitter = false;
        PanelSplitter.ReleasePointerCapture(e.Pointer);
        ProtectedCursor = _arrow;
        SettingsStore.Current.View.PanelWidth = _panelWidth;
        SettingsStore.Save();

        if (_layout is not null && _fit != FitMode.Custom)
        {
            ApplyZoom(ComputeModeZoom(), keepTop: true);
        }

        e.Handled = true;
    }

    // ------------------------------------------------------------------ link hint

    private void ShowLinkTarget(PdfLink link)
    {
        string text = link.IsInternal ? $"Go to page {link.TargetPage + 1}" : link.Uri ?? string.Empty;
        if (LinkTargetText.Text != text)
        {
            LinkTargetText.Text = text;
        }

        LinkTarget.Visibility = Visibility.Visible;
    }

    private void HideLinkTarget()
    {
        if (LinkTarget.Visibility == Visibility.Visible)
        {
            LinkTarget.Visibility = Visibility.Collapsed;
        }
    }
}
