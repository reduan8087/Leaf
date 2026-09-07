using Leaf.Dialogs;
using Leaf.Organize;
using Leaf.Pdfium;
using Leaf.Services;
using Leaf.Viewer;
using Microsoft.UI.Xaml;

namespace Leaf;

/// <summary>
/// The two page-editing entry points: Organize Pages, which takes over the content area, and Combine Files,
/// which produces a new unsaved document.
/// </summary>
public sealed partial class MainWindow
{
    private OrganizePagesView? _organize;

    private bool IsOrganizing => _organize is not null;

    // ----- Organize Pages -----

    private async void OnOrganizeClick(object sender, RoutedEventArgs e)
    {
        if (IsOrganizing || CurrentDocument is not DocumentTab doc || doc.Viewer.Session is not DocumentSession session)
        {
            return;
        }

        var view = new OrganizePagesView();
        view.Completed += changed => _ = CloseOrganizeAsync(doc, changed);

        _organize = view;
        OrganizeHost.Content = view;
        OrganizeHost.Visibility = Visibility.Visible;
        TabContent.Visibility = Visibility.Collapsed;
        Tabs.IsEnabled = false; // switching documents underneath a staged edit would be incoherent

        try
        {
            await view.LoadAsync(session, this);
        }
        catch (Exception ex) when (ex is PdfException or IOException or UnauthorizedAccessException)
        {
            ShowError("Can't organize this document", ex.Message);
            await CloseOrganizeAsync(doc, changed: false);
        }
    }

    private Task CloseOrganizeAsync(DocumentTab doc, bool changed)
    {
        _organize = null;
        OrganizeHost.Content = null;
        OrganizeHost.Visibility = Visibility.Collapsed;
        TabContent.Visibility = Visibility.Visible;
        Tabs.IsEnabled = true;

        if (changed)
        {
            // The edits live in memory, so the viewer refreshes around the open document rather than reopening it.
            doc.Viewer.RefreshAfterEdit();
            RefreshDocumentState();
        }

        doc.Viewer.Focus(FocusState.Programmatic);
        return Task.CompletedTask;
    }

    // ----- Combine Files -----

    private async void OnCombineClick(object sender, RoutedEventArgs e)
    {
        if (IsOrganizing)
        {
            return;
        }

        await WhenLoadedAsync();
        string? combined = await CombineFilesDialog.ShowAsync(this);
        if (combined is null)
        {
            return;
        }

        await OpenCombinedAsync(combined);
    }

    /// <summary>
    /// Opens a freshly combined document as an unsaved tab. It lives in the scratch folder until the user
    /// saves it somewhere, so Ctrl+S becomes Save As and closing asks before throwing it away.
    /// </summary>
    private async Task OpenCombinedAsync(string scratchPath)
    {
        var viewer = new ViewerControl();
        var docTab = new DocumentTab(scratchPath, viewer, untitled: true, displayName: "Combined");
        var tab = new Microsoft.UI.Xaml.Controls.TabViewItem
        {
            Header = docTab.DisplayName,
            Tag = docTab,
            IconSource = new Microsoft.UI.Xaml.Controls.SymbolIconSource { Symbol = Microsoft.UI.Xaml.Controls.Symbol.Document },
        };
        docTab.Tab = tab;
        Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(tab, "Not saved yet");
        Tabs.TabItems.Add(tab);
        Tabs.SelectedItem = tab;
        viewer.StateChanged += _ => UpdateTitle();
        viewer.FullScreenRequested += ToggleFullScreen;
        viewer.Reopen = () => OpenSessionAsync(docTab.Path, closeTabOnFailure: null);

        DocumentSession? session = await OpenSessionAsync(scratchPath, closeTabOnFailure: tab);
        if (session is null)
        {
            ScratchFiles.TryDelete(scratchPath);
            return;
        }

        if (!Tabs.TabItems.Contains(tab))
        {
            session.Dispose();
            ScratchFiles.TryDelete(scratchPath);
            return;
        }

        await viewer.LoadAsync(session);
        viewer.SetToolbarVisible(!_fullScreen || _chromeRevealed);

        // Mark it as needing a home: the tab shows the dot and closing it asks first.
        await PdfiumThread.Instance.RunAsync(() => session.Document.MarkModified(), priority: 100);
        RefreshDocumentState();
    }
}
