using UsageDock.Models;
using UsageDock.Providers;

namespace UsageDock.Services;

/// <summary>A null <paramref name="Snapshot"/> means only the activity state changed.</summary>
public sealed record ProviderUpdate(ProviderId Provider, UsageSnapshot? Snapshot, ActivityState Activity);

/// <summary>
/// Polls every provider on a timer and pushes results out. A failed poll falls back to the last
/// cached snapshot so the dock keeps showing the most recent known numbers.
///
/// Snapshots are cached as read and rolled forward to the current time only when published, so
/// a window whose reset has passed drops to zero within one poll even when its source is silent.
/// </summary>
public sealed class UsageCoordinator : IAsyncDisposable
{
    private readonly HttpClient _http;
    private readonly ClaudeUsageProvider _claude;
    private readonly CodexUsageProvider _codex;
    private readonly CacheService _cache;
    private readonly ActivityService _activity;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _pollGate = new(1, 1);

    private Timer? _usageTimer;
    private Timer? _activityTimer;
    private UsageFileWatcher? _claudeWatcher;
    private UsageFileWatcher? _statusLineWatcher;

    /// <summary>Last logged outcome per provider, so a steady state is not logged every minute.</summary>
    private readonly Dictionary<ProviderId, string> _lastLogged = [];

    public UsageCoordinator(CacheService cache, ActivityService activity)
    {
        _cache = cache;
        _activity = activity;

        _http = new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AutomaticDecompression = System.Net.DecompressionMethods.All
        })
        {
            Timeout = TimeSpan.FromSeconds(15)
        };

        _http.DefaultRequestHeaders.UserAgent.ParseAdd("UsageDock/1.0");

        _claude = new ClaudeUsageProvider(_http);
        _codex = new CodexUsageProvider();
    }

    /// <summary>Raised on a thread pool thread; the UI marshals it.</summary>
    public event Action<ProviderUpdate>? Updated;

    public void Start(int pollSeconds)
    {
        var period = TimeSpan.FromSeconds(Math.Clamp(pollSeconds, 15, 3600));

        // Publish whatever is cached immediately so the dock is never blank at startup.
        PublishCached();

        _usageTimer = new Timer(_ => _ = PollAsync(), null, TimeSpan.FromSeconds(1), period);

        // Activity is cheap and local, so it can refresh much faster than the quotas.
        _activityTimer = new Timer(_ => PublishActivity(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2));

        // Claude Desktop writes a new usage sample every fifteen minutes. Reacting to the write
        // means a new sample shows up at once instead of waiting for the next poll.
        _claudeWatcher = new UsageFileWatcher(AppPaths.ClaudeDesktopUsageFile);
        _claudeWatcher.Changed += () => _ = PollAsync();

        // The status line feed is written whenever Claude Code reports new limits, which is the
        // whole point of it, so it gets the same treatment.
        _statusLineWatcher = new UsageFileWatcher(AppPaths.ClaudeStatusLineFile);
        _statusLineWatcher.Changed += () => _ = PollAsync();
    }

    public void SetPollInterval(int pollSeconds)
    {
        var period = TimeSpan.FromSeconds(Math.Clamp(pollSeconds, 15, 3600));
        _usageTimer?.Change(period, period);
    }

    public Task RefreshNowAsync() => PollAsync();

    private void PublishCached()
    {
        foreach (var id in (ProviderId[])[ProviderId.Claude, ProviderId.Codex])
        {
            if (_cache.Get(id) is { } cached)
            {
                Updated?.Invoke(new ProviderUpdate(id, cached.AsOf(DateTimeOffset.UtcNow), CurrentActivity(id)));
            }
        }
    }

    /// <summary>
    /// Activity only tick. It deliberately carries no snapshot: re-publishing the quota numbers
    /// every couple of seconds would raise a dozen property changes and rebuild the hover card
    /// rows for values that did not move.
    /// </summary>
    private void PublishActivity()
    {
        foreach (var id in (ProviderId[])[ProviderId.Claude, ProviderId.Codex])
        {
            Updated?.Invoke(new ProviderUpdate(id, null, CurrentActivity(id)));
        }
    }

    private ActivityState CurrentActivity(ProviderId id) => id switch
    {
        ProviderId.Claude => _activity.ReadHookState(ProviderId.Claude),

        // Codex has no hook system, so freshness of its session log is the available signal.
        ProviderId.Codex => _activity.InferFromLastWrite(_codex.LastSessionWriteUtc),
        _ => ActivityState.Unknown
    };

    private async Task PollAsync()
    {
        if (!await _pollGate.WaitAsync(0).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            await PollProviderAsync(_claude).ConfigureAwait(false);
            await PollProviderAsync(_codex).ConfigureAwait(false);
        }
        finally
        {
            _pollGate.Release();
        }
    }

    private async Task PollProviderAsync(IUsageProvider provider)
    {
        UsageSnapshot snapshot;

        try
        {
            snapshot = await provider.FetchAsync(_cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            Log.Error($"{provider.Id} poll threw", ex);
            snapshot = UsageSnapshot.Failure(provider.Id, SnapshotStatus.Error, "Unexpected error");
        }

        var servedFromCache = false;

        if (snapshot.Status == SnapshotStatus.Ok)
        {
            _cache.Store(snapshot);
        }
        else if (_cache.Get(provider.Id) is { } cached)
        {
            // Keep the numbers on screen, but carry the reason the refresh failed.
            snapshot = cached with { Message = snapshot.Message };
            servedFromCache = true;
        }

        snapshot = snapshot.AsOf(DateTimeOffset.UtcNow);

        LogOutcome(provider.Id, snapshot, servedFromCache);

        Updated?.Invoke(new ProviderUpdate(provider.Id, snapshot, CurrentActivity(provider.Id)));
    }

    /// <summary>
    /// Records what a poll produced, but only when it differs from the last thing recorded for
    /// that provider. Without this a failing provider is invisible: the app happily shows a
    /// stale number and the log says nothing at all, which is exactly what made a dead poll
    /// impossible to diagnose after the fact.
    /// </summary>
    private void LogOutcome(ProviderId provider, UsageSnapshot snapshot, bool servedFromCache)
    {
        var source = servedFromCache ? "cache" : "live";
        var detail = snapshot.Status == SnapshotStatus.Ok
            ? string.Join(", ", snapshot.Windows.Select(w => $"{w.Name} {w.UsedPercent:0.#}%"))
            : snapshot.Message ?? "no detail";

        var line = $"{provider} {snapshot.Status} ({source}): {detail}";

        if (_lastLogged.TryGetValue(provider, out var previous) && previous == line)
        {
            return;
        }

        _lastLogged[provider] = line;

        if (snapshot.Status == SnapshotStatus.Ok)
        {
            Log.Info(line);
        }
        else
        {
            Log.Warn(line);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);

        if (_usageTimer is not null)
        {
            await _usageTimer.DisposeAsync().ConfigureAwait(false);
        }

        if (_activityTimer is not null)
        {
            await _activityTimer.DisposeAsync().ConfigureAwait(false);
        }

        _claudeWatcher?.Dispose();
        _statusLineWatcher?.Dispose();

        _http.Dispose();
        _cts.Dispose();
        _pollGate.Dispose();
    }
}
