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

    /// <summary>How often to retry attaching when the folder is not there yet.</summary>
    public static readonly TimeSpan DefaultAttachRetry = TimeSpan.FromMinutes(1);

    private readonly string _filePath;
    private readonly Timer _debounce;
    private readonly Timer _attachRetry;
    private readonly Lock _gate = new();

    private FileSystemWatcher? _watcher;
    private bool _disposed;

    public UsageFileWatcher(string filePath, TimeSpan? attachRetry = null)
    {
        _filePath = filePath;
        var retry = attachRetry ?? DefaultAttachRetry;
        _debounce = new Timer(_ => Raise(), null, Timeout.Infinite, Timeout.Infinite);

        // At Windows login this can run before the folder it wants exists, and a watcher that
        // gave up once would stay dead for the whole session. Retrying costs a directory check
        // a minute and stops silently.
        _attachRetry = new Timer(_ => TryAttach(), null, retry, retry);

        TryAttach();
    }

    public event Action? Changed;

    public bool IsWatching
    {
        get
        {
            lock (_gate)
            {
                return _watcher is not null;
            }
        }
    }

    private void TryAttach()
    {
        lock (_gate)
        {
            if (_disposed || _watcher is not null)
            {
                return;
            }

            try
            {
                var directory = Path.GetDirectoryName(_filePath);
                if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
                {
                    return;
                }

                var watcher = new FileSystemWatcher(directory, Path.GetFileName(_filePath))
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName
                };

                watcher.Changed += OnChanged;
                watcher.Created += OnChanged;
                watcher.Renamed += OnChanged;
                watcher.Error += OnError;
                watcher.EnableRaisingEvents = true;

                _watcher = watcher;
                Log.Info($"Watching {Path.GetFileName(_filePath)} for changes");
            }
            catch (Exception ex)
            {
                Log.Error($"Could not watch {Path.GetFileName(_filePath)}", ex);
            }
        }
    }

    /// <summary>
    /// A watcher can die on its own, for example if its folder is removed. Dropping it lets the
    /// retry timer attach a fresh one; polling covers the gap either way.
    /// </summary>
    private void OnError(object sender, ErrorEventArgs e)
    {
        Log.Warn($"Usage file watcher stopped, will retry: {e.GetException().Message}");

        lock (_gate)
        {
            _watcher?.Dispose();
            _watcher = null;
        }
    }

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
        lock (_gate)
        {
            _disposed = true;

            if (_watcher is not null)
            {
                _watcher.EnableRaisingEvents = false;
                _watcher.Dispose();
                _watcher = null;
            }
        }

        _attachRetry.Dispose();
        _debounce.Dispose();
    }
}
