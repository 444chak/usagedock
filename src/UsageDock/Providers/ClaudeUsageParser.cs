using System.Text.Json;
using UsageDock.Models;

namespace UsageDock.Providers;

/// <summary>
/// Parses the payload of the Claude OAuth usage endpoint into quota windows.
/// Unknown or missing sections are skipped rather than treated as errors, so a shape change
/// upstream degrades to fewer windows instead of a hard failure.
/// </summary>
public static class ClaudeUsageParser
{
    private static readonly (string Key, string Label)[] KnownWindows =
    [
        ("five_hour", "5 hour"),
        ("seven_day", "Weekly"),
        ("seven_day_opus", "Weekly (Opus)")
    ];

    public static IReadOnlyList<QuotaWindow> Parse(string json)
    {
        var windows = new List<QuotaWindow>();

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return windows;
            }

            foreach (var (key, label) in KnownWindows)
            {
                if (root.TryGetProperty(key, out var section)
                    && section.ValueKind == JsonValueKind.Object
                    && TryReadWindow(section, label) is { } window)
                {
                    windows.Add(window);
                }
            }
        }
        catch (JsonException)
        {
            return [];
        }

        return windows;
    }

    private static QuotaWindow? TryReadWindow(JsonElement section, string label)
    {
        if (!TryReadUtilization(section, out var used))
        {
            return null;
        }

        return QuotaWindow.Create(label, used, TryReadResetsAt(section));
    }

    private static bool TryReadUtilization(JsonElement section, out double percent)
    {
        percent = 0d;

        // "utilization" is the documented field; the others are defensive fallbacks.
        foreach (var name in (string[])["utilization", "used_percent", "usedPercent", "percent_used"])
        {
            if (section.TryGetProperty(name, out var element)
                && element.ValueKind == JsonValueKind.Number
                && element.TryGetDouble(out var value))
            {
                percent = QuotaWindow.Clamp(value);
                return true;
            }
        }

        return false;
    }

    private static DateTimeOffset? TryReadResetsAt(JsonElement section)
    {
        foreach (var name in (string[])["resets_at", "resetsAt", "reset_at"])
        {
            if (!section.TryGetProperty(name, out var element))
            {
                continue;
            }

            switch (element.ValueKind)
            {
                case JsonValueKind.String
                    when DateTimeOffset.TryParse(element.GetString(), out var parsed):
                    return parsed.ToUniversalTime();

                // Epoch seconds are also accepted, matching the Codex convention.
                case JsonValueKind.Number when element.TryGetInt64(out var epochSeconds) && epochSeconds > 0:
                    return DateTimeOffset.FromUnixTimeSeconds(epochSeconds);
            }
        }

        return null;
    }
}
