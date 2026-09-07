using Leaf.Dialogs;
using Leaf.Pdfium;
using Leaf.Services;
using Leaf.Viewer;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Leaf;

/// <summary>
/// Open documents: what each tab holds, saving, reverting, and making sure page edits are never lost silently.
/// </summary>
public sealed partial class MainWindow
{
    private bool _closeConfirmed;

    /// <summary>One tab's document. Mutable because Save As moves a tab onto a different file.</summary>
    private sealed class DocumentTab(string path, ViewerControl viewer, bool untitled, string displayName)
    {
        public string Path { get; set; } = path;

        public ViewerControl Viewer { get; } = viewer;

        /// <summary>True for a document that exists only in the scratch folder, such as a combine result.</summary>
        public bool IsUntitled { get; set; } = untitled;

        /// <summary>What the tab and the title bar call it.</summary>
        public string DisplayName { get; set; } = displayName;

        public TabViewItem? Tab { get; set; }

        public bool IsModified => Viewer.Session?.IsModified ?? false;
    }

    private DocumentTab? CurrentDocument => (Tabs.SelectedItem as TabViewItem)?.Tag as DocumentTab;

    private IEnumerable<DocumentTab> Documents =>
        Tabs.TabItems.OfType<TabViewItem>().Select(t => t.Tag).OfType<DocumentTab>();

    // ----- Dirty state -----

    /// <summary>Refreshes the modified marker on the tab, the title bar and the menu. Cheap; call it after any edit.</summary>
    public void RefreshDocumentState()
    {
        foreach (DocumentTab doc in Documents)
        {
            if (doc.Tab is not null)
            {
                doc.Tab.Header = doc.IsModified ? $"{doc.DisplayName} •" : doc.DisplayName;
            }
        }

        UpdateTitle();
    }

    // ----- Saving -----

    /// <summary>Ctrl+S. An untitled document has nowhere to go, so it becomes a Save As.</summary>
    private async Task<bool> SaveAsync(DocumentTab doc)
    {
        if (doc.IsUntitled)
        {
            return await SaveAsAsync(doc);
        }

        if (doc.Viewer.Session is not DocumentSession session || !doc.IsModified)
        {
            return true;
        }

        try
        {
            await SaveService.SaveInPlaceAsync(session.Document, doc.Path);
        }
        catch (Exception ex) when (ex is PdfException or IOException or UnauthorizedAccessException)
        {
            ShowError($"Can't save {doc.DisplayName}", ex.Message);
            return false;
        }

        // The open handle still refers to the file that was just replaced, so the document must be reopened.
        await doc.Viewer.ReloadAsync();
        RefreshDocumentState();
        return true;
    }

    /// <summary>Ctrl+Shift+S. Writes elsewhere and moves the tab onto the new file, as Acrobat does.</summary>
    private async Task<bool> SaveAsAsync(DocumentTab doc)
    {
        if (doc.Viewer.Session is not DocumentSession session)
        {
            return false;
        }

        string? target = await FileDialogs.PickSavePdfAsync(this, doc.DisplayName);
        if (target is null)
        {
            return false;
        }

        try
        {
            if (!doc.IsUntitled && string.Equals(target, doc.Path, StringComparison.OrdinalIgnoreCase))
            {
                // Saving over itself is still an in-place save; writing straight to it would corrupt the read.
                await SaveService.SaveInPlaceAsync(session.Document, doc.Path);
            }
            else
            {
                await SaveService.SaveCopyAsync(session.Document, target);
            }
        }
        catch (Exception ex) when (ex is PdfException or IOException or UnauthorizedAccessException)
        {
            ShowError($"Can't save {Path.GetFileName(target)}", ex.Message);
            return false;
        }

        string previous = doc.Path;
        bool wasUntitled = doc.IsUntitled;
        doc.Path = target;
        doc.IsUntitled = false;
        doc.DisplayName = Path.GetFileName(target);
        if (doc.Tab is not null)
        {
            ToolTipService.SetToolTip(doc.Tab, target);
        }

        await doc.Viewer.ReloadAsync();
        RecentFiles.Add(target);
        RefreshDocumentState();

        if (wasUntitled)
        {
            ScratchFiles.TryDelete(previous); // the scratch copy has served its purpose
        }

        return true;
    }

    /// <summary>Throws away page edits by reopening the file from disk.</summary>
    private async Task RevertAsync(DocumentTab doc)
    {
        if (doc.IsUntitled || !doc.IsModified)
        {
            return;
        }

        ContentDialogResult result = await new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = $"Revert {doc.DisplayName}?",
            Content = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Text = "The page changes you have made will be discarded and the file reloaded from disk.",
            },
            PrimaryButtonText = "Revert",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        }.ShowAsync();

        if (result == ContentDialogResult.Primary)
        {
            await doc.Viewer.ReloadAsync();
            RefreshDocumentState();
        }
    }

    // ----- Closing -----

    /// <summary>Asks about one document's unsaved changes. False means the user cancelled.</summary>
    private async Task<bool> ConfirmCloseAsync(DocumentTab doc)
    {
        if (!doc.IsModified)
        {
            return true;
        }

        Tabs.SelectedItem = doc.Tab; // show what is being asked about
        await WhenLoadedAsync();
        return await SaveChangesDialog.ShowAsync(Content.XamlRoot, doc.DisplayName, doc.IsUntitled) switch
        {
            SaveChoice.Save => await SaveAsync(doc),
            SaveChoice.Discard => true,
            _ => false,
        };
    }

    /// <summary>
    /// The window close button. AppWindow.Closing can be cancelled, unlike Window.Closed, so it is the only
    /// place a prompt can still stop the window from going away.
    /// </summary>
    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_closeConfirmed || !Documents.Any(d => d.IsModified))
        {
            return;
        }

        args.Cancel = true;
        _ = ConfirmThenCloseAsync();
    }

    private async Task ConfirmThenCloseAsync()
    {
        foreach (DocumentTab doc in Documents.ToList())
        {
            if (!await ConfirmCloseAsync(doc))
            {
                return; // cancelled: leave the window open
            }
        }

        _closeConfirmed = true;
        Close();
    }

    // ----- Menu handlers -----

    private async void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (CurrentDocument is DocumentTab doc)
        {
            await SaveAsync(doc);
        }
    }

    private async void OnSaveAsClick(object sender, RoutedEventArgs e)
    {
        if (CurrentDocument is DocumentTab doc)
        {
            await SaveAsAsync(doc);
        }
    }

    private async void OnRevertClick(object sender, RoutedEventArgs e)
    {
        if (CurrentDocument is DocumentTab doc)
        {
            await RevertAsync(doc);
        }
    }
}
