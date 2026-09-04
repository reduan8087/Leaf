using Leaf.Dialogs;
using Leaf.Pdfium;
using Leaf.Viewer;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;

namespace Leaf;

public sealed partial class MainWindow : Window
{
    private static readonly string IconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Leaf.ico");

    public MainWindow()
    {
        InitializeComponent();
        Title = "Leaf";
        SystemBackdrop = new MicaBackdrop();
        SetUpTitleBar();
        WireInput();
        Closed += (_, _) =>
        {
            foreach (TabViewItem item in Tabs.TabItems.OfType<TabViewItem>().ToList())
            {
                (item.Tag as DocumentTab)?.Viewer.Dispose();
            }
        };
    }

    private void SetUpTitleBar()
    {
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(DragRegion);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        if (File.Exists(IconPath))
        {
            AppWindow.SetIcon(IconPath);
            AppIcon.Source = new BitmapImage(new Uri(IconPath));
        }
    }

    private void WireInput()
    {
        Root.AllowDrop = true;
        Root.DragOver += (_, e) => e.AcceptedOperation = e.DataView.Contains(StandardDataFormats.StorageItems)
            ? DataPackageOperation.Link
            : DataPackageOperation.None;
        Root.Drop += OnDrop;

        AddAccelerator(VirtualKey.O, VirtualKeyModifiers.Control, (sender, e) => { _ = OpenWithPickerAsync(); e.Handled = true; });
        AddAccelerator(VirtualKey.W, VirtualKeyModifiers.Control, (_, e) => { CloseCurrentTab(); e.Handled = true; });
        AddAccelerator(VirtualKey.Tab, VirtualKeyModifiers.Control, (_, e) => { CycleTab(+1); e.Handled = true; });
        AddAccelerator(VirtualKey.Tab, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, (_, e) => { CycleTab(-1); e.Handled = true; });
    }

    private void AddAccelerator(VirtualKey key, VirtualKeyModifiers modifiers, TypedEventHandler<KeyboardAccelerator, KeyboardAcceleratorInvokedEventArgs> handler)
    {
        var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
        accelerator.Invoked += handler;
        Root.KeyboardAccelerators.Add(accelerator);
    }

    // ----- Public API used by App -----

    public void OpenFile(string path) => _ = OpenFileAsync(path);

    public async Task OpenFileAsync(string path)
    {
        foreach (TabViewItem item in Tabs.TabItems.OfType<TabViewItem>())
        {
            if (item.Tag is DocumentTab existing && string.Equals(existing.Path, path, StringComparison.OrdinalIgnoreCase))
            {
                Tabs.SelectedItem = item;
                return;
            }
        }

        var viewer = new ViewerControl();
        var docTab = new DocumentTab(path, viewer);
        var tab = new TabViewItem
        {
            Header = Path.GetFileName(path),
            Tag = docTab,
            IconSource = new SymbolIconSource { Symbol = Symbol.Document },
        };
        ToolTipService.SetToolTip(tab, path);
        Tabs.TabItems.Add(tab);
        Tabs.SelectedItem = tab;
        viewer.StateChanged += _ => UpdateTitle();

        string? password = null;
        while (true)
        {
            try
            {
                DocumentSession session = await DocumentSession.OpenAsync(path, password);
                if (!Tabs.TabItems.Contains(tab))
                {
                    session.Dispose(); // tab was closed while loading
                    return;
                }

                await viewer.LoadAsync(session);
                UpdateTitle();
                _ = TestAutomation.RunIfRequestedAsync(viewer);
                return;
            }
            catch (PdfException ex) when (ex.Error == PdfError.Password)
            {
                password = await PasswordDialog.ShowAsync(Content.XamlRoot, Path.GetFileName(path), previousAttemptFailed: password is not null);
                if (password is null)
                {
                    CloseTab(tab);
                    return;
                }
            }
            catch (PdfException ex)
            {
                CloseTab(tab);
                ShowError($"Can't open {Path.GetFileName(path)}", ex.Message);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                CloseTab(tab);
                ShowError($"Can't open {Path.GetFileName(path)}", ex.Message);
                return;
            }
        }
    }

    public void BringToFront()
    {
        AppWindow.Show(activateWindow: true);
        Activate();
    }

    public void ShowError(string title, string message)
    {
        Notice.Severity = InfoBarSeverity.Error;
        Notice.Title = title;
        Notice.Message = message;
        Notice.IsOpen = true;
    }

    // ----- Handlers -----

    private async void OnOpenClick(object sender, RoutedEventArgs e) => await OpenWithPickerAsync();

    private async void OnAddTabClick(TabView sender, object args) => await OpenWithPickerAsync();

    private async Task OpenWithPickerAsync()
    {
        var picker = new FileOpenPicker
        {
            ViewMode = PickerViewMode.List,
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
        };
        picker.FileTypeFilter.Add(".pdf");
        nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        IReadOnlyList<StorageFile> files = await picker.PickMultipleFilesAsync();
        foreach (StorageFile file in files)
        {
            OpenFile(file.Path);
        }
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return;
        }

        foreach (IStorageItem item in await e.DataView.GetStorageItemsAsync())
        {
            if (item is StorageFile file && file.FileType.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            {
                OpenFile(file.Path);
            }
        }
    }

    private void OnTabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args) => CloseTab(args.Tab);

    private void OnTabSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Tabs.SelectedItem is TabViewItem { Tag: DocumentTab doc })
        {
            TabContent.Content = doc.Viewer;
            WelcomePanel.Visibility = Visibility.Collapsed;
            doc.Viewer.Focus(FocusState.Programmatic);
        }
        else
        {
            TabContent.Content = null;
            WelcomePanel.Visibility = Visibility.Visible;
        }

        UpdateTitle();
    }

    private void UpdateTitle()
    {
        if (Tabs.SelectedItem is TabViewItem { Tag: DocumentTab doc })
        {
            int pages = doc.Viewer.PageCount;
            Title = pages > 0 ? $"{Path.GetFileName(doc.Path)} ({doc.Viewer.CurrentPage + 1}/{pages}) - Leaf" : $"{Path.GetFileName(doc.Path)} - Leaf";
        }
        else
        {
            Title = "Leaf";
        }
    }

    private void CloseCurrentTab()
    {
        if (Tabs.SelectedItem is TabViewItem tab)
        {
            CloseTab(tab);
        }
    }

    private void CloseTab(TabViewItem tab)
    {
        var doc = tab.Tag as DocumentTab;
        Tabs.TabItems.Remove(tab);
        if (doc is not null)
        {
            if (ReferenceEquals(TabContent.Content, doc.Viewer))
            {
                TabContent.Content = null;
            }

            doc.Viewer.Dispose();
        }
    }

    private void CycleTab(int delta)
    {
        int count = Tabs.TabItems.Count;
        if (count == 0)
        {
            return;
        }

        Tabs.SelectedIndex = (Tabs.SelectedIndex + delta + count) % count;
    }

    private sealed record DocumentTab(string Path, ViewerControl Viewer);
}
