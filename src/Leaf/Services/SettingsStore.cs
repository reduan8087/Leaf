using System.Text.Json;

namespace Leaf.Services;

/// <summary>
/// Loads and saves <see cref="AppSettings"/> in %LOCALAPPDATA%\Leaf\settings.json.
/// Settings are a convenience, never a dependency: a missing, unreadable or corrupt file silently yields
/// defaults, and a failed write is dropped rather than surfaced. Writes are debounced because the viewer
/// updates preferences on every zoom and panel change.
/// </summary>
public static class SettingsStore
{
    private const int SaveDelayMs = 750;

    private static readonly Lock Gate = new();
    private static AppSettings? s_current;
    private static Timer? s_timer;
    private static bool s_dirty;

    public static string Directory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Leaf");

    private static string FilePath => Path.Combine(Directory, "settings.json");

    /// <summary>The live settings object. Mutate it, then call <see cref="Save"/>.</summary>
    public static AppSettings Current
    {
        get
        {
            lock (Gate)
            {
                return s_current ??= Load();
            }
        }
    }

    private static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                using FileStream stream = File.OpenRead(FilePath);
                AppSettings? loaded = JsonSerializer.Deserialize(stream, LeafJsonContext.Default.AppSettings);
                if (loaded is not null)
                {
                    loaded.View ??= new ViewPreferences();
                    loaded.Recent ??= [];
                    return loaded;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // A settings file we cannot read is not worth interrupting the user for.
        }

        return new AppSettings();
    }

    /// <summary>Queues a write. Repeated calls inside the debounce window collapse into one.</summary>
    public static void Save()
    {
        lock (Gate)
        {
            if (s_current is null)
            {
                return;
            }

            s_dirty = true;
            s_timer ??= new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
            s_timer.Change(SaveDelayMs, Timeout.Infinite);
        }
    }

    /// <summary>Writes immediately if anything is pending. Called when the window closes.</summary>
    public static void Flush()
    {
        AppSettings snapshot;
        lock (Gate)
        {
            if (!s_dirty || s_current is null)
            {
                return;
            }

            s_dirty = false;
            snapshot = s_current;
        }

        try
        {
            System.IO.Directory.CreateDirectory(Directory);

            // Write beside the target and swap, so a crash mid-write cannot leave a truncated settings file.
            string temp = FilePath + ".tmp";
            using (FileStream stream = File.Create(temp))
            {
                JsonSerializer.Serialize(stream, snapshot, LeafJsonContext.Default.AppSettings);
            }

            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Losing preferences is acceptable; interrupting the user over it is not.
        }
    }
}
