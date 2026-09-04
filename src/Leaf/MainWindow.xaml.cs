using Leaf.Dialogs;
using Leaf.Pdfium;
using Leaf.Services;
using Leaf.Viewer;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Graphics;
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
        SetInitialSize();
        WireInput();
        Closed += (_, _) =>
        {
            foreach (TabViewItem item in Tabs.TabItems.OfType<TabViewItem>().ToList())
            {
                (item.Tag as DocumentTab)?.Viewer.Dispose();
            }
        };
        Root.Loaded += (_, _) => MaybeOfferDefaultApp();
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

    /// <summary>Open at ~80% of the work area, centred, so a document is readable at fit-width immediately.</summary>
    private void SetInitialSize()
    {
        try
        {
            DisplayArea area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
            RectInt32 work = area.WorkArea;
            int w = (int)(work.Width * 0.8);
            int h = (int)(work.Height * 0.88);
            AppWindow.MoveAndResize(new RectInt32(work.X + (work.Width - w) / 2, work.Y + (work.Height - h) / 2, w, h));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            // keep the default size
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
        viewer.Reopen = () => OpenSessionAsync(path, closeTabOnFailure: null);

        DocumentSession? session = await OpenSessionAsync(path, closeTabOnFailure: tab);
        if (session is null)
        {
            return;
        }

        if (!Tabs.TabItems.Contains(tab))
        {
            session.Dispose(); // tab was closed while loading
            return;
        }

        await viewer.LoadAsync(session);
        UpdateTitle();
        _ = TestAutomation.RunIfRequestedAsync(viewer);
    }

    /// <summary>Opens a session, prompting for a password as needed. Returns null when cancelled or failed (error shown).</summary>
    private async Task<DocumentSession?> OpenSessionAsync(string path, TabViewItem? closeTabOnFailure)
    {
        string? password = null;
        while (true)
        {
            try
            {
                return await DocumentSession.OpenAsync(path, password);
            }
            catch (PdfException ex) when (ex.Error == PdfError.Password)
            {
                password = await PasswordDialog.ShowAsync(Content.XamlRoot, Path.GetFileName(path), previousAttemptFailed: password is not null);
                if (password is null)
                {
                    if (closeTabOnFailure is not null)
                    {
                        CloseTab(closeTabOnFailure);
                    }

                    return null;
                }
            }
            catch (Exception ex) when (ex is PdfException or IOException or UnauthorizedAccessException)
            {
                if (closeTabOnFailure is not null)
                {
                    CloseTab(closeTabOnFailure);
                }

                ShowError($"Can't open {Path.GetFileName(path)}", ex.Message);
                return null;
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
        Notice.ActionButton = null;
        Notice.Severity = InfoBarSeverity.Error;
        Notice.Title = title;
        Notice.Message = message;
        Notice.IsOpen = true;
    }

    // ----- Default app prompt -----

    private void MaybeOfferDefaultApp()
    {
        if (!DefaultAppService.IsRegistered() || DefaultAppService.IsDefaultPdfHandler() || DefaultAppService.PromptDismissed)
        {
            return;
        }

        var button = new Button { Content = "Set as default" };
        button.Click += async (_, _) => { Notice.IsOpen = false; await DefaultAppService.OpenDefaultAppsSettingsAsync(); };
        Notice.ActionButton = button;
        Notice.Severity = InfoBarSeverity.Informational;
        Notice.Title = "Make Leaf your default PDF app?";
        Notice.Message = "Windows opens a Settings page where one click sets Leaf as the default for .pdf files.";
        Notice.CloseButtonClick += (_, _) => DefaultAppService.DismissPrompt();
        Notice.IsOpen = true;
    }

    private async void OnSetDefaultClick(object sender, RoutedEventArgs e)
    {
        if (!DefaultAppService.IsRegistered())
        {
            ShowError("Leaf is not installed", "Run the Leaf installer first; it registers Leaf as a PDF app so Windows can make it the default.");
            return;
        }

        await DefaultAppService.OpenDefaultAppsSettingsAsync();
    }

    private async void OnAboutClick(object sender, RoutedEventArgs e)
    {
        var text = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = $"Leaf {typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "0.1"}\n\nA light, fast PDF reader for Windows.\n\n" +
                   "Rendering: PDFium (Apache License 2.0)\nUI: Windows App SDK / WinUI 3\nRuntime: .NET (Native AOT)\n\n" +
                   "Shortcuts: Ctrl+O open, Ctrl+W close tab, Ctrl+F find, F3 next, Ctrl+G go to page, Ctrl+wheel / Ctrl+= / Ctrl+- zoom, Ctrl+0 fit width, Ctrl+Shift+R rotate, Ctrl+A select page, Ctrl+C copy.",
        };
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "About Leaf",
            Content = text,
            CloseButtonText = "Close",
        };
        await dialog.ShowAsync();
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
