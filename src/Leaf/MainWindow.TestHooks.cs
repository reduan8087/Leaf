using Leaf.Dialogs;
using Leaf.Organize;
using Leaf.Pdfium;
using Leaf.Services;
using Leaf.Viewer;
using Microsoft.UI.Xaml;

namespace Leaf;

/// <summary>
/// Entry points used only by <see cref="TestAutomation"/> (the LEAF_TEST_ACTIONS script). They exist because
/// the page-editing surfaces cannot otherwise be exercised without synthesising keyboard and mouse input,
/// which is unreliable enough to be worthless as a check.
/// </summary>
public sealed partial class MainWindow
{
    internal OrganizePagesView? TestOrganize => _organize;

    internal Task TestOpenOrganizeAsync()
    {
        OnOrganizeClick(this, new RoutedEventArgs());
        return Task.CompletedTask;
    }

    /// <summary>
    /// Runs a combine over the given files with default options, skipping only the dialog itself. It goes
    /// through the same merge code the dialog uses, so images and their decoding are covered too.
    /// </summary>
    internal async Task TestCombineAsync(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
        {
            return;
        }

        try
        {
            var items = new List<CombineItem>(paths.Count);
            foreach (string path in paths)
            {
                var item = new CombineItem(path);
                await CombineFilesPanel.DescribeAsync(item);
                if (!item.Failed)
                {
                    items.Add(item);
                }
            }

            if (items.Count == 0)
            {
                ShowError("Test combine failed", "None of those files could be read.");
                return;
            }

            await OpenCombinedAsync(await CombineFilesDialog.CombineAsync(items, (0, 0)));
        }
        catch (Exception ex) when (ex is PdfException or IOException or UnauthorizedAccessException)
        {
            ShowError("Test combine failed", ex.Message);
        }
    }

    /// <summary>Saves the current document to an explicit path, skipping the picker.</summary>
    internal async Task TestSaveAsAsync(string path)
    {
        if (CurrentDocument is not DocumentTab doc || doc.Viewer.Session is not DocumentSession session || string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            await SaveService.SaveCopyAsync(session.Document, path);
            doc.Path = path;
            doc.IsUntitled = false;
            doc.DisplayName = Path.GetFileName(path);
            await doc.Viewer.ReloadAsync();
            RefreshDocumentState();
        }
        catch (Exception ex) when (ex is PdfException or IOException or UnauthorizedAccessException)
        {
            ShowError("Test save failed", ex.Message);
        }
    }
}
