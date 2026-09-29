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
/// there is no bearer token to use, and the quotas come from the usage history the desktop app
/// writes to disk. Either way nothing but the app itself ever sees a credential.
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

    public ClaudeUsageProvider(
        HttpClient http,
        RateLimitPolicy? policy = null,
        Func<DateTimeOffset>? now = null,
        string? credentialsPath = null,
        string? localUsagePath = null)
    {
        _http = http;
        _policy = policy ?? new RateLimitPolicy();
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _credentialsPath = credentialsPath;
        _localUsagePath = localUsagePath;
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

        // Local history written by Claude Desktop. No token, no network, nothing to throttle.
        if (ClaudeLocalUsageReader.Read(_localUsagePath) is { Windows.Count: > 0 } local)
        {
            return new UsageSnapshot
            {
                Provider = Id,
                Status = SnapshotStatus.Ok,
                Windows = local.Windows,
                Plan = credentials.SubscriptionType,
                // As fresh as the last desktop sample, not as fresh as this poll.
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
