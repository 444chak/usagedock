using System.Text.Json;
using UsageDock.Services;

namespace UsageDock.Providers;

/// <summary>
/// Read only access to the Claude Code OAuth token.
///
/// The token is returned to the caller for use as a bearer header and is never logged, cached,
/// or written anywhere. The credentials file itself is opened read only and never modified.
/// </summary>
public static class ClaudeCredentialsReader
{
    public sealed record Result(string? AccessToken, DateTimeOffset? ExpiresAt, string? SubscriptionType)
    {
        public bool HasToken => !string.IsNullOrWhiteSpace(AccessToken);

        /// <summary>Expiry of 0 or missing is treated as "unknown", not as expired.</summary>
        public bool IsExpired(DateTimeOffset nowUtc) =>
            ExpiresAt is { } expiry && expiry > DateTimeOffset.UnixEpoch && expiry <= nowUtc;

        public static readonly Result Empty = new(null, null, null);
    }

    public static Result Read(string? path = null)
    {
        var text = JsonIo.TryReadAllText(path ?? AppPaths.ClaudeCredentialsFile);
        return text is null ? Result.Empty : Parse(text);
    }

    internal static Result Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("claudeAiOauth", out var oauth)
                || oauth.ValueKind != JsonValueKind.Object)
            {
                return Result.Empty;
            }

            string? token = null;
            if (oauth.TryGetProperty("accessToken", out var tokenElement)
                && tokenElement.ValueKind == JsonValueKind.String)
            {
                var value = tokenElement.GetString();
                token = string.IsNullOrWhiteSpace(value) ? null : value;
            }

            DateTimeOffset? expiresAt = null;
            if (oauth.TryGetProperty("expiresAt", out var expiryElement)
                && expiryElement.ValueKind == JsonValueKind.Number
                && expiryElement.TryGetInt64(out var epochMs)
                && epochMs > 0)
            {
                expiresAt = DateTimeOffset.FromUnixTimeMilliseconds(epochMs);
            }

            string? subscription = null;
            if (oauth.TryGetProperty("subscriptionType", out var subElement)
                && subElement.ValueKind == JsonValueKind.String)
            {
                subscription = subElement.GetString();
            }

            return new Result(token, expiresAt, subscription);
        }
        catch (JsonException)
        {
            return Result.Empty;
        }
    }
}
