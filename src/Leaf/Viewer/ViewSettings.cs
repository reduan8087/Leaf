namespace Leaf.Viewer;

/// <summary>How the zoom level is chosen. Custom means the user set a percentage and it should not follow the viewport.</summary>
public enum FitMode
{
    FitWidth,
    FitHeight,
    FitPage,
    ActualSize,
    Custom,
}

/// <summary>Which side panel is showing, if any.</summary>
public enum SidePanelKind
{
    None,
    Thumbnails,
    Outline,
}

/// <summary>
/// How pages are arranged, as two independent axes rather than one list of named modes: a column count and
/// whether scrolling is continuous. Together they cover Acrobat's page-display modes:
/// 1 + not continuous = Single Page View, 1 + continuous = Enable Scrolling, 2 + continuous = Two Page Scrolling,
/// and so on.
/// </summary>
/// <param name="Columns">Pages side by side, 1..<see cref="MaxColumns"/>.</param>
/// <param name="Continuous">True to scroll through the whole document, false to show one row at a time.</param>
/// <param name="CoverPageSeparate">In multi-column layouts, put page 1 alone so spreads pair up like a printed book.</param>
public readonly record struct PageArrangement(int Columns, bool Continuous, bool CoverPageSeparate)
{
    public const int MaxColumns = 6;

    public static PageArrangement Default => new(1, true, true);

    /// <summary>Clamps the column count and drops the cover-page offset where it has no meaning.</summary>
    public PageArrangement Normalized() => new(Math.Clamp(Columns, 1, MaxColumns), Continuous, CoverPageSeparate);

    /// <summary>True when a cover page should sit alone on the first row.</summary>
    public bool HasCover => CoverPageSeparate && Columns > 1;
}
