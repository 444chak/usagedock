using System.Net;

namespace UsageDock.Services;

/// <summary>
/// Decides when a provider is allowed to hit the network again.
/// Honours Retry-After and applies capped exponential backoff on repeated failures, so a
/// throttled or broken endpoint is not hammered.
/// </summary>
public sealed class RateLimitPolicy
{
    public static readonly TimeSpan BaseBackoff = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(30);

    private int _consecutiveFailures;
    private DateTimeOffset _nextAttemptUtc = DateTimeOffset.MinValue;

    public int ConsecutiveFailures => _consecutiveFailures;

    public DateTimeOffset NextAttemptUtc => _nextAttemptUtc;

    public bool CanAttempt(DateTimeOffset nowUtc) => nowUtc >= _nextAttemptUtc;

    public void RecordSuccess()
    {
        _consecutiveFailures = 0;
        _nextAttemptUtc = DateTimeOffset.MinValue;
    }

    /// <summary>
    /// Records a failure. <paramref name="retryAfter"/> from the server always wins over the
    /// computed backoff when it is longer.
    /// </summary>
    public TimeSpan RecordFailure(DateTimeOffset nowUtc, TimeSpan? retryAfter = null)
    {
        _consecutiveFailures = Math.Min(_consecutiveFailures + 1, 16);
        var delay = ComputeBackoff(_consecutiveFailures);

        if (retryAfter is { } wait && wait > delay)
        {
            delay = wait > MaxBackoff ? MaxBackoff : wait;
        }

        _nextAttemptUtc = nowUtc + delay;
        return delay;
    }

    /// <summary>Exponential backoff: 30s, 60s, 120s ... capped at 30 minutes.</summary>
    public static TimeSpan ComputeBackoff(int consecutiveFailures)
    {
        if (consecutiveFailures <= 0)
        {
            return TimeSpan.Zero;
        }

        // Cap the shift before it can overflow the multiplication.
        var shift = Math.Min(consecutiveFailures - 1, 10);
        var seconds = BaseBackoff.TotalSeconds * Math.Pow(2, shift);
        var delay = TimeSpan.FromSeconds(seconds);
        return delay > MaxBackoff ? MaxBackoff : delay;
    }

    /// <summary>
    /// Parses a Retry-After header value, which is either delta seconds or an HTTP date.
    /// </summary>
    public static TimeSpan? ParseRetryAfter(string? headerValue, DateTimeOffset nowUtc)
    {
        if (string.IsNullOrWhiteSpace(headerValue))
        {
            return null;
        }

        var text = headerValue.Trim();

        if (int.TryParse(text, out var seconds))
        {
            return seconds <= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(seconds);
        }

        if (DateTimeOffset.TryParse(text, out var when))
        {
            var delta = when - nowUtc;
            return delta <= TimeSpan.Zero ? TimeSpan.Zero : delta;
        }

        return null;
    }

    public static TimeSpan? ParseRetryAfter(HttpResponseMessage response, DateTimeOffset nowUtc)
    {
        var header = response.Headers.RetryAfter;
        if (header is null)
        {
            return null;
        }

        if (header.Delta is { } delta)
        {
            return delta <= TimeSpan.Zero ? TimeSpan.Zero : delta;
        }

        if (header.Date is { } date)
        {
            var wait = date - nowUtc;
            return wait <= TimeSpan.Zero ? TimeSpan.Zero : wait;
        }

        return null;
    }

    /// <summary>True for statuses where retrying quickly is pointless.</summary>
    public static bool IsHardAuthFailure(HttpStatusCode status) =>
        status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;
}
