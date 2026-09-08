namespace UsageDock.Services;

/// <summary>
/// Watches a local usage file and reports when it changes.
///
/// Claude Desktop only samples its usage every fifteen minutes, so the numbers can never be
/// fresher than that. What this removes is the extra lag on top: without it a new sample would
/// sit unnoticed until the next poll came round.
/// </summary>
public sealed class UsageFileWatcher : IDisposable
{
    /// <summary>A single save raises several events, so they are coalesced.</summary>
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(750);

    private readonly FileSystemWatcher? _watcher;
    private readonly Timer _debounce;
    private bool _disposed;

    public UsageFileWatcher(string filePath)
    {
        _debounce = new Timer(_ => Raise(), null, Timeout.Infinite, Timeout.Infinite);

        try
        {
            var directory = Path.GetDirectoryName(filePath);
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            {
                return;
            }

            _watcher = new FileSystemWatcher(directory, Path.GetFileName(filePath))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                EnableRaisingEvents = true
            };

            _watcher.Changed += OnChanged;
            _watcher.Created += OnChanged;
            _watcher.Renamed += OnChanged;

            // A watcher can die on its own, for example if the folder is removed. Failing quietly
            // is fine: polling still covers the file.
            _watcher.Error += (_, e) => Log.Warn($"Usage file watcher stopped: {e.GetException().Message}");
        }
        catch (Exception ex)
        {
            Log.Error($"Could not watch {Path.GetFileName(filePath)}", ex);
            _watcher = null;
        }
    }

    public event Action? Changed;

    public bool IsWatching => _watcher is not null;

    private void OnChanged(object sender, FileSystemEventArgs e)
    {
        if (!_disposed)
        {
            _debounce.Change(Debounce, Timeout.InfiniteTimeSpan);
        }
    }

    private void Raise()
    {
        if (!_disposed)
        {
            Changed?.Invoke();
        }
    }

    public void Dispose()
    {
        _disposed = true;

        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
        }

        _debounce.Dispose();
    }
}
