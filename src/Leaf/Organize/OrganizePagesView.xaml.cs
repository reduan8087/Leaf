using System.Collections.ObjectModel;
using Leaf.Pdfium;
using Leaf.Services;
using Leaf.Viewer;
using Microsoft.UI.Xaml;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.System;

namespace Leaf.Organize;

/// <summary>
/// Full-window page organizer: reorder by dragging, rotate, delete, insert pages from another PDF and extract
/// a selection to a new one.
/// </summary>
/// <remarks>
/// Every edit is staged in <see cref="PageEditItem"/>s and only reaches the document when Done is pressed, so
/// Cancel genuinely discards. Pages inserted from another file keep pointing at that file until then, which is
/// why the inserted documents are held open for the lifetime of this view.
/// </remarks>
public sealed partial class OrganizePagesView : UserControl
{
    /// <summary>Beyond this, rendering every thumbnail up front costs more than it is worth.</summary>
    private const int EagerThumbnailLimit = 400;

    private readonly ObservableCollection<PageEditItem> _items = [];
    private readonly List<PageSource> _sources = [];
    private readonly Stack<Snapshot> _undo = new();
    private readonly Stack<Snapshot> _redo = new();
    private readonly ThumbnailCache _thumbnails = new() { Capacity = 96 };
    private CancellationTokenSource _renderCts = new();

    private DocumentSession? _session;
    private MainWindow? _owner;
    private double _tileWidth = 150;
    private bool _applying;

    public OrganizePagesView()
    {
        InitializeComponent();
        PagesGrid.ItemsSource = _items;
        _items.CollectionChanged += OnCollectionChanged;
        KeyDown += OnKeyDown;
        Unloaded += (_, _) => Cleanup();
    }

    /// <summary>Raised when the view is finished with. True when the document was actually changed.</summary>
    public event Action<bool>? Completed;

    public async Task LoadAsync(DocumentSession session, MainWindow owner)
    {
        _session = session;
        _owner = owner;

        var primary = new PageSource(session.Document, isPrimary: true, Path.GetFileName(session.Path));
        _sources.Add(primary);

        Busy.IsActive = true;
        PdfDocument document = session.Document;
        int[] rotations = await PdfiumThread.Instance.RunAsync(
            () =>
            {
                var values = new int[document.PageCount];
                for (int i = 0; i < values.Length; i++)
                {
                    values[i] = document.GetPageRotation(i);
                }

                return values;
            },
            priority: 100);

        for (int i = 0; i < rotations.Length; i++)
        {
            PdfPageInfo info = session.Pages[i];
            _items.Add(new PageEditItem(primary, i, rotations[i], info.HeightPt / Math.Max(1, info.WidthPt)));
        }

        Busy.IsActive = false;
        ApplyTileSize();
        Renumber();
        UpdateCommands();
        _ = RefreshThumbnailsAsync();
        PagesGrid.Focus(FocusState.Programmatic);
    }

    // ------------------------------------------------------------------ thumbnails

    /// <summary>
    /// Renders the thumbnails. Long documents are rendered lazily as the grid is scrolled, so opening a
    /// 900-page file does not queue 900 renders.
    /// </summary>
    private async Task RefreshThumbnailsAsync()
    {
        CancellationToken token = _renderCts.Token;
        int width = (int)Math.Round(_tileWidth * (XamlRoot?.RasterizationScale ?? 1.0));

        List<PageEditItem> pending = [.. _items.Take(EagerThumbnailLimit).Where(i => i.Thumbnail is null)];
        foreach (PageEditItem item in pending)
        {
            if (token.IsCancellationRequested)
            {
                return;
            }

            WriteableBitmap? bitmap = await _thumbnails.GetAsync(item.Source.Document, item.SourceIndex, item.ViewRotation, width, token);
            if (bitmap is not null && !token.IsCancellationRequested)
            {
                item.Thumbnail = bitmap;
            }
        }
    }

    private void ApplyTileSize()
    {
        foreach (PageEditItem item in _items)
        {
            item.TileWidth = _tileWidth;
            item.TileHeight = Math.Round(_tileWidth * item.ShownAspect);
        }
    }

    private void OnSizeChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_items.Count == 0 || Math.Abs(e.NewValue - _tileWidth) < 1)
        {
            return;
        }

        _tileWidth = e.NewValue;
        ApplyTileSize();
    }

    // ------------------------------------------------------------------ editing

    private IReadOnlyList<PageEditItem> Selection => [.. PagesGrid.SelectedItems.OfType<PageEditItem>()];

    private void PushUndo()
    {
        _undo.Push(Snapshot.Capture(_items));
        _redo.Clear();
        UpdateCommands();
    }

    private void Restore(Snapshot snapshot)
    {
        _items.CollectionChanged -= OnCollectionChanged;
        snapshot.ApplyTo(_items);
        _items.CollectionChanged += OnCollectionChanged;
        Renumber();
        ApplyTileSize();
        UpdateCommands();
        _ = RefreshThumbnailsAsync();
    }

    private void OnCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => OnItemsChanged();

    private void OnItemsChanged()
    {
        Renumber();
        UpdateCommands();
    }

    private void Renumber()
    {
        for (int i = 0; i < _items.Count; i++)
        {
            _items[i].Label = (i + 1).ToString(System.Globalization.CultureInfo.CurrentCulture);
        }
    }

    private void Rotate(int quarterTurns)
    {
        IReadOnlyList<PageEditItem> selection = Selection;
        if (selection.Count == 0)
        {
            return;
        }

        PushUndo();
        foreach (PageEditItem item in selection)
        {
            item.Rotation += quarterTurns;
            item.Thumbnail = null; // re-rendered at the new angle; the cache keeps the old one for undo
        }

        ApplyTileSize();
        UpdateCommands();
        _ = RefreshThumbnailsAsync();
    }

    private void OnRotateLeft(object sender, RoutedEventArgs e) => Rotate(-1);

    private void OnRotateRight(object sender, RoutedEventArgs e) => Rotate(+1);

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        IReadOnlyList<PageEditItem> selection = Selection;
        if (selection.Count == 0 || selection.Count == _items.Count)
        {
            if (selection.Count > 0)
            {
                Status("A document must keep at least one page.");
            }

            return;
        }

        PushUndo();
        foreach (PageEditItem item in selection)
        {
            _items.Remove(item);
        }
    }

    private void OnMoveToStart(object sender, RoutedEventArgs e) => Move(toStart: true);

    private void OnMoveToEnd(object sender, RoutedEventArgs e) => Move(toStart: false);

    /// <summary>Moves the selection while keeping its own relative order, which dragging 200 pages cannot do.</summary>
    private void Move(bool toStart)
    {
        List<PageEditItem> selection = [.. _items.Where(i => PagesGrid.SelectedItems.Contains(i))];
        if (selection.Count == 0 || selection.Count == _items.Count)
        {
            return;
        }

        PushUndo();
        foreach (PageEditItem item in selection)
        {
            _items.Remove(item);
        }

        int at = toStart ? 0 : _items.Count;
        foreach (PageEditItem item in selection)
        {
            _items.Insert(at++, item);
        }

        PagesGrid.SelectedItems.Clear();
        foreach (PageEditItem item in selection)
        {
            PagesGrid.SelectedItems.Add(item);
        }
    }

    private void OnUndo(object sender, RoutedEventArgs e)
    {
        if (_undo.Count == 0)
        {
            return;
        }

        _redo.Push(Snapshot.Capture(_items));
        Restore(_undo.Pop());
    }

    private void OnRedo(object sender, RoutedEventArgs e)
    {
        if (_redo.Count == 0)
        {
            return;
        }

        _undo.Push(Snapshot.Capture(_items));
        Restore(_redo.Pop());
    }

    // ------------------------------------------------------------------ insert and extract

    private async void OnInsert(object sender, RoutedEventArgs e)
    {
        if (_owner is null)
        {
            return;
        }

        IReadOnlyList<string> paths = await FileDialogs.PickPdfsAsync(_owner);
        if (paths.Count == 0)
        {
            return;
        }

        // Insert after the selection, or at the end when nothing is selected.
        int at = _items.Count;
        if (Selection.Count > 0)
        {
            at = _items.IndexOf(Selection[^1]) + 1;
        }

        PushUndo();
        Busy.IsActive = true;
        try
        {
            foreach (string path in paths)
            {
                at = await InsertOneAsync(path, at);
            }
        }
        catch (Exception ex) when (ex is PdfException or IOException or UnauthorizedAccessException)
        {
            Status($"Could not insert those pages. {ex.Message}");
        }
        finally
        {
            Busy.IsActive = false;
        }

        ApplyTileSize();
        Renumber();
        UpdateCommands();
        _ = RefreshThumbnailsAsync();
    }

    private async Task<int> InsertOneAsync(string path, int at)
    {
        PdfDocument document = await PdfiumThread.Instance.RunAsync(() => PdfDocument.Open(path), priority: 100);
        var source = new PageSource(document, isPrimary: false, Path.GetFileName(path));
        _sources.Add(source);

        int[] rotations = await PdfiumThread.Instance.RunAsync(
            () =>
            {
                var values = new int[document.PageCount];
                for (int i = 0; i < values.Length; i++)
                {
                    values[i] = document.GetPageRotation(i);
                }

                return values;
            },
            priority: 100);

        for (int i = 0; i < rotations.Length; i++)
        {
            PdfPageInfo info = document.Pages[i];
            _items.Insert(at++, new PageEditItem(source, i, rotations[i], info.HeightPt / Math.Max(1, info.WidthPt)));
        }

        return at;
    }

    private async void OnExtract(object sender, RoutedEventArgs e)
    {
        IReadOnlyList<PageEditItem> selection = [.. _items.Where(i => PagesGrid.SelectedItems.Contains(i))];
        if (selection.Count == 0 || _owner is null || _session is null)
        {
            return;
        }

        string suggested = $"{Path.GetFileNameWithoutExtension(_session.Path)} pages";
        string? target = await FileDialogs.PickSavePdfAsync(_owner, suggested);
        if (target is null)
        {
            return;
        }

        Busy.IsActive = true;
        try
        {
            // The staged rotations come along, so what is extracted matches what the grid shows.
            List<(PageSource Source, int Index, int Rotation)> pages =
                [.. selection.Select(i => (i.Source, i.SourceIndex, i.Rotation))];

            await PdfiumThread.Instance.RunAsync(
                () =>
                {
                    using var builder = new PdfBuilder();
                    foreach ((PageSource source, int index, int _) in pages)
                    {
                        builder.AppendPages(source.Document, [index]);
                    }

                    for (int i = 0; i < pages.Count; i++)
                    {
                        builder.SetPageRotation(i, pages[i].Rotation);
                    }

                    builder.Save(target);
                },
                priority: -50);

            Status($"Saved {selection.Count} page{(selection.Count == 1 ? string.Empty : "s")} to {Path.GetFileName(target)}.");
        }
        catch (Exception ex) when (ex is PdfException or IOException or UnauthorizedAccessException)
        {
            _owner.ShowError("Can't extract those pages", ex.Message);
        }
        finally
        {
            Busy.IsActive = false;
        }
    }

    // ------------------------------------------------------------------ apply

    private void OnCancel(object sender, RoutedEventArgs e) => Finish(changed: false);

    private async void OnDone(object sender, RoutedEventArgs e)
    {
        if (_session is null || _applying)
        {
            return;
        }

        if (_items.Count == 0)
        {
            Status("A document must keep at least one page.");
            return;
        }

        if (!HasChanges())
        {
            Finish(changed: false);
            return;
        }

        _applying = true;
        Busy.IsActive = true;
        DoneButton.IsEnabled = false;
        try
        {
            await ApplyAsync(_session);
            _session.InvalidateAfterEdit();
            Finish(changed: true);
        }
        catch (Exception ex) when (ex is PdfException or IOException or UnauthorizedAccessException)
        {
            _owner?.ShowError("Can't apply those changes", ex.Message);
            Finish(changed: true); // the document may be partly edited; let the viewer reload what is there
        }
        finally
        {
            _applying = false;
            Busy.IsActive = false;
            DoneButton.IsEnabled = true;
        }
    }

    private bool HasChanges()
    {
        if (_session is null)
        {
            return false;
        }

        if (_items.Count != _session.PageCount)
        {
            return true;
        }

        for (int i = 0; i < _items.Count; i++)
        {
            PageEditItem item = _items[i];
            if (item.IsInserted || item.SourceIndex != i || item.IsRotated)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Turns the staged grid into engine calls, in the one order that keeps indices meaningful: import first so
    /// every page lives in the target document, then rotate (while original indices still hold), then delete
    /// from the back, and finally reorder what is left.
    /// </summary>
    private Task ApplyAsync(DocumentSession session)
    {
        PdfDocument target = session.Document;
        List<PageEditItem> items = [.. _items];

        return PdfiumThread.Instance.RunAsync(
            () =>
            {
                // 1. Copy in pages from inserted documents, appending them to the end for now.
                var resolved = new int[items.Count];
                foreach (PageSource source in _sources.Where(s => !s.IsPrimary))
                {
                    List<int> wanted = [];
                    List<int> slots = [];
                    for (int i = 0; i < items.Count; i++)
                    {
                        if (ReferenceEquals(items[i].Source, source))
                        {
                            slots.Add(i);
                            wanted.Add(items[i].SourceIndex);
                        }
                    }

                    if (wanted.Count == 0)
                    {
                        continue;
                    }

                    int appendAt = target.PageCount;
                    target.ImportPages(source.Document, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(wanted), appendAt);
                    for (int k = 0; k < slots.Count; k++)
                    {
                        resolved[slots[k]] = appendAt + k;
                    }
                }

                for (int i = 0; i < items.Count; i++)
                {
                    if (items[i].Source.IsPrimary)
                    {
                        resolved[i] = items[i].SourceIndex;
                    }
                }

                // 2. Rotations, while every index above still refers to the page it was captured from.
                for (int i = 0; i < items.Count; i++)
                {
                    if (items[i].Rotation != target.GetPageRotation(resolved[i]))
                    {
                        target.SetPageRotation(resolved[i], items[i].Rotation);
                    }
                }

                // 3 and 4. Drop what the grid no longer lists, then put the rest in the order it shows.
                (int[] removed, int[] order) = PdfPageOrder.Plan(target.PageCount, resolved);
                if (removed.Length > 0)
                {
                    target.DeletePages(removed);
                }

                target.ReorderPages(order);
            },
            priority: -50);
    }

    private void Finish(bool changed)
    {
        Cleanup();
        Completed?.Invoke(changed);
    }

    private void Cleanup()
    {
        _renderCts.Cancel();
        _renderCts = new CancellationTokenSource();
        _thumbnails.Clear();
        foreach (PageSource source in _sources.Where(s => !s.IsPrimary))
        {
            source.Dispose();
        }

        _sources.RemoveAll(s => !s.IsPrimary);
    }

    // ------------------------------------------------------------------ chrome

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateCommands();

    private void UpdateCommands()
    {
        int selected = PagesGrid.SelectedItems.Count;
        bool any = selected > 0;
        RotateLeftButton.IsEnabled = any;
        RotateRightButton.IsEnabled = any;
        DeleteButton.IsEnabled = any && selected < _items.Count;
        MoveStartButton.IsEnabled = any && selected < _items.Count;
        MoveEndButton.IsEnabled = any && selected < _items.Count;
        ExtractButton.IsEnabled = any;
        UndoButton.IsEnabled = _undo.Count > 0;
        RedoButton.IsEnabled = _redo.Count > 0;
        EmptyHint.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        int rotated = _items.Count(i => i.IsRotated);
        int inserted = _items.Count(i => i.IsInserted);
        List<string> parts = [$"{_items.Count} page{(_items.Count == 1 ? string.Empty : "s")}"];
        if (selected > 0)
        {
            parts.Add($"{selected} selected");
        }

        if (rotated > 0)
        {
            parts.Add($"{rotated} rotated");
        }

        if (inserted > 0)
        {
            parts.Add($"{inserted} inserted");
        }

        StatusText.Text = string.Join("   ·   ", parts);
    }

    private void Status(string message) => StatusText.Text = message;

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        bool control = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

        switch (e.Key)
        {
            case VirtualKey.Delete when !control:
                OnDelete(this, new RoutedEventArgs());
                break;
            case VirtualKey.Z when control:
                OnUndo(this, new RoutedEventArgs());
                break;
            case VirtualKey.Y when control:
                OnRedo(this, new RoutedEventArgs());
                break;
            case VirtualKey.Escape:
                Finish(changed: false);
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    // ------------------------------------------------------------------ test hooks

    /// <summary>Selects pages by zero-based index. Used only by the LEAF_TEST_ACTIONS script.</summary>
    internal void TestSelect(IReadOnlyList<int> pageIndices)
    {
        PagesGrid.SelectedItems.Clear();
        foreach (int index in pageIndices)
        {
            if ((uint)index < (uint)_items.Count)
            {
                PagesGrid.SelectedItems.Add(_items[index]);
            }
        }

        UpdateCommands();
    }

    internal void TestRotate(int quarterTurns) => Rotate(quarterTurns);

    internal void TestDelete() => OnDelete(this, new RoutedEventArgs());

    internal void TestMove(bool toStart) => Move(toStart);

    internal void TestCancel() => Finish(changed: false);

    internal async Task TestDoneAsync()
    {
        if (_session is null || !HasChanges())
        {
            Finish(changed: false);
            return;
        }

        await ApplyAsync(_session);
        _session.InvalidateAfterEdit();
        Finish(changed: true);
    }

    /// <summary>Order and rotations at a point in time. Deleted pages stay alive here so undo can bring them back.</summary>
    private sealed class Snapshot(PageEditItem[] items, int[] rotations)
    {
        public static Snapshot Capture(IList<PageEditItem> items) =>
            new([.. items], [.. items.Select(i => i.Rotation)]);

        public void ApplyTo(ObservableCollection<PageEditItem> target)
        {
            target.Clear();
            for (int i = 0; i < items.Length; i++)
            {
                if (items[i].Rotation != rotations[i])
                {
                    items[i].Rotation = rotations[i];
                    items[i].Thumbnail = null;
                }

                target.Add(items[i]);
            }
        }
    }
}
