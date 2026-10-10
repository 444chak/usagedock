using System.Net;
using System.Net.Http.Headers;
using UsageDock.Models;
using UsageDock.Services;

namespace UsageDock.Providers;

/// <summary>
/// Claude quotas, from whichever source is available.
///
/// The standalone Claude Code CLI stores an OAuth token in .credentials.json, and when one is
/// there the usage endpoint is the better source: it is live and it carries exact reset times.
/// Claude Desktop keeps its session in an encrypted Electron store instead, so on those machines
/// there is no bearer token to use, and the quotas come from what is on disk: the readings Claude
/// Code hands its status line, and the usage history the desktop app samples every fifteen
/// minutes. Whichever was taken last wins. Either way nothing but the app itself ever sees a
/// credential.
/// </summary>
public sealed class ClaudeUsageProvider : IUsageProvider
{
    private const string UsageEndpoint = "https://api.anthropic.com/api/oauth/usage";
    private const string OAuthBeta = "oauth-2025-04-20";

    private readonly HttpClient _http;
    private readonly RateLimitPolicy _policy;
    private readonly Func<DateTimeOffset> _now;
    private readonly string? _credentialsPath;
    private readonly string? _localUsagePath;
    private readonly string? _statusLinePath;

    public ClaudeUsageProvider(
        HttpClient http,
        RateLimitPolicy? policy = null,
        Func<DateTimeOffset>? now = null,
        string? credentialsPath = null,
        string? localUsagePath = null,
        string? statusLinePath = null)
    {
        _http = http;
        _policy = policy ?? new RateLimitPolicy();
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _credentialsPath = credentialsPath;
        _localUsagePath = localUsagePath;
        _statusLinePath = statusLinePath;
    }

    public ProviderId Id => ProviderId.Claude;

    public async Task<UsageSnapshot> FetchAsync(CancellationToken cancellationToken)
    {
        var now = _now();
        var credentials = ClaudeCredentialsReader.Read(_credentialsPath);
        string? apiProblem = null;

        if (credentials.HasToken && !credentials.IsExpired(now))
        {
            if (_policy.CanAttempt(now))
            {
                var (snapshot, problem) = await CallUsageEndpointAsync(credentials, now, cancellationToken)
                    .ConfigureAwait(false);

                if (snapshot is not null)
                {
                    return snapshot;
                }

                apiProblem = problem;
            }
            else
            {
                apiProblem = $"Backing off, retry in {FormatDelay(_policy.NextAttemptUtc - now)}";
            }
        }

        // Local readings. No token, no network, nothing to throttle.
        var local = Freshest(
            ClaudeStatusLineFeed.Read(_statusLinePath),
            ClaudeLocalUsageReader.Read(_localUsagePath));

        if (local is { Windows.Count: > 0 })
        {
            return new UsageSnapshot
            {
                Provider = Id,
                Status = SnapshotStatus.Ok,
                Windows = local.Windows,
                Plan = credentials.SubscriptionType,
                // As fresh as the reading, not as fresh as this poll.
                UpdatedAt = local.SampledAt,
                Message = apiProblem
            };
        }

        if (apiProblem is not null)
        {
            return UsageSnapshot.Failure(Id, SnapshotStatus.Error, apiProblem);
        }

        return UsageSnapshot.Failure(
            Id,
            SnapshotStatus.NoCredentials,
            credentials.HasToken
                ? "Claude Code session expired, run /login"
                : "No local Claude usage found");
    }

    /// <summary>
    /// Picks the most recent of the two local readings.
    ///
    /// When the desktop sample is the newer one its percentages win, but its reset times are only
    /// inferred, while the status line carries exact ones. A status line reset that still lies
    /// after the desktop sample belongs to the very window that sample was taken in, so it is
    /// carried over.
    /// </summary>
    internal static ClaudeLocalUsageReader.Result? Freshest(
        ClaudeLocalUsageReader.Result? statusLine,
        ClaudeLocalUsageReader.Result? desktop)
    {
        if (statusLine is null || desktop is null)
        {
            return statusLine ?? desktop;
        }

        if (statusLine.SampledAt >= desktop.SampledAt)
        {
            return statusLine;
        }

        var windows = desktop.Windows
            .Select(window => statusLine.Windows.FirstOrDefault(exact =>
                    exact.WindowLength == window.WindowLength
                    && exact.ResetsAt > desktop.SampledAt) is { } exact
                ? window with { ResetsAt = exact.ResetsAt }
                : window)
            .ToList();

        return desktop with { Windows = windows };
    }

    /// <summary>
    /// Returns a snapshot on success, or the reason it failed so the caller can fall back.
    /// </summary>
    private async Task<(UsageSnapshot? Snapshot, string? Problem)> CallUsageEndpointAsync(
        ClaudeCredentialsReader.Result credentials,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, UsageEndpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.AccessToken);
            request.Headers.Add("anthropic-beta", OAuthBeta);
            request.Headers.Add("Accept", "application/json");

            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var retryAfter = RateLimitPolicy.ParseRetryAfter(response, now);
                var delay = _policy.RecordFailure(now, retryAfter);
                Log.Warn($"Claude usage throttled (429), backing off {FormatDelay(delay)}");
                return (null, "Throttled by Anthropic (429)");
            }

            if (RateLimitPolicy.IsHardAuthFailure(response.StatusCode))
            {
                _policy.RecordFailure(now);
                Log.Warn($"Claude usage rejected credentials ({(int)response.StatusCode})");
                return (null, $"Credentials rejected ({(int)response.StatusCode}), run /login");
            }

            if (!response.IsSuccessStatusCode)
            {
                var delay = _policy.RecordFailure(now);
                Log.Warn($"Claude usage HTTP {(int)response.StatusCode}, backing off {FormatDelay(delay)}");
                return (null, $"HTTP {(int)response.StatusCode}");
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var windows = ClaudeUsageParser.Parse(body);

            if (windows.Count == 0)
            {
                _policy.RecordFailure(now);
                return (null, "No quota data in response");
            }

            _policy.RecordSuccess();

            return (new UsageSnapshot
            {
                Provider = Id,
                Status = SnapshotStatus.Ok,
                Windows = windows,
                Plan = credentials.SubscriptionType,
                UpdatedAt = now
            }, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var delay = _policy.RecordFailure(now);
            Log.Error($"Claude usage request failed, backing off {FormatDelay(delay)}", ex);
            return (null, "Network error");
        }
    }

    internal static string FormatDelay(TimeSpan delay) =>
        delay.TotalMinutes >= 1 ? $"{(int)delay.TotalMinutes}m" : $"{Math.Max(1, (int)delay.TotalSeconds)}s";
}
