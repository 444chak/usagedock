using System.Net;
using System.Net.Http.Headers;
using UsageDock.Models;
using UsageDock.Services;

namespace UsageDock.Providers;

/// <summary>
/// Reads Claude Code quotas from the OAuth usage endpoint, using the token that Claude Code
/// already stores locally. The token is held only for the duration of one request.
/// </summary>
public sealed class ClaudeUsageProvider : IUsageProvider
{
    private const string UsageEndpoint = "https://api.anthropic.com/api/oauth/usage";
    private const string OAuthBeta = "oauth-2025-04-20";

    private readonly HttpClient _http;
    private readonly RateLimitPolicy _policy;
    private readonly Func<DateTimeOffset> _now;
    private readonly string? _credentialsPath;

    public ClaudeUsageProvider(
        HttpClient http,
        RateLimitPolicy? policy = null,
        Func<DateTimeOffset>? now = null,
        string? credentialsPath = null)
    {
        _http = http;
        _policy = policy ?? new RateLimitPolicy();
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _credentialsPath = credentialsPath;
    }

    public ProviderId Id => ProviderId.Claude;

    public async Task<UsageSnapshot> FetchAsync(CancellationToken cancellationToken)
    {
        var now = _now();

        if (!_policy.CanAttempt(now))
        {
            return UsageSnapshot.Failure(
                Id,
                SnapshotStatus.RateLimited,
                $"Backing off, retry in {FormatDelay(_policy.NextAttemptUtc - now)}");
        }

        var credentials = ClaudeCredentialsReader.Read(_credentialsPath);

        if (!credentials.HasToken)
        {
            // Not a network failure, so no backoff: the user may sign in at any moment.
            return UsageSnapshot.Failure(
                Id,
                SnapshotStatus.NoCredentials,
                "Not signed in to Claude Code");
        }

        if (credentials.IsExpired(now))
        {
            return UsageSnapshot.Failure(
                Id,
                SnapshotStatus.NoCredentials,
                "Claude Code session expired, run /login");
        }

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
                return UsageSnapshot.Failure(Id, SnapshotStatus.RateLimited, "Throttled by Anthropic (429)");
            }

            if (RateLimitPolicy.IsHardAuthFailure(response.StatusCode))
            {
                _policy.RecordFailure(now);
                Log.Warn($"Claude usage rejected credentials ({(int)response.StatusCode})");
                return UsageSnapshot.Failure(
                    Id,
                    SnapshotStatus.Unauthorized,
                    $"Credentials rejected ({(int)response.StatusCode}), run /login");
            }

            if (!response.IsSuccessStatusCode)
            {
                var delay = _policy.RecordFailure(now);
                Log.Warn($"Claude usage HTTP {(int)response.StatusCode}, backing off {FormatDelay(delay)}");
                return UsageSnapshot.Failure(Id, SnapshotStatus.Error, $"HTTP {(int)response.StatusCode}");
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var windows = ClaudeUsageParser.Parse(body);

            if (windows.Count == 0)
            {
                _policy.RecordFailure(now);
                return UsageSnapshot.Failure(Id, SnapshotStatus.Error, "No quota data in response");
            }

            _policy.RecordSuccess();

            return new UsageSnapshot
            {
                Provider = Id,
                Status = SnapshotStatus.Ok,
                Windows = windows,
                Plan = credentials.SubscriptionType,
                UpdatedAt = now
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var delay = _policy.RecordFailure(now);
            Log.Error($"Claude usage request failed, backing off {FormatDelay(delay)}", ex);
            return UsageSnapshot.Failure(Id, SnapshotStatus.Error, "Network error");
        }
    }

    internal static string FormatDelay(TimeSpan delay) =>
        delay.TotalMinutes >= 1 ? $"{(int)delay.TotalMinutes}m" : $"{Math.Max(1, (int)delay.TotalSeconds)}s";
}
