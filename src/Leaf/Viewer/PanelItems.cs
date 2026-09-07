using System.ComponentModel;
using System.Runtime.CompilerServices;
using Leaf.Pdfium;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Leaf.Viewer;

/// <summary>One page in the thumbnails panel.</summary>
public sealed partial class ThumbnailItem(int pageIndex, double aspect) : INotifyPropertyChanged
{
    private ImageSource? _thumbnail;
    private bool _isCurrent;

    public int PageIndex { get; } = pageIndex;

    public string Label { get; } = (pageIndex + 1).ToString(System.Globalization.CultureInfo.CurrentCulture);

    /// <summary>Height over width, so the slot is the right shape before the render arrives.</summary>
    public double Aspect { get; } = aspect <= 0 ? 1.294 : aspect;

    public double TileWidth => 128;

    public double TileHeight => Math.Round(TileWidth * Aspect);

    public event PropertyChangedEventHandler? PropertyChanged;

    public ImageSource? Thumbnail
    {
        get => _thumbnail;
        set => Set(ref _thumbnail, value);
    }

    /// <summary>True for the page being read, which the panel highlights.</summary>
    public bool IsCurrent
    {
        get => _isCurrent;
        set
        {
            if (Set(ref _isCurrent, value))
            {
                Notify(nameof(CurrentVisibility));
            }
        }
    }

    /// <summary>Shows the accent outline over the page being read.</summary>
    public Visibility CurrentVisibility => IsCurrent ? Visibility.Visible : Visibility.Collapsed;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        Notify(name);
        return true;
    }

    private void Notify(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>One entry in the outline panel, shaped for TreeView's data-bound mode.</summary>
public sealed partial class OutlineItem
{
    public OutlineItem(PdfOutlineNode node)
    {
        Title = string.IsNullOrWhiteSpace(node.Title) ? "(untitled)" : node.Title;
        PageIndex = node.PageIndex;
        Children = [.. node.Children.Select(child => new OutlineItem(child))];
    }

    public string Title { get; }

    /// <summary>Destination page, or -1 when the entry does not resolve to one.</summary>
    public int PageIndex { get; }

    /// <summary>Must be an IList: TreeViewItem.ItemsSource goes through WinRT, which cannot see IReadOnlyList.</summary>
    public IList<OutlineItem> Children { get; }

    public bool HasChildren => Children.Count > 0;
}
