namespace UsageDock.Models;

public enum SnapshotStatus
{
    Ok,

    /// <summary>Credentials missing or empty - the user is not signed in locally.</summary>
    NoCredentials,

    /// <summary>Credentials rejected (401/403).</summary>
    Unauthorized,

    /// <summary>Throttled by the upstream service (429).</summary>
    RateLimited,

    /// <summary>Network or parsing failure.</summary>
    Error
}

/// <summary>
/// Immutable result of one poll of a provider. Never contains credentials.
/// </summary>
public sealed record UsageSnapshot
{
    public required ProviderId Provider { get; init; }

    public required SnapshotStatus Status { get; init; }

    public IReadOnlyList<QuotaWindow> Windows { get; init; } = [];

    /// <summary>Plan or tier label such as "pro" or "plus", when the source exposes one.</summary>
    public string? Plan { get; init; }

    /// <summary>Short, credential free explanation shown in the hover card.</summary>
    public string? Message { get; init; }

    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>True when the values came from cache rather than a fresh read.</summary>
    public bool FromCache { get; init; }

    public bool HasData => Windows.Count > 0;

    /// <summary>The window that drives the ring in the dock: the most used one.</summary>
    public QuotaWindow? Primary =>
        Windows.Count == 0 ? null : Windows.MaxBy(static w => w.UsedPercent);

    /// <summary>The rolling short window, normally five hours. Drives the outer ring.</summary>
    public QuotaWindow? ShortWindow =>
        Match(TimeSpan.FromHours(5), "5 hour") ?? Windows.FirstOrDefault();

    /// <summary>The long window, normally seven days. Drives the inner ring.</summary>
    public QuotaWindow? LongWindow => Match(TimeSpan.FromDays(7), "week");

    /// <summary>
    /// Finds a window by its declared length, falling back to its label when a source does not
    /// report one.
    /// </summary>
    private QuotaWindow? Match(TimeSpan length, string nameFragment)
    {
        var byLength = Windows.FirstOrDefault(
            w => w.WindowLength is { } l && Math.Abs((l - length).TotalMinutes) < 1d);

        return byLength ?? Windows.FirstOrDefault(
            w => w.Name.Contains(nameFragment, StringComparison.OrdinalIgnoreCase));
    }

    public static UsageSnapshot Failure(ProviderId provider, SnapshotStatus status, string message) => new()
    {
        Provider = provider,
        Status = status,
        Message = message
    };
}
