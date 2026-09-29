using UsageDock.Models;
using UsageDock.Services;

namespace UsageDock.Providers;

/// <summary>
/// Codex quotas, sourced entirely from local files.
///
/// The Codex CLI records its server reported rate limits into each session rollout, so the
/// short (5 hour) and weekly windows can be read without any network call and without touching
/// the stored token.
/// </summary>
public sealed class CodexUsageProvider : IUsageProvider
{
    private readonly CodexSessionScanner _scanner;
    private readonly Func<DateTimeOffset> _now;
    private readonly string? _authPath;

    public CodexUsageProvider(
        CodexSessionScanner? scanner = null,
        Func<DateTimeOffset>? now = null,
        string? authPath = null)
    {
        _scanner = scanner ?? new CodexSessionScanner();
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _authPath = authPath;
    }

    public ProviderId Id => ProviderId.Codex;

    /// <summary>Newest rollout write time, used by the activity monitor.</summary>
    public DateTimeOffset? LastSessionWriteUtc { get; private set; }

    public Task<UsageSnapshot> FetchAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Fetch());
    }

    private UsageSnapshot Fetch()
    {
        var now = _now();
        var auth = CodexAuthReader.Read(_authPath);

        Scan scan;
        try
        {
            var raw = _scanner.Run();
            scan = new Scan(raw.RateLimits, raw.NewestActivityUtc);
        }
        catch (Exception ex)
        {
            Log.Error("Codex session scan failed", ex);
            return UsageSnapshot.Failure(Id, SnapshotStatus.Error, "Could not read Codex sessions");
        }

        LastSessionWriteUtc = scan.NewestActivityUtc;

        if (!auth.HasSession)
        {
            return UsageSnapshot.Failure(Id, SnapshotStatus.NoCredentials, "Not signed in to Codex");
        }

        if (scan.RateLimits is not { Windows.Count: > 0 } limits)
        {
            return UsageSnapshot.Failure(
                Id,
                SnapshotStatus.Error,
                "No quota data yet, run Codex once");
        }

        return new UsageSnapshot
        {
            Provider = Id,
            Status = SnapshotStatus.Ok,
            Windows = limits.Windows,
            Plan = limits.PlanType ?? auth.AuthMode,
            // The numbers are as fresh as the record they came from, not as fresh as this poll.
            UpdatedAt = limits.RecordedAt ?? scan.NewestActivityUtc ?? now
        };
    }

    private readonly record struct Scan(
        CodexRateLimitParser.Result? RateLimits,
        DateTimeOffset? NewestActivityUtc);
}
