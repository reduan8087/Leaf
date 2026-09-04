using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace Leaf;

public partial class App : Application
{
    private readonly string[] _launchArgs;
    private MainWindow? _window;

    public App(string[] launchArgs)
    {
        _launchArgs = launchArgs;
        InitializeComponent();
        UnhandledException += OnUnhandledException;
    }

    public static new App Current => (App)Application.Current;

    public MainWindow? Window => _window;

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        AppInstance.GetCurrent().Activated += OnRedirectedActivation;
        _window.Activate();

        foreach (string path in ActivationParser.ExtractPdfPaths(_launchArgs))
        {
            _window.OpenFile(path);
        }
    }

    private void OnRedirectedActivation(object? sender, AppActivationArguments e)
    {
        string? commandLine = (e.Data as Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs)?.Arguments;
        IReadOnlyList<string> paths = ActivationParser.ExtractPdfPaths(ActivationParser.SplitCommandLine(commandLine));
        if (_window is null)
        {
            return;
        }

        _window.DispatcherQueue.TryEnqueue(() =>
        {
            _window.BringToFront();
            foreach (string path in paths)
            {
                _window.OpenFile(path);
            }
        });
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        // Last line of defence: log and keep the process alive when possible.
        Debug.WriteLine($"[Leaf] Unhandled: {e.Exception}");
        e.Handled = true;
        _window?.ShowError("Something went wrong", e.Message);
    }
}
