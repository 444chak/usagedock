using UsageDock.Models;
using UsageDock.Services;

namespace UsageDock.Providers;

/// <summary>
/// Codex quotas, from whichever reading is newest: the limits the Codex CLI records into each
/// local session rollout, or the live ones the Codex app server reports on request.
///
/// The rollouts cost nothing and update the moment a local session gets an answer, but the
/// desktop app no longer writes any (see <see cref="CodexCli"/>), so the app server is asked
/// every <see cref="MinCliInterval"/> as well. Neither path touches the stored token.
/// </summary>
public sealed class CodexUsageProvider : IUsageProvider
{
    /// <summary>
    /// Each ask starts a process and makes one request to OpenAI. Between asks the last live
    /// reading stands, and local sessions still show up at once through their rollouts.
    /// </summary>
    public static readonly TimeSpan MinCliInterval = TimeSpan.FromMinutes(2);

    private readonly CodexSessionScanner _scanner;
    private readonly Func<DateTimeOffset> _now;
    private readonly string? _authPath;
    private readonly ICodexCliUsage? _cli;
    private readonly RateLimitPolicy _policy;
    private DateTimeOffset _lastCliAttempt = DateTimeOffset.MinValue;

    /// <summary>
    /// Kept between asks so that a poll in between offers the same reading again rather than an
    /// older rollout, which the coordinator would serve from cache and log as a change.
    /// </summary>
    private UsageSnapshot? _lastLive;

    /// <param name="cli">
    /// The Codex app server to ask, or null to rely on the rollouts alone. Null by default so
    /// that nothing but the app itself ever spawns Codex, tests included.
    /// </param>
    public CodexUsageProvider(
        CodexSessionScanner? scanner = null,
        Func<DateTimeOffset>? now = null,
        string? authPath = null,
        ICodexCliUsage? cli = null,
        RateLimitPolicy? policy = null)
    {
        _scanner = scanner ?? new CodexSessionScanner();
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _authPath = authPath;
        _cli = cli;
        _policy = policy ?? new RateLimitPolicy();
    }

    public ProviderId Id => ProviderId.Codex;

    /// <summary>Newest rollout write time, used by the activity monitor.</summary>
    public DateTimeOffset? LastSessionWriteUtc { get; private set; }

    public async Task<UsageSnapshot> FetchAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var now = _now();
        var auth = CodexAuthReader.Read(_authPath);

        CodexSessionScanner.Scan scan;
        string? scanProblem = null;
        try
        {
            scan = _scanner.Run();
        }
        catch (Exception ex)
        {
            // The app server can still answer, so a broken scan is not the end of the poll.
            Log.Error("Codex session scan failed", ex);
            scan = new CodexSessionScanner.Scan(null, null);
            scanProblem = "Could not read Codex sessions";
        }

        LastSessionWriteUtc = scan.NewestActivityUtc;

        if (!auth.HasSession)
        {
            return UsageSnapshot.Failure(Id, SnapshotStatus.NoCredentials, "Not signed in to Codex");
        }

        string? cliProblem = null;

        if (_cli is { IsAvailable: true } && now - _lastCliAttempt >= MinCliInterval)
        {
            if (_policy.CanAttempt(now))
            {
                _lastCliAttempt = now;
                cliProblem = await AskCliAsync(now, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                cliProblem = $"Backing off, retry in {ClaudeUsageProvider.FormatDelay(_policy.NextAttemptUtc - now)}";
            }
        }

        UsageSnapshot? local = scan.RateLimits is { Windows.Count: > 0 } limits
            ? new UsageSnapshot
            {
                Provider = Id,
                Status = SnapshotStatus.Ok,
                Windows = limits.Windows,
                Plan = limits.PlanType ?? auth.AuthMode,
                // The numbers are as fresh as the record they came from, not as fresh as this poll.
                UpdatedAt = limits.RecordedAt ?? scan.NewestActivityUtc ?? now
            }
            : null;

        var newest = local is null || (_lastLive is not null && _lastLive.UpdatedAt >= local.UpdatedAt)
            ? _lastLive
            : local;

        if (newest is not null)
        {
            return newest with { Message = cliProblem };
        }

        return UsageSnapshot.Failure(
            Id,
            SnapshotStatus.Error,
            cliProblem ?? scanProblem ?? "No quota data yet, run Codex once");
    }

    /// <summary>Stores a successful answer in <see cref="_lastLive"/>, or returns what went wrong.</summary>
    private async Task<string?> AskCliAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var outcome = await _cli!.GetRateLimitsAsync(cancellationToken).ConfigureAwait(false);
        var parsed = outcome.Response is null ? null : CodexCliRateLimitParser.Parse(outcome.Response);

        if (parsed is { Windows.Count: > 0 })
        {
            _policy.RecordSuccess();
            _lastLive = new UsageSnapshot
            {
                Provider = Id,
                Status = SnapshotStatus.Ok,
                Windows = parsed.Windows,
                Plan = parsed.Plan,
                UpdatedAt = now
            };
            return null;
        }

        // Backing off keeps a broken install or login from starting a process every poll.
        var problem = outcome.Problem
                      ?? (parsed?.Error is { } error ? $"Codex: {error}" : null)
                      ?? (parsed is null ? "Unreadable answer from Codex" : "No rate limits from Codex");
        var delay = _policy.RecordFailure(now);
        Log.Warn($"{problem}, backing off {ClaudeUsageProvider.FormatDelay(delay)}");
        return problem;
    }
}
