using System.Globalization;
using Leaf.Viewer;

namespace Leaf;

/// <summary>
/// Scripted UI actions for automated verification (screenshots, perf runs). Only active when the environment variable
/// LEAF_TEST_ACTIONS is set, e.g. "wait:800;find:Business;zoomin;zoomin;rotate;pagedown;selectall".
/// </summary>
internal static class TestAutomation
{
    private static bool s_ran;

    public static async Task RunIfRequestedAsync(ViewerControl viewer)
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
                default:
                    continue;
            }

            await Task.Delay(400);
        }

        PerfLog.Stamp("actions-done");
    }
}
