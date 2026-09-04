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

    public void OpenFile(string path)
    {
        foreach (TabViewItem item in Tabs.TabItems.OfType<TabViewItem>())
        {
            if (item.Tag is string existing && string.Equals(existing, path, StringComparison.OrdinalIgnoreCase))
            {
                Tabs.SelectedItem = item;
                return;
            }
        }

        var tab = new TabViewItem
        {
            Header = Path.GetFileName(path),
            Tag = path,
            IconSource = new SymbolIconSource { Symbol = Symbol.Document },
            Content = new TextBlock { Text = path, Margin = new Thickness(24), TextWrapping = TextWrapping.Wrap },
        };
        ToolTipService.SetToolTip(tab, path);
        Tabs.TabItems.Add(tab);
        Tabs.SelectedItem = tab;
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
        if (Tabs.SelectedItem is TabViewItem { Header: string name } tab)
        {
            Title = $"{name} - Leaf";
            TabContent.Content = tab.Content;
            WelcomePanel.Visibility = Visibility.Collapsed;
        }
        else
        {
            Title = "Leaf";
            TabContent.Content = null;
            WelcomePanel.Visibility = Visibility.Visible;
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
        object? content = tab.Content;
        Tabs.TabItems.Remove(tab);
        (content as IDisposable)?.Dispose();
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
}
