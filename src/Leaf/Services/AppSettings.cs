using System.Text.Json.Serialization;
using Leaf.Viewer;

namespace Leaf.Services;

/// <summary>
/// Everything Leaf remembers between runs, as a plain DTO shaped for JSON. Kept deliberately separate from the
/// viewer's own types so a settings file written by an older build never blocks a rename in the UI code.
/// </summary>
public sealed class AppSettings
{
    public WindowPlacement? Window { get; set; }

    public ViewPreferences View { get; set; } = new();

    public List<RecentEntry> Recent { get; set; } = [];
}

/// <summary>Last window rectangle in screen pixels, so Leaf reopens where it was left.</summary>
public sealed class WindowPlacement
{
    public int X { get; set; }

    public int Y { get; set; }

    public int Width { get; set; }

    public int Height { get; set; }

    public bool Maximized { get; set; }
}

/// <summary>How the user last had the viewer set up; applied to every newly opened document.</summary>
public sealed class ViewPreferences
{
    public FitMode Fit { get; set; } = FitMode.FitWidth;

    public int Columns { get; set; } = 1;

    public bool Continuous { get; set; } = true;

    public bool CoverPageSeparate { get; set; } = true;

    public SidePanelKind Panel { get; set; } = SidePanelKind.None;

    public double PanelWidth { get; set; } = 200;

    public PageArrangement Arrangement() => new PageArrangement(Columns, Continuous, CoverPageSeparate).Normalized();

    public void SetArrangement(PageArrangement arrangement)
    {
        Columns = arrangement.Columns;
        Continuous = arrangement.Continuous;
        CoverPageSeparate = arrangement.CoverPageSeparate;
    }
}

/// <summary>One entry in the Open Recent list.</summary>
public sealed class RecentEntry
{
    public string Path { get; set; } = string.Empty;

    public DateTimeOffset LastOpened { get; set; }
}

/// <summary>
/// Source-generated serialization. Reflection-based System.Text.Json does not survive trimming under Native AOT,
/// so every persisted type must be listed here.
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class LeafJsonContext : JsonSerializerContext
{
}
