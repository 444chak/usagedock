using System.Text.Json;
using UsageDock.Services;

namespace UsageDock.Providers;

/// <summary>
/// Read only probe of the local Codex session.
///
/// Only the presence of a session and the auth mode are extracted. Token values are never read
/// out of the document, never returned, and never logged.
/// </summary>
public static class CodexAuthReader
{
    public sealed record Result(bool HasSession, string? AuthMode, DateTimeOffset? LastRefresh)
    {
        public static readonly Result None = new(false, null, null);
    }

    public static Result Read(string? path = null)
    {
        var text = JsonIo.TryReadAllText(path ?? AppPaths.CodexAuthFile);
        return text is null ? Result.None : Parse(text);
    }

    internal static Result Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Result.None;
            }

            var hasSession = false;
            if (root.TryGetProperty("tokens", out var tokens) && tokens.ValueKind == JsonValueKind.Object)
            {
                // Presence only. The value is deliberately not materialised into a variable.
                hasSession = tokens.TryGetProperty("access_token", out var accessToken)
                    && accessToken.ValueKind == JsonValueKind.String
                    && accessToken.GetString()?.Length > 0;
            }

            if (!hasSession
                && root.TryGetProperty("OPENAI_API_KEY", out var apiKey)
                && apiKey.ValueKind == JsonValueKind.String)
            {
                hasSession = apiKey.GetString()?.Length > 0;
            }

            string? mode = null;
            if (root.TryGetProperty("auth_mode", out var modeElement)
                && modeElement.ValueKind == JsonValueKind.String)
            {
                mode = modeElement.GetString();
            }

            DateTimeOffset? lastRefresh = null;
            if (root.TryGetProperty("last_refresh", out var refreshElement)
                && refreshElement.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(refreshElement.GetString(), out var parsed))
            {
                lastRefresh = parsed.ToUniversalTime();
            }

            return new Result(hasSession, mode, lastRefresh);
        }
        catch (JsonException)
        {
            return Result.None;
        }
    }
}
