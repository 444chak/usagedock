using System.Text.Json;
using UsageDock.Models;

namespace UsageDock.Providers;

/// <summary>
/// Parses the <c>rate_limits</c> payload that the Codex CLI records in its session rollout
/// files. Reading these local files means no token ever leaves the machine.
/// </summary>
public static class CodexRateLimitParser
{
    public sealed record Result(IReadOnlyList<QuotaWindow> Windows, string? PlanType);

    /// <summary>
    /// Extracts rate limits from a single rollout line. Returns null when the line carries none.
    /// </summary>
    public static Result? ParseLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line) || !line.Contains("rate_limits", StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(line);
            return FindRateLimits(document.RootElement, depth: 0) is { } limits ? ParseRateLimits(limits) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Parses a <c>rate_limits</c> object directly.</summary>
    public static Result ParseRateLimits(JsonElement limits)
    {
        var windows = new List<QuotaWindow>();

        if (TryReadWindow(limits, "primary") is { } primary)
        {
            windows.Add(primary);
        }

        if (TryReadWindow(limits, "secondary") is { } secondary)
        {
            windows.Add(secondary);
        }

        string? plan = null;
        if (limits.TryGetProperty("plan_type", out var planElement)
            && planElement.ValueKind == JsonValueKind.String)
        {
            plan = planElement.GetString();
        }

        return new Result(windows, plan);
    }

    /// <summary>Walks the object graph looking for a rate_limits object.</summary>
    private static JsonElement? FindRateLimits(JsonElement element, int depth)
    {
        if (depth > 6 || element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (element.TryGetProperty("rate_limits", out var direct) && direct.ValueKind == JsonValueKind.Object)
        {
            return direct;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (FindRateLimits(property.Value, depth + 1) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private static QuotaWindow? TryReadWindow(JsonElement limits, string key)
    {
        if (!limits.TryGetProperty(key, out var section) || section.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!section.TryGetProperty("used_percent", out var usedElement)
            || usedElement.ValueKind != JsonValueKind.Number
            || !usedElement.TryGetDouble(out var used))
        {
            return null;
        }

        TimeSpan? length = null;
        if (section.TryGetProperty("window_minutes", out var minutesElement)
            && minutesElement.ValueKind == JsonValueKind.Number
            && minutesElement.TryGetDouble(out var minutes)
            && minutes > 0)
        {
            length = TimeSpan.FromMinutes(minutes);
        }

        var resetsAt = ReadResetsAt(section);

        return QuotaWindow.Create(DescribeWindow(key, length), used, resetsAt, length);
    }

    private static DateTimeOffset? ReadResetsAt(JsonElement section)
    {
        // Newer Codex builds write an absolute epoch; older ones write a relative countdown.
        if (section.TryGetProperty("resets_at", out var resetsAt)
            && resetsAt.ValueKind == JsonValueKind.Number
            && resetsAt.TryGetInt64(out var epochSeconds)
            && epochSeconds > 0)
        {
            return DateTimeOffset.FromUnixTimeSeconds(epochSeconds);
        }

        if (section.TryGetProperty("resets_in_seconds", out var resetsIn)
            && resetsIn.ValueKind == JsonValueKind.Number
            && resetsIn.TryGetInt64(out var seconds)
            && seconds >= 0)
        {
            return DateTimeOffset.UtcNow.AddSeconds(seconds);
        }

        return null;
    }

    /// <summary>Names the window from its length, falling back to primary/secondary.</summary>
    internal static string DescribeWindow(string key, TimeSpan? length)
    {
        if (length is { } span)
        {
            if (Math.Abs(span.TotalMinutes - 300) < 1)
            {
                return "5 hour";
            }

            if (Math.Abs(span.TotalMinutes - 10080) < 1)
            {
                return "Weekly";
            }

            if (span.TotalHours >= 24)
            {
                return $"{(int)Math.Round(span.TotalDays)} day";
            }

            if (span.TotalMinutes >= 60)
            {
                return $"{(int)Math.Round(span.TotalHours)} hour";
            }

            return $"{(int)Math.Round(span.TotalMinutes)} min";
        }

        return key == "primary" ? "Short window" : "Long window";
    }
}
