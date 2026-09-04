using Microsoft.UI.Dispatching;

namespace Leaf.Services;

/// <summary>
/// Watches one file for external changes (save-over, replace, delete, rename) and raises <see cref="Changed"/>
/// on the UI thread after a short debounce. Leaf never locks files, so this is how edits made elsewhere show up.
/// </summary>
public sealed class DocumentWatcher : IDisposable
{
    private readonly FileSystemWatcher? _watcher;
    private readonly DispatcherQueue _dispatcher;
    private readonly DispatcherQueueTimer _debounce;
    private bool _pending;

    public DocumentWatcher(string path, DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
        _debounce = dispatcher.CreateTimer();
        _debounce.Interval = TimeSpan.FromMilliseconds(600);
        _debounce.IsRepeating = false;
        _debounce.Tick += (_, _) =>
        {
            if (_pending)
            {
                _pending = false;
                Changed?.Invoke(this, EventArgs.Empty);
            }
        };

        string? dir = Path.GetDirectoryName(path);
        string name = Path.GetFileName(path);
        if (dir is null || !Directory.Exists(dir))
        {
            return;
        }

        try
        {
            _watcher = new FileSystemWatcher(dir, name)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName | NotifyFilters.CreationTime,
                IncludeSubdirectories = false,
            };
            _watcher.Changed += OnEvent;
            _watcher.Created += OnEvent;
            _watcher.Deleted += OnEvent;
            _watcher.Renamed += OnEvent;
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            _watcher = null; // network share or odd folder: just don't watch
        }
    }

    public event EventHandler? Changed;

    private void OnEvent(object sender, FileSystemEventArgs e)
    {
        _dispatcher.TryEnqueue(() =>
        {
            _pending = true;
            _debounce.Stop();
            _debounce.Start();
        });
    }

    public void Dispose()
    {
        _debounce.Stop();
        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
        }
    }
}
