using System.Diagnostics;

namespace Leaf;

/// <summary>
/// Startup instrumentation. When the environment variable LEAF_PERF is set, appends "name=milliseconds since process start"
/// lines to %LOCALAPPDATA%\Leaf\perf.log so scripts/measure-perf.ps1 can report first-frame and first-page times.
/// </summary>
internal static class PerfLog
{
    private static readonly bool Enabled = Environment.GetEnvironmentVariable("LEAF_PERF") is { Length: > 0 };
    private static readonly DateTime ProcessStart = Process.GetCurrentProcess().StartTime;

    public static void Stamp(string name)
    {
        double ms = (DateTime.Now - ProcessStart).TotalMilliseconds;
        Debug.WriteLine($"[Leaf perf] {name}={ms:F0} ms, WS={Environment.WorkingSet / 1048576} MB");
        if (!Enabled)
        {
            return;
        }

        try
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Leaf");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "perf.log"), $"{name}={ms:F0} ws={Environment.WorkingSet / 1048576}{Environment.NewLine}");
        }
        catch (IOException)
        {
            // best effort
        }
    }
}
