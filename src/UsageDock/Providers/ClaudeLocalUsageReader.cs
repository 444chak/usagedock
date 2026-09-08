using System.Text.Json;
using UsageDock.Models;
using UsageDock.Services;

namespace UsageDock.Providers;

/// <summary>
/// Reads Claude quotas from the usage history Claude Desktop keeps locally.
///
/// This is the counterpart of the Codex approach: no token, no network, nothing to be throttled.
/// It matters because Claude Desktop keeps its session in an encrypted Electron store rather
/// than in .credentials.json, so on a machine without the standalone CLI there is no bearer
/// token to call the usage endpoint with, but the numbers are sitting on disk anyway.
///
/// The file is a list of samples, roughly one every fifteen minutes:
/// <c>{ "t": &lt;epoch ms&gt;, "org": "...", "u": { "fh": &lt;5h %&gt;, "sd": &lt;7d %&gt; } }</c>.
/// The org identifier is deliberately never read.
/// </summary>
public static class ClaudeLocalUsageReader
{
    public static readonly TimeSpan FiveHourWindow = TimeSpan.FromHours(5);
    public static readonly TimeSpan SevenDayWindow = TimeSpan.FromDays(7);

    public sealed record Result(IReadOnlyList<QuotaWindow> Windows, DateTimeOffset SampledAt);

    private readonly record struct Sample(long TimestampMs, double FiveHour, double SevenDay);

    public static Result? Read(string? path = null, DateTimeOffset? nowUtc = null)
    {
        var text = JsonIo.TryReadAllText(path ?? AppPaths.ClaudeDesktopUsageFile);
        return text is null ? null : Parse(text, nowUtc ?? DateTimeOffset.UtcNow);
    }

    internal static Result? Parse(string json, DateTimeOffset nowUtc)
    {
        var samples = ReadSamples(json);
        if (samples.Count == 0)
        {
            return null;
        }

        var latest = samples[^1];
        var sampledAt = DateTimeOffset.FromUnixTimeMilliseconds(latest.TimestampMs);

        var windows = new List<QuotaWindow>
        {
            QuotaWindow.Create(
                "5 hour",
                latest.FiveHour,
                InferReset(samples, static s => s.FiveHour, FiveHourWindow, nowUtc),
                FiveHourWindow),
            QuotaWindow.Create(
                "Weekly",
                latest.SevenDay,
                InferReset(samples, static s => s.SevenDay, SevenDayWindow, nowUtc),
                SevenDayWindow)
        };

        return new Result(windows, sampledAt);
    }

    private static List<Sample> ReadSamples(string json)
    {
        var samples = new List<Sample>();

        try
        {
            using var document = JsonDocument.Parse(json);

            if (!document.RootElement.TryGetProperty("samples", out var array)
                || array.ValueKind != JsonValueKind.Array)
            {
                return samples;
            }

            foreach (var element in array.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object
                    || !element.TryGetProperty("t", out var t)
                    || t.ValueKind != JsonValueKind.Number
                    || !t.TryGetInt64(out var timestamp)
                    || timestamp <= 0
                    || !element.TryGetProperty("u", out var usage)
                    || usage.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                samples.Add(new Sample(
                    timestamp,
                    ReadPercent(usage, "fh"),
                    ReadPercent(usage, "sd")));
            }
        }
        catch (JsonException)
        {
            return [];
        }

        // The file is written in order, but sorting makes the reset inference independent of that.
        samples.Sort(static (a, b) => a.TimestampMs.CompareTo(b.TimestampMs));
        return samples;
    }

    private static double ReadPercent(JsonElement usage, string name) =>
        usage.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetDouble(out var percent)
            ? QuotaWindow.Clamp(percent)
            : 0d;

    /// <summary>
    /// Estimates when the current window rolls over.
    ///
    /// The file carries no reset timestamp, but usage only ever grows inside a window: it goes
    /// back to zero, or drops, exactly when a window rolls. Walking back to the start of the
    /// current run of non zero samples therefore locates the window, and the reset is one window
    /// length after it. The exact moment sits between two samples, so the midpoint is used and
    /// the answer is good to roughly half a sampling interval.
    ///
    /// Returns null when there is nothing to reset, when the run starts before the retained
    /// history, or when the estimate has already passed, rather than showing a made up value.
    /// </summary>
    private static DateTimeOffset? InferReset(
        List<Sample> samples,
        Func<Sample, double> selector,
        TimeSpan window,
        DateTimeOffset nowUtc)
    {
        if (selector(samples[^1]) <= 0d)
        {
            return null;
        }

        var index = samples.Count - 1;
        while (index > 0
               && selector(samples[index - 1]) > 0d
               && selector(samples[index - 1]) <= selector(samples[index]))
        {
            index--;
        }

        if (index == 0)
        {
            // The run began before the oldest sample we kept, so the start is unknown.
            return null;
        }

        var openedMs = (samples[index - 1].TimestampMs + samples[index].TimestampMs) / 2;
        var reset = DateTimeOffset.FromUnixTimeMilliseconds(openedMs) + window;

        return reset <= nowUtc ? null : reset;
    }
}
