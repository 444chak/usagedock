using UsageDock.Models;
using UsageDock.Providers;

namespace UsageDock.Services;

public sealed record ProviderUpdate(ProviderId Provider, UsageSnapshot Snapshot, ActivityState Activity);

/// <summary>
/// Polls every provider on a timer and pushes results out. A failed poll falls back to the last
/// cached snapshot so the dock keeps showing the most recent known numbers.
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
                Updated?.Invoke(new ProviderUpdate(id, cached, CurrentActivity(id)));
            }
        }
    }

    private void PublishActivity()
    {
        Updated?.Invoke(new ProviderUpdate(
            ProviderId.Claude,
            _cache.Get(ProviderId.Claude) ?? UsageSnapshot.Failure(ProviderId.Claude, SnapshotStatus.Error, "Loading"),
            CurrentActivity(ProviderId.Claude)));
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

        if (snapshot.Status == SnapshotStatus.Ok)
        {
            _cache.Store(snapshot);
        }
        else if (_cache.Get(provider.Id) is { } cached)
        {
            // Keep the numbers on screen, but carry the reason the refresh failed.
            snapshot = cached with { Message = snapshot.Message };
        }

        Updated?.Invoke(new ProviderUpdate(provider.Id, snapshot, CurrentActivity(provider.Id)));
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

        _http.Dispose();
        _cts.Dispose();
        _pollGate.Dispose();
    }
}
