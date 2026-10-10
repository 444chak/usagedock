using System.Text.Json;
using UsageDock.Models;
using UsageDock.Services;

namespace UsageDock.Providers;

/// <summary>
/// Claude quotas as Claude Code itself reports them to its status line.
///
/// Every status line refresh receives the session as JSON on stdin, and for Pro and Max that
/// includes <c>rate_limits</c>: the five hour and weekly percentages with their exact reset
/// times, taken from the last API response. That is as fresh as Claude usage gets without a
/// token, against the fifteen minute samples Claude Desktop writes. It only flows while Claude
/// Code runs in a terminal, though: the Code tab of Claude Desktop never runs a status line, so
/// the desktop history stays the fallback.
///
/// Only the <c>rate_limits</c> object is kept. The rest of the payload names the session, the
/// working directory and the transcript, none of which this app has any business storing.
/// </summary>
public static class ClaudeStatusLineFeed
{
    /// <summary>
    /// An unchanged reading is still rewritten after this long, so its age keeps saying how
    /// recently Claude Code confirmed it. Rewriting on every refresh would wake the dock for
    /// nothing several times a minute.
    /// </summary>
    public static readonly TimeSpan RefreshUnchangedAfter = TimeSpan.FromMinutes(1);

    /// <summary>What lands on disk. Deliberately nothing but the limits and when they were seen.</summary>
    internal sealed class Record
    {
        public DateTimeOffset At { get; init; }

        public JsonElement RateLimits { get; init; }
    }

    /// <summary>
    /// The <c>rate_limits</c> object of a status line payload, or null when there is none, which
    /// is normal before the first API response of a session and for API key users.
    /// </summary>
    internal static JsonElement? ExtractRateLimits(ReadOnlySpan<byte> utf8Json)
    {
        try
        {
            var reader = new Utf8JsonReader(utf8Json);
            using var document = JsonDocument.ParseValue(ref reader);

            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("rate_limits", out var limits)
                && limits.ValueKind == JsonValueKind.Object
                && ClaudeUsageParser.Parse(limits.GetRawText()).Count > 0)
            {
                return limits.Clone();
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }

    /// <summary>
    /// Stores the limits unless the same ones were stored moments ago. Returns whether it wrote.
    /// </summary>
    internal static bool Store(JsonElement rateLimits, DateTimeOffset now, string? path = null)
    {
        var resolved = path ?? AppPaths.ClaudeStatusLineFile;

        if (JsonIo.TryReadFile<Record>(resolved) is { } previous
            && previous.RateLimits.ValueKind == JsonValueKind.Object
            && JsonElement.DeepEquals(previous.RateLimits, rateLimits)
            && now - previous.At < RefreshUnchangedAfter
            && previous.At <= now)
        {
            return false;
        }

        return JsonIo.TryWriteFile(resolved, new Record { At = now, RateLimits = rateLimits });
    }

    public static ClaudeLocalUsageReader.Result? Read(string? path = null)
    {
        var record = JsonIo.TryReadFile<Record>(path ?? AppPaths.ClaudeStatusLineFile);

        if (record is null || record.RateLimits.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var windows = ClaudeUsageParser.Parse(record.RateLimits.GetRawText());
        return windows.Count == 0 ? null : new ClaudeLocalUsageReader.Result(windows, record.At);
    }

    /// <summary>
    /// The line shown when UsageDock is the only status line, for example <c>5h 11% · week 1%</c>.
    /// Installing a status line costs the user Claude Code's footer hints, so it should at least
    /// show something worth having.
    /// </summary>
    internal static string Summarize(JsonElement? rateLimits)
    {
        if (rateLimits is not { } limits)
        {
            return string.Empty;
        }

        var windows = ClaudeUsageParser.Parse(limits.GetRawText());

        return string.Join(" · ", windows.Select(static w =>
            $"{(w.WindowLength == TimeSpan.FromHours(5) ? "5h" : w.Name.ToLowerInvariant())} {w.UsedPercent:0}%"));
    }
}
