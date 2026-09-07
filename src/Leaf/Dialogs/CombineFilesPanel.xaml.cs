using System.Collections.ObjectModel;
using System.Globalization;
using Leaf.Pdfium;
using Leaf.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Storage;

namespace Leaf.Dialogs;

/// <summary>
/// The queue of files to merge: add, remove, reorder by dragging, sort, and choose how images are sized.
/// The panel owns the list and the reading of each file; <see cref="CombineFilesDialog"/> hosts it and does
/// the actual merge.
/// </summary>
public sealed partial class CombineFilesPanel : UserControl
{
    private const double A4WidthPt = 595;
    private const double A4HeightPt = 842;
    private const double LetterWidthPt = 612;
    private const double LetterHeightPt = 792;

    private readonly ObservableCollection<CombineItem> _items = [];
    private bool _sortAscending = true;
    private int _sortMode;

    public CombineFilesPanel()
    {
        InitializeComponent();
        FilesList.ItemsSource = _items;
        _items.CollectionChanged += (_, _) => Refresh();
    }

    /// <summary>Raised when the list changes, so the hosting dialog can enable or disable Combine.</summary>
    public event Action<bool>? CanCombineChanged;

    /// <summary>The window that owns the file picker.</summary>
    public Window? Owner { get; set; }

    public ImagePageSize ImagePageSize { get; private set; } = ImagePageSize.MatchImage;

    /// <summary>Files that could actually be read, in the order the user arranged them.</summary>
    public IReadOnlyList<CombineItem> UsableItems => [.. _items.Where(i => !i.Failed && i.PageCount > 0)];

    public (double Width, double Height) ImagePagePoints => ImagePageSize switch
    {
        ImagePageSize.A4 => (A4WidthPt, A4HeightPt),
        ImagePageSize.Letter => (LetterWidthPt, LetterHeightPt),
        _ => (0, 0), // zero means "size the page to the image"
    };

    // ------------------------------------------------------------------ adding

    private async void OnAddFiles(object sender, RoutedEventArgs e)
    {
        if (Owner is null)
        {
            return;
        }

        await AddAsync(await FileDialogs.PickCombineInputsAsync(Owner));
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = e.DataView.Contains(StandardDataFormats.StorageItems)
            ? DataPackageOperation.Copy
            : DataPackageOperation.None;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return;
        }

        var paths = new List<string>();
        foreach (IStorageItem item in await e.DataView.GetStorageItemsAsync())
        {
            if (item is StorageFile file && (FileDialogs.IsPdf(file.Path) || FileDialogs.IsImage(file.Path)))
            {
                paths.Add(file.Path);
            }
        }

        await AddAsync(paths);
    }

    private async Task AddAsync(IReadOnlyList<string> paths)
    {
        var added = new List<CombineItem>();
        foreach (string path in paths)
        {
            if (_items.Any(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase)))
            {
                continue; // the same file twice is almost always a mis-click
            }

            var item = new CombineItem(path);
            _items.Add(item);
            added.Add(item);
        }

        // Read each file for its page count or pixel size. Done after adding so the list appears immediately.
        foreach (CombineItem item in added)
        {
            await DescribeAsync(item);
        }

        Refresh();
    }

    /// <summary>Reads a file's page count or pixel size. Internal so tests can build items without the UI.</summary>
    internal static async Task DescribeAsync(CombineItem item)
    {
        try
        {
            if (item.IsImage)
            {
                (int width, int height, _) = await ReadImageInfoAsync(item.Path);
                item.Describe(1, width, height);
            }
            else
            {
                int pages = await PdfiumThread.Instance.RunAsync(
                    () =>
                    {
                        using PdfDocument document = PdfDocument.Open(item.Path);
                        return document.PageCount;
                    },
                    priority: 100);
                item.Describe(pages, 0, 0);
            }
        }
        catch (PdfException ex) when (ex.Error == PdfError.Password)
        {
            item.Fail("password protected");
        }
        catch (Exception ex) when (ex is PdfException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            item.Fail("can't be read");
        }
    }

    /// <summary>Pixel size and whether the file carries an EXIF rotation that a straight copy would ignore.</summary>
    private static async Task<(int Width, int Height, bool Rotated)> ReadImageInfoAsync(string path)
    {
        StorageFile file = await StorageFile.GetFileFromPathAsync(path);
        using Windows.Storage.Streams.IRandomAccessStream stream = await file.OpenReadAsync();
        BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream);
        bool rotated = decoder.OrientedPixelWidth != decoder.PixelWidth || decoder.OrientedPixelHeight != decoder.PixelHeight;
        return ((int)decoder.OrientedPixelWidth, (int)decoder.OrientedPixelHeight, rotated);
    }

    /// <summary>
    /// Decodes an image to opaque BGRA, honouring EXIF orientation and flattening any transparency onto white,
    /// because a PDF page is opaque.
    /// </summary>
    public static async Task<(byte[] Pixels, int Width, int Height)> DecodeImageAsync(string path)
    {
        StorageFile file = await StorageFile.GetFileFromPathAsync(path);
        using Windows.Storage.Streams.IRandomAccessStream stream = await file.OpenReadAsync();
        BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream);
        PixelDataProvider provider = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Straight,
            new BitmapTransform(),
            ExifOrientationMode.RespectExifOrientation,
            ColorManagementMode.ColorManageToSRgb);

        byte[] pixels = provider.DetachPixelData();
        for (int i = 0; i < pixels.Length; i += 4)
        {
            byte alpha = pixels[i + 3];
            if (alpha != 255)
            {
                // Composite over white so a transparent PNG does not print as black.
                pixels[i] = Blend(pixels[i], alpha);
                pixels[i + 1] = Blend(pixels[i + 1], alpha);
                pixels[i + 2] = Blend(pixels[i + 2], alpha);
                pixels[i + 3] = 255;
            }
        }

        return (pixels, (int)decoder.OrientedPixelWidth, (int)decoder.OrientedPixelHeight);
    }

    private static byte Blend(byte channel, byte alpha) => (byte)(((channel * alpha) + (255 * (255 - alpha))) / 255);

    /// <summary>True when a JPEG can be embedded byte-for-byte rather than re-encoded.</summary>
    public static async Task<bool> CanEmbedJpegDirectlyAsync(string path)
    {
        if (!FileDialogs.IsJpeg(path))
        {
            return false;
        }

        try
        {
            (_, _, bool rotated) = await ReadImageInfoAsync(path);
            return !rotated; // an EXIF-rotated photo has to go through the decoder to come out upright
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    // ------------------------------------------------------------------ list commands

    private void OnRemove(object sender, RoutedEventArgs e)
    {
        foreach (CombineItem item in FilesList.SelectedItems.OfType<CombineItem>().ToList())
        {
            _items.Remove(item);
        }
    }

    private void OnClear(object sender, RoutedEventArgs e) => _items.Clear();

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        RemoveButton.IsEnabled = FilesList.SelectedItems.Count > 0;

    private void OnSortChanged(object sender, SelectionChangedEventArgs e)
    {
        _sortMode = SortBox.SelectedIndex;
        ApplySort();
    }

    private void OnToggleSortDirection(object sender, RoutedEventArgs e)
    {
        _sortAscending = !_sortAscending;
        SortDirectionIcon.Glyph = _sortAscending ? "" : ""; // sort ascending / descending
        if (_sortMode == 0)
        {
            // Manual order: reversing is itself the sort.
            List<CombineItem> reversed = [.. _items.Reverse()];
            Replace(reversed);
        }
        else
        {
            ApplySort();
        }
    }

    private void ApplySort()
    {
        if (_sortMode == 0)
        {
            return; // manual: leave the user's arrangement alone
        }

        IEnumerable<CombineItem> sorted = _sortMode switch
        {
            1 => _items.OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase),
            2 => _items.OrderBy(i => i.Modified),
            3 => _items.OrderBy(i => i.Size),
            _ => _items.OrderBy(i => i.Path, StringComparer.CurrentCultureIgnoreCase),
        };

        Replace([.. _sortAscending ? sorted : sorted.Reverse()]);
    }

    private void Replace(List<CombineItem> ordered)
    {
        _items.Clear();
        foreach (CombineItem item in ordered)
        {
            _items.Add(item);
        }
    }

    private void OnImageSizeChanged(object sender, SelectionChangedEventArgs e) =>
        ImagePageSize = (ImagePageSize)Math.Max(0, ImageSizeBox.SelectedIndex);

    // ------------------------------------------------------------------ state

    private void Refresh()
    {
        for (int i = 0; i < _items.Count; i++)
        {
            _items[i].Position = (i + 1).ToString(CultureInfo.CurrentCulture);
        }

        EmptyState.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ClearButton.IsEnabled = _items.Count > 0;
        RemoveButton.IsEnabled = FilesList.SelectedItems.Count > 0;

        IReadOnlyList<CombineItem> usable = UsableItems;
        int pages = usable.Sum(i => i.PageCount);
        int failed = _items.Count(i => i.Failed);

        SummaryText.Text = _items.Count == 0
            ? "No files yet"
            : string.Create(
                CultureInfo.CurrentCulture,
                $"{usable.Count} file{(usable.Count == 1 ? string.Empty : "s")}, {pages} page{(pages == 1 ? string.Empty : "s")}") +
              (failed > 0 ? $"   ·   {failed} skipped" : string.Empty);

        CanCombineChanged?.Invoke(pages > 0);
    }
}
