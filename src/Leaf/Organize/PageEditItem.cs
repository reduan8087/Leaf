using System.ComponentModel;
using System.Runtime.CompilerServices;
using Leaf.Pdfium;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Leaf.Organize;

/// <summary>
/// A document that pages in the Organize grid can come from: either the one being edited, or another PDF the
/// user inserted. Inserted documents stay open for as long as the grid does, because their pages are only
/// copied across when the edit is applied.
/// </summary>
public sealed class PageSource(PdfDocument document, bool isPrimary, string name) : IDisposable
{
    public PdfDocument Document { get; } = document;

    public bool IsPrimary { get; } = isPrimary;

    public string Name { get; } = name;

    public void Dispose()
    {
        if (!IsPrimary)
        {
            PdfDocument document = Document;
            _ = PdfiumThread.Instance.RunAsync(document.Dispose, priority: 50);
        }
    }
}

/// <summary>
/// One page in the Organize grid. Edits are staged here and only reach the document when the user presses Done,
/// so Cancel really does throw everything away.
/// </summary>
public sealed partial class PageEditItem : INotifyPropertyChanged
{
    private int _rotation;
    private ImageSource? _thumbnail;
    private string _label = string.Empty;
    private double _tileWidth = 160;
    private double _tileHeight = 210;

    public PageEditItem(PageSource source, int sourceIndex, int originalRotation, double aspect)
    {
        Source = source;
        SourceIndex = sourceIndex;
        OriginalRotation = originalRotation;
        _rotation = originalRotation;
        Aspect = aspect <= 0 ? 1.294 : aspect; // A4 as a sane fallback for a page with no usable size
    }

    public PageSource Source { get; }

    /// <summary>Index of this page inside <see cref="Source"/>, which never changes while the grid is open.</summary>
    public int SourceIndex { get; }

    public int OriginalRotation { get; }

    /// <summary>Height divided by width, used to size the thumbnail slot before the render arrives.</summary>
    public double Aspect { get; }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Absolute rotation in quarter-turns clockwise, as it will be written to the file.</summary>
    public int Rotation
    {
        get => _rotation;
        set
        {
            if (Set(ref _rotation, value & 3))
            {
                Notify(nameof(IsRotated));
                Notify(nameof(ViewRotation));
            }
        }
    }

    public bool IsRotated => Rotation != OriginalRotation;

    /// <summary>
    /// Rotation to render the thumbnail at, relative to how the page is stored. Rendering at the new angle
    /// rather than spinning the existing bitmap keeps the aspect ratio honest for quarter turns.
    /// </summary>
    public int ViewRotation => (Rotation - OriginalRotation + 4) & 3;

    /// <summary>Height over width as currently shown, with the aspect flipped for a quarter turn.</summary>
    public double ShownAspect => (ViewRotation & 1) == 1 ? 1.0 / Aspect : Aspect;

    public ImageSource? Thumbnail
    {
        get => _thumbnail;
        set => Set(ref _thumbnail, value);
    }

    /// <summary>The page's position in the grid, one-based.</summary>
    public string Label
    {
        get => _label;
        set => Set(ref _label, value);
    }

    /// <summary>Where the page came from, shown on pages that were inserted from another file.</summary>
    public string? Origin => Source.IsPrimary ? null : Source.Name;

    public bool IsInserted => !Source.IsPrimary;

    /// <summary>x:Bind has no implicit bool-to-Visibility conversion, so the item exposes it directly.</summary>
    public Visibility OriginVisibility => IsInserted ? Visibility.Visible : Visibility.Collapsed;

    public double TileWidth
    {
        get => _tileWidth;
        set => Set(ref _tileWidth, value);
    }

    public double TileHeight
    {
        get => _tileHeight;
        set => Set(ref _tileHeight, value);
    }

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
