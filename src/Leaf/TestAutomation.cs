using System.Globalization;
using Leaf.Viewer;

namespace Leaf;

/// <summary>
/// Scripted UI actions for automated verification (screenshots, perf runs). Only active when the environment variable
/// LEAF_TEST_ACTIONS is set, e.g. "wait:800;find:Business;columns:2;scrolling:off;pagedown;selectall".
///
/// Actions prefixed "organize." and "combine" drive the page-editing surfaces, which is the only way to test
/// them without synthesising input: keyboard shortcuts sent from outside do not reliably reach the window.
/// </summary>
internal static class TestAutomation
{
    private static bool s_ran;

    /// <summary>Parses "1,3,5" into zero-based page indices.</summary>
    private static int[] ParsePages(string value) =>
        [.. value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                 .Select(part => int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out int n) ? n - 1 : -1)
                 .Where(n => n >= 0)];

    public static async Task RunIfRequestedAsync(ViewerControl viewer, MainWindow? window = null)
    {
        string? script = Environment.GetEnvironmentVariable("LEAF_TEST_ACTIONS");
        if (string.IsNullOrWhiteSpace(script) || s_ran)
        {
            return;
        }

        s_ran = true;
        await Task.Delay(700); // let the first tiles land
        foreach (string raw in script.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string action = raw;
            string arg = string.Empty;
            int colon = raw.IndexOf(':');
            if (colon > 0)
            {
                action = raw[..colon];
                arg = raw[(colon + 1)..];
            }

            switch (action.ToLowerInvariant())
            {
                case "wait":
                    await Task.Delay(int.TryParse(arg, out int ms) ? ms : 500);
                    continue;
                case "find":
                    viewer.Find(arg);
                    break;
                case "next":
                    viewer.FindStep(+1);
                    break;
                case "prev":
                    viewer.FindStep(-1);
                    break;
                case "zoomin":
                    viewer.ZoomIn();
                    break;
                case "zoomout":
                    viewer.ZoomOut();
                    break;
                case "zoom":
                    if (double.TryParse(arg, NumberStyles.Float, CultureInfo.InvariantCulture, out double z))
                    {
                        viewer.SetZoom(z);
                    }

                    break;
                case "fitpage":
                    viewer.FitPage();
                    break;
                case "fitwidth":
                    viewer.FitWidth();
                    break;
                case "fitheight":
                    viewer.FitHeight();
                    break;
                case "actualsize":
                    viewer.ShowActualSize();
                    break;
                case "columns":
                    if (int.TryParse(arg, NumberStyles.None, CultureInfo.InvariantCulture, out int columns))
                    {
                        viewer.SetColumns(columns);
                    }

                    break;
                case "cover":
                    viewer.SetCoverPageSeparate(!string.Equals(arg, "off", StringComparison.OrdinalIgnoreCase));
                    break;
                case "scrolling":
                    viewer.SetContinuous(!string.Equals(arg, "off", StringComparison.OrdinalIgnoreCase));
                    break;
                case "rotate":
                    viewer.Rotate(+1);
                    break;
                case "rotateccw":
                    viewer.Rotate(-1);
                    break;
                case "pagedown":
                    viewer.ScrollBy(0, viewer.ViewportHeight - 40, animate: false);
                    break;
                case "pageup":
                    viewer.ScrollBy(0, -(viewer.ViewportHeight - 40), animate: false);
                    break;
                case "page":
                    if (int.TryParse(arg, out int page))
                    {
                        viewer.GoToPage(page - 1);
                    }

                    break;
                case "end":
                    viewer.GoToPage(int.MaxValue);
                    break;
                case "home":
                    viewer.GoToPage(0);
                    break;
                case "selectall":
                    viewer.SelectAllOnCurrentPage();
                    break;
                case "copy":
                    viewer.CopySelection();
                    break;
                case "scrollthrough":
                    // scroll the whole document a viewport at a time (perf scenario)
                    for (int i = 0; i < 400 && viewer.CanScrollDown; i++)
                    {
                        viewer.ScrollBy(0, viewer.ViewportHeight, animate: false);
                        await Task.Delay(40);
                    }

                    break;
                case "organize":
                    if (window is not null)
                    {
                        await window.TestOpenOrganizeAsync();
                    }

                    break;
                case "organize.select":
                    window?.TestOrganize?.TestSelect(ParsePages(arg));
                    break;
                case "organize.rotate":
                    window?.TestOrganize?.TestRotate(arg == "left" ? -1 : +1);
                    break;
                case "organize.delete":
                    window?.TestOrganize?.TestDelete();
                    break;
                case "organize.movestart":
                    window?.TestOrganize?.TestMove(toStart: true);
                    break;
                case "organize.moveend":
                    window?.TestOrganize?.TestMove(toStart: false);
                    break;
                case "organize.done":
                    if (window?.TestOrganize is { } done)
                    {
                        await done.TestDoneAsync();
                    }

                    break;
                case "organize.cancel":
                    window?.TestOrganize?.TestCancel();
                    break;
                case "combine":
                    if (window is not null)
                    {
                        await window.TestCombineAsync(arg.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                    }

                    break;
                case "saveas":
                    if (window is not null)
                    {
                        await window.TestSaveAsAsync(arg);
                    }

                    break;
                case "touchfile":
                    // simulate an external save so the file watcher offers a reload
                    if (viewer.Session is { } s)
                    {
                        File.SetLastWriteTimeUtc(s.Path, DateTime.UtcNow);
                    }

                    break;
                default:
                    continue;
            }

            await Task.Delay(500);
            PerfLog.Stamp($"after-{action} offset={viewer.VerticalOffset:F0}/{viewer.ScrollableHeight:F0} vh={viewer.ViewportHeight:F0} page={viewer.CurrentPage + 1} zoom={viewer.Zoom:F2}");
        }

        PerfLog.Stamp("actions-done");
    }
}
