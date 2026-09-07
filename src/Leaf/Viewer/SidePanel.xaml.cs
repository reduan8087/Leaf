using System.Collections.ObjectModel;
using Leaf.Pdfium;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Leaf.Viewer;

/// <summary>
/// The navigation panel: page thumbnails or the document's bookmarks. Thumbnails are rendered only for the
/// rows the list actually realizes, so opening a nine-hundred-page document does not queue nine hundred
/// renders.
/// </summary>
public sealed partial class SidePanel : UserControl
{
    private readonly ObservableCollection<ThumbnailItem> _thumbnails = [];
    private readonly ThumbnailCache _cache = new() { Capacity = 48 };
    private CancellationTokenSource _renderCts = new();

    private DocumentSession? _session;
    private SidePanelKind _mode = SidePanelKind.Thumbnails;
    private bool _outlineLoaded;
    private int _currentPage = -1;
    private bool _suppressSelection;

    public SidePanel()
    {
        InitializeComponent();
        ThumbnailsList.ItemsSource = _thumbnails;
        Unloaded += (_, _) => Cancel();
    }

    /// <summary>Raised when the user picks a page or a bookmark.</summary>
    public event Action<int>? PageRequested;

    public SidePanelKind Mode
    {
        get => _mode;
        set
        {
            if (value == SidePanelKind.None)
            {
                return;
            }

            _mode = value;
            ApplyMode();
        }
    }

    public void Load(DocumentSession session)
    {
        Cancel();
        _session = session;
        _outlineLoaded = false;
        _currentPage = -1;
        _thumbnails.Clear();
        _cache.Clear();
        OutlineTree.RootNodes.Clear();
        OutlineTree.ItemsSource = null;

        for (int i = 0; i < session.PageCount; i++)
        {
            PdfPageInfo info = session.Pages[i];
            _thumbnails.Add(new ThumbnailItem(i, info.HeightPt / Math.Max(1, info.WidthPt)));
        }

        ApplyMode();
    }

    /// <summary>Highlights the page being read and scrolls it into view.</summary>
    public void SetCurrentPage(int pageIndex)
    {
        if (pageIndex == _currentPage || (uint)pageIndex >= (uint)_thumbnails.Count)
        {
            return;
        }

        if ((uint)_currentPage < (uint)_thumbnails.Count)
        {
            _thumbnails[_currentPage].IsCurrent = false;
        }

        _currentPage = pageIndex;
        _thumbnails[pageIndex].IsCurrent = true;

        if (_mode == SidePanelKind.Thumbnails && Visibility == Visibility.Visible)
        {
            _suppressSelection = true;
            ThumbnailsList.SelectedIndex = pageIndex;
            ThumbnailsList.ScrollIntoView(_thumbnails[pageIndex]);
            _suppressSelection = false;
        }
    }

    private void ApplyMode()
    {
        bool thumbnails = _mode == SidePanelKind.Thumbnails;
        ThumbnailsTab.IsChecked = thumbnails;
        OutlineTab.IsChecked = !thumbnails;
        PanelTitle.Text = thumbnails ? "Thumbnails" : "Bookmarks";
        ThumbnailsList.Visibility = thumbnails ? Visibility.Visible : Visibility.Collapsed;

        if (thumbnails)
        {
            OutlineTree.Visibility = Visibility.Collapsed;
            EmptyOutlineHint.Visibility = Visibility.Collapsed;
            SetCurrentPage(_currentPage);
            return;
        }

        _ = LoadOutlineAsync();
    }

    private async Task LoadOutlineAsync()
    {
        if (_session is not DocumentSession session)
        {
            return;
        }

        if (_outlineLoaded)
        {
            ShowOutline(OutlineTree.ItemsSource is IList<OutlineItem> { Count: > 0 });
            return;
        }

        _outlineLoaded = true;
        PdfDocument document = session.Document;
        IReadOnlyList<PdfOutlineNode> nodes;
        try
        {
            nodes = await PdfiumThread.Instance.RunAsync(document.GetOutline, priority: 400);
        }
        catch (Exception ex) when (ex is PdfException or ObjectDisposedException)
        {
            nodes = [];
        }

        List<OutlineItem> items = [.. nodes.Select(node => new OutlineItem(node))];
        OutlineTree.ItemsSource = items;
        ShowOutline(items.Count > 0);
    }

    private void ShowOutline(bool hasEntries)
    {
        OutlineTree.Visibility = hasEntries ? Visibility.Visible : Visibility.Collapsed;
        EmptyOutlineHint.Visibility = hasEntries ? Visibility.Collapsed : Visibility.Visible;
    }

    // ------------------------------------------------------------------ thumbnails

    /// <summary>
    /// Renders a thumbnail as its row is realized. This is the whole reason the panel scales: the list only
    /// ever asks for what it is about to show.
    /// </summary>
    private void OnThumbnailContainerChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue || args.Item is not ThumbnailItem item || item.Thumbnail is not null)
        {
            return;
        }

        _ = RenderAsync(item);
    }

    private async Task RenderAsync(ThumbnailItem item)
    {
        if (_session is not DocumentSession session)
        {
            return;
        }

        CancellationToken token = _renderCts.Token;
        int width = (int)Math.Round(item.TileWidth * (XamlRoot?.RasterizationScale ?? 1.0));
        WriteableBitmap? bitmap = await _cache.GetAsync(session.Document, item.PageIndex, 0, width, token);
        if (bitmap is not null && !token.IsCancellationRequested)
        {
            item.Thumbnail = bitmap;
        }
    }

    private void OnThumbnailClick(object sender, ItemClickEventArgs e)
    {
        if (!_suppressSelection && e.ClickedItem is ThumbnailItem item)
        {
            PageRequested?.Invoke(item.PageIndex);
        }
    }

    private void OnOutlineInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        if (args.InvokedItem is OutlineItem { PageIndex: >= 0 } item)
        {
            PageRequested?.Invoke(item.PageIndex);
        }
    }

    private void OnShowThumbnails(object sender, RoutedEventArgs e) => Mode = SidePanelKind.Thumbnails;

    private void OnShowOutline(object sender, RoutedEventArgs e) => Mode = SidePanelKind.Outline;

    /// <summary>Drops rendered thumbnails after an edit changed what a page index means.</summary>
    public void Refresh()
    {
        if (_session is DocumentSession session)
        {
            Load(session);
        }
    }

    private void Cancel()
    {
        _renderCts.Cancel();
        _renderCts = new CancellationTokenSource();
    }
}
