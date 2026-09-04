using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace Leaf;

/// <summary>
/// Custom entry point. Runs BEFORE any XAML is loaded so that a second launch
/// (e.g. double-clicking another PDF in Explorer) costs only a few milliseconds:
/// it forwards its command line to the running instance and exits.
/// </summary>
public static class Program
{
    public const string InstanceKey = "Leaf";

    [STAThread]
    private static int Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();

        AppInstance mainInstance = AppInstance.FindOrRegisterForKey(InstanceKey);
        if (!mainInstance.IsCurrent)
        {
            AppActivationArguments activationArgs = AppInstance.GetCurrent().GetActivatedEventArgs();
            // Run the redirect on an MTA thread-pool thread so the STA main thread never has to pump.
            Task.Run(async () => await mainInstance.RedirectActivationToAsync(activationArgs)).GetAwaiter().GetResult();
            return 0;
        }

        Application.Start(p =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            new App(args);
        });
        return 0;
    }
}
