using Leaf.Pdfium;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Leaf.Viewer;

/// <summary>
/// Clickable links. Internal destinations jump within the document; http and https links open in the browser
/// behind a confirmation, because a PDF can say one thing and link somewhere else entirely.
/// </summary>
public sealed partial class ViewerControl
{
    /// <summary>How far the pointer may travel between press and release and still count as a click.</summary>
    private const double ClickSlopDip = 4;

    private readonly Dictionary<int, Task<List<PdfLink>>> _links = [];
    private readonly InputCursor _hand = InputSystemCursor.Create(InputSystemCursorShape.Hand);

    private PdfLink? _pressedLink;
    private Point _pressedAt;

    /// <summary>Links for a page, fetched once. Runs behind text so it never delays selection.</summary>
    private Task<List<PdfLink>> GetLinksAsync(int pageIndex)
    {
        if (_links.TryGetValue(pageIndex, out Task<List<PdfLink>>? existing))
        {
            return existing;
        }

        if (_session is not DocumentSession session)
        {
            return Task.FromResult(new List<PdfLink>());
        }

        PdfDocument document = session.Document;
        Task<List<PdfLink>> task = PdfiumThread.Instance.RunAsync(() => document.GetLinks(pageIndex), priority: 400);
        _links[pageIndex] = task;
        return task;
    }

    /// <summary>The link under a point, if its page's links have already been read. Never blocks the pointer.</summary>
    private PdfLink? LinkAt(int pageIndex, Point localDip)
    {
        if (!_links.TryGetValue(pageIndex, out Task<List<PdfLink>>? task))
        {
            _ = GetLinksAsync(pageIndex); // warm it for the next move
            return null;
        }

        if (!task.IsCompletedSuccessfully || task.Result.Count == 0)
        {
            return null;
        }

        (double x, double y) = ToPagePoints(pageIndex, localDip);
        foreach (PdfLink link in task.Result)
        {
            if (link.Bounds.Contains(x, y))
            {
                return link;
            }
        }

        return null;
    }

    private void ClearLinkCache() => _links.Clear();

    /// <summary>Follows a link. External addresses are shown to the user before anything is opened.</summary>
    private async Task ActivateLinkAsync(PdfLink link)
    {
        if (link.IsInternal)
        {
            GoToPage(link.TargetPage);
            return;
        }

        if (string.IsNullOrEmpty(link.Uri) || !Uri.TryCreate(link.Uri, UriKind.Absolute, out Uri? uri))
        {
            return;
        }

        // Only ordinary web addresses, and only after the user has seen the real one.
        if (!uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase)
            && !uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
        {
            ShowNotice(InfoBarSeverity.Warning, "Link not opened", $"Leaf only opens web links. This one points to \"{link.Uri}\".");
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Open this link?",
            Content = new TextBlock
            {
                Text = uri.ToString(),
                TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
            },
            PrimaryButtonText = "Open",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await Windows.System.Launcher.LaunchUriAsync(uri);
        }
    }
}
