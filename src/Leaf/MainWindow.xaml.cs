using Leaf.Dialogs;
using Leaf.Pdfium;
using Leaf.Services;
using Leaf.Viewer;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Storage;
using Windows.System;

namespace Leaf;

public sealed partial class MainWindow : Window
{
    private static readonly string IconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Leaf.ico");

    /// <summary>How close to the top edge the pointer must come to reveal the chrome in full screen.</summary>
    private const double RevealEdgeDip = 3;
    private const double RevealKeepDip = 88;

    private readonly TaskCompletionSource _loaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _fullScreen;
    private bool _chromeRevealed;

    public MainWindow()
    {
        InitializeComponent();
        Title = "Leaf";
        SystemBackdrop = new MicaBackdrop();
        SetUpTitleBar();
        RestoreWindowPlacement();
        WireInput();
        Closed += (_, _) =>
        {
            RememberWindowPlacement();
            SettingsStore.Flush();
            foreach (TabViewItem item in Tabs.TabItems.OfType<TabViewItem>().ToList())
            {
                (item.Tag as DocumentTab)?.Viewer.Dispose();
            }
        };
        Root.Loaded += (_, _) =>
        {
            _loaded.TrySetResult();
            MaybeOfferDefaultApp();
        };
    }

    /// <summary>ContentDialogs need a loaded visual tree; awaiting this makes early activations (file double-click) safe.</summary>
    private Task WhenLoadedAsync() => Root.IsLoaded ? Task.CompletedTask : _loaded.Task;

    private ViewerControl? CurrentViewer => (Tabs.SelectedItem as TabViewItem)?.Tag is DocumentTab doc ? doc.Viewer : null;

    private void SetUpTitleBar()
    {
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(DragRegion);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        if (File.Exists(IconPath))
        {
            AppWindow.SetIcon(IconPath);
        }
    }

    // ----- Window placement -----

    /// <summary>
    /// Restores the last window rectangle, falling back to ~80% of the work area. The saved rectangle is
    /// checked against the current monitor layout first, so unplugging a second screen cannot strand the
    /// window off-screen.
    /// </summary>
    private void RestoreWindowPlacement()
    {
        try
        {
            WindowPlacement? saved = SettingsStore.Current.Window;
            if (saved is { Width: > 200, Height: > 200 } && IsOnScreen(saved))
            {
                AppWindow.MoveAndResize(new RectInt32(saved.X, saved.Y, saved.Width, saved.Height));
                if (saved.Maximized && AppWindow.Presenter is OverlappedPresenter presenter)
                {
                    presenter.Maximize();
                }

                return;
            }

            DisplayArea area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
            RectInt32 work = area.WorkArea;
            int w = (int)(work.Width * 0.8);
            int h = (int)(work.Height * 0.88);
            AppWindow.MoveAndResize(new RectInt32(work.X + ((work.Width - w) / 2), work.Y + ((work.Height - h) / 2), w, h));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            // keep the default size
        }
    }

    /// <summary>True when a decent part of the saved rectangle still lands on a connected display.</summary>
    private static bool IsOnScreen(WindowPlacement placement)
    {
        var rect = new RectInt32(placement.X, placement.Y, placement.Width, placement.Height);
        DisplayArea area = DisplayArea.GetFromRect(rect, DisplayAreaFallback.Nearest);
        RectInt32 work = area.WorkArea;
        int overlapX = Math.Min(rect.X + rect.Width, work.X + work.Width) - Math.Max(rect.X, work.X);
        int overlapY = Math.Min(rect.Y + rect.Height, work.Y + work.Height) - Math.Max(rect.Y, work.Y);
        return overlapX > 200 && overlapY > 100;
    }

    private void RememberWindowPlacement()
    {
        try
        {
            if (_fullScreen)
            {
                return; // full screen is a mode, not a size worth restoring into
            }

            bool maximized = AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized };
            if (maximized)
            {
                // Keep the previous restore rectangle: the maximized bounds are not useful to reopen into.
                WindowPlacement existing = SettingsStore.Current.Window ?? new WindowPlacement();
                existing.Maximized = true;
                SettingsStore.Current.Window = existing;
            }
            else
            {
                SettingsStore.Current.Window = new WindowPlacement
                {
                    X = AppWindow.Position.X,
                    Y = AppWindow.Position.Y,
                    Width = AppWindow.Size.Width,
                    Height = AppWindow.Size.Height,
                    Maximized = false,
                };
            }

            SettingsStore.Save();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            // not worth failing a close over
        }
    }

    // ----- Input -----

    private void WireInput()
    {
        Root.AllowDrop = true;
        Root.DragOver += (_, e) => e.AcceptedOperation = e.DataView.Contains(StandardDataFormats.StorageItems)
            ? DataPackageOperation.Link
            : DataPackageOperation.None;
        Root.Drop += OnDrop;
        Root.PointerMoved += OnRootPointerMoved;

        AddAccelerator(VirtualKey.O, VirtualKeyModifiers.Control, (sender, e) => { _ = OpenWithPickerAsync(); e.Handled = true; });
        AddAccelerator(VirtualKey.W, VirtualKeyModifiers.Control, (_, e) => { CloseCurrentTab(); e.Handled = true; });
        AddAccelerator(VirtualKey.Tab, VirtualKeyModifiers.Control, (_, e) => { CycleTab(+1); e.Handled = true; });
        AddAccelerator(VirtualKey.Tab, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, (_, e) => { CycleTab(-1); e.Handled = true; });

        // F11 is the Windows-wide convention; Ctrl+L matches Acrobat. Ctrl+F stays Find.
        AddAccelerator(VirtualKey.F11, VirtualKeyModifiers.None, (_, e) => { ToggleFullScreen(); e.Handled = true; });
        AddAccelerator(VirtualKey.L, VirtualKeyModifiers.Control, (_, e) => { ToggleFullScreen(); e.Handled = true; });

        // Only claims Escape when full screen is actually on, so the viewer keeps it for the find bar and selection.
        AddAccelerator(VirtualKey.Escape, VirtualKeyModifiers.None, (_, e) =>
        {
            if (_fullScreen)
            {
                SetFullScreen(false);
                e.Handled = true;
            }
        });
    }

    private void AddAccelerator(VirtualKey key, VirtualKeyModifiers modifiers, TypedEventHandler<KeyboardAccelerator, KeyboardAcceleratorInvokedEventArgs> handler)
    {
        var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
        accelerator.Invoked += handler;
        Root.KeyboardAccelerators.Add(accelerator);
    }

    // ----- Full screen -----

    public bool IsFullScreen => _fullScreen;

    public void ToggleFullScreen() => SetFullScreen(!_fullScreen);

    private void SetFullScreen(bool on)
    {
        if (on == _fullScreen)
        {
            return;
        }

        try
        {
            AppWindow.SetPresenter(on ? AppWindowPresenterKind.FullScreen : AppWindowPresenterKind.Default);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NotSupportedException)
        {
            ShowError("Full screen is unavailable", ex.Message);
            return;
        }

        _fullScreen = on;
        _chromeRevealed = false;
        ApplyChrome();

        FullScreenHint.IsOpen = on;
        FullScreenMenuItem.Text = on ? "Exit full screen" : "Full screen";
        CurrentViewer?.Focus(FocusState.Programmatic);
    }

    /// <summary>Hides the tab strip and the viewer toolbar in full screen, unless the pointer has revealed them.</summary>
    private void ApplyChrome()
    {
        bool visible = !_fullScreen || _chromeRevealed;
        TitleBarArea.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        foreach (TabViewItem item in Tabs.TabItems.OfType<TabViewItem>())
        {
            (item.Tag as DocumentTab)?.Viewer.SetToolbarVisible(visible);
        }
    }

    private void OnRootPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_fullScreen)
        {
            return;
        }

        // A wider band keeps the chrome up once shown, so it does not flicker while the pointer travels to a button.
        double y = e.GetCurrentPoint(Root).Position.Y;
        bool reveal = y <= (_chromeRevealed ? RevealKeepDip : RevealEdgeDip);
        if (reveal != _chromeRevealed)
        {
            _chromeRevealed = reveal;
            ApplyChrome();
        }
    }

    // ----- Public API used by App -----

    public async void OpenFile(string path)
    {
        try
        {
            await OpenFileAsync(path);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Leaf] OpenFile failed: {ex}");
            ShowError($"Can't open {Path.GetFileName(path)}", ex.Message);
        }
    }

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
        viewer.FullScreenRequested += ToggleFullScreen;
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
        viewer.SetToolbarVisible(!_fullScreen || _chromeRevealed);
        RecentFiles.Add(path);
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
                await WhenLoadedAsync();
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
        await WhenLoadedAsync();
        var text = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = $"Leaf {typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "0.2"}\n\nA light, fast PDF reader for Windows.\n\n" +
                   "Rendering: PDFium (Apache License 2.0)\nUI: Windows App SDK / WinUI 3\nRuntime: .NET (Native AOT)\n\n" +
                   "Files: Ctrl+O open, Ctrl+W close tab.\n" +
                   "Reading: Ctrl+F find, F3 next, Ctrl+G go to page, Ctrl+wheel zoom, Ctrl+0 fit width, Ctrl+A select page, Ctrl+C copy.\n" +
                   "View: F11 or Ctrl+L full screen, Esc to leave, Ctrl+Shift+R / Ctrl+Shift+L rotate.",
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

    // ----- App menu -----

    /// <summary>
    /// Builds the volatile parts of the menu as it opens. Doing it here rather than at startup keeps the
    /// settings file and the disk checks off the launch path.
    /// </summary>
    private void OnAppMenuOpening(object? sender, object e)
    {
        ViewerControl? viewer = CurrentViewer;
        bool hasDocument = viewer?.Session is not null;
        PropertiesMenuItem.IsEnabled = hasDocument;
        ViewMenu.IsEnabled = hasDocument;
        FullScreenMenuItem.Text = _fullScreen ? "Exit full screen" : "Full screen";
        SyncViewMenu(viewer);
        BuildRecentMenu();
    }

    /// <summary>Mirrors the viewer's current modes into the View submenu as it opens.</summary>
    private void SyncViewMenu(ViewerControl? viewer)
    {
        if (viewer is null)
        {
            return;
        }

        FitMode fit = viewer.Fit;
        FitWidthMenuItem.IsChecked = fit == FitMode.FitWidth;
        FitHeightMenuItem.IsChecked = fit == FitMode.FitHeight;
        FitPageMenuItem.IsChecked = fit == FitMode.FitPage;
        ActualSizeMenuItem.IsChecked = fit == FitMode.ActualSize;

        PageArrangement arrangement = viewer.Arrangement;
        OneUpMenuItem.IsChecked = arrangement.Columns == 1;
        TwoUpMenuItem.IsChecked = arrangement.Columns == 2;
        GridMenuItem.IsChecked = arrangement.Columns >= 3;
        CoverMenuItem.IsChecked = arrangement.CoverPageSeparate;
        CoverMenuItem.IsEnabled = arrangement.Columns > 1;
        ContinuousMenuItem.IsChecked = arrangement.Continuous;
    }

    private void OnMenuFitWidth(object sender, RoutedEventArgs e) => CurrentViewer?.FitWidth();

    private void OnMenuFitHeight(object sender, RoutedEventArgs e) => CurrentViewer?.FitHeight();

    private void OnMenuFitPage(object sender, RoutedEventArgs e) => CurrentViewer?.FitPage();

    private void OnMenuActualSize(object sender, RoutedEventArgs e) => CurrentViewer?.ShowActualSize();

    private void OnMenuOneUp(object sender, RoutedEventArgs e) => CurrentViewer?.SetColumns(1);

    private void OnMenuTwoUp(object sender, RoutedEventArgs e) => CurrentViewer?.SetColumns(2);

    private void OnMenuGrid(object sender, RoutedEventArgs e) => CurrentViewer?.SetColumns(4);

    private void OnMenuCover(object sender, RoutedEventArgs e)
    {
        if (CurrentViewer is ViewerControl viewer)
        {
            viewer.SetCoverPageSeparate(!viewer.Arrangement.CoverPageSeparate);
        }
    }

    private void OnMenuContinuous(object sender, RoutedEventArgs e) => CurrentViewer?.ToggleContinuous();

    private void BuildRecentMenu()
    {
        RecentMenu.Items.Clear();
        IReadOnlyList<RecentEntry> entries = RecentFiles.Entries;
        if (entries.Count == 0)
        {
            RecentMenu.Items.Add(new MenuFlyoutItem { Text = "No recent files", IsEnabled = false });
            return;
        }

        foreach (RecentEntry entry in entries)
        {
            bool exists = RecentFiles.Exists(entry);
            string path = entry.Path;
            var item = new MenuFlyoutItem
            {
                Text = Path.GetFileName(path),
                Opacity = exists ? 1 : 0.5, // a moved file is worth still seeing
            };
            ToolTipService.SetToolTip(item, exists ? path : $"{path}\n\nThis file is no longer there.");
            item.Click += (_, _) =>
            {
                if (RecentFiles.Exists(entry))
                {
                    OpenFile(path);
                }
                else
                {
                    RecentFiles.Remove(path);
                    ShowError("That file has moved", $"{path} is no longer there, so it was removed from the recent list.");
                }
            };
            RecentMenu.Items.Add(item);
        }

        RecentMenu.Items.Add(new MenuFlyoutSeparator());
        var clear = new MenuFlyoutItem { Text = "Clear recent files" };
        clear.Click += (_, _) => RecentFiles.Clear();
        RecentMenu.Items.Add(clear);
    }

    private void OnPrintClick(object sender, RoutedEventArgs e)
    {
        // Reachable only if the item is ever enabled; printing arrives in 0.3.0.
        ShowError("Printing is not ready yet", "Printing arrives in Leaf 0.3.0.");
    }

    private async void OnPropertiesClick(object sender, RoutedEventArgs e)
    {
        ViewerControl? viewer = CurrentViewer;
        if (viewer?.Session is not DocumentSession session)
        {
            return;
        }

        await WhenLoadedAsync();
        try
        {
            await DocumentPropertiesDialog.ShowAsync(Content.XamlRoot, session, viewer.CurrentPage);
        }
        catch (PdfException ex)
        {
            ShowError("Can't read the document properties", ex.Message);
        }
    }

    private void OnFullScreenClick(object sender, RoutedEventArgs e) => ToggleFullScreen();

    // ----- Handlers -----

    private async void OnOpenClick(object sender, RoutedEventArgs e) => await OpenWithPickerAsync();

    private async void OnAddTabClick(TabView sender, object args) => await OpenWithPickerAsync();

    private async Task OpenWithPickerAsync()
    {
        foreach (string path in await FileDialogs.PickPdfsAsync(this))
        {
            OpenFile(path);
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
            if (item is StorageFile file && FileDialogs.IsPdf(file.Path))
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
            string name = Path.GetFileName(doc.Path);
            Title = pages > 0 ? $"{name} ({doc.Viewer.CurrentPage + 1}/{pages}) - Leaf" : $"{name} - Leaf";
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
