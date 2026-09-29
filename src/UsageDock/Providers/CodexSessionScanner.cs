using UsageDock.Services;

namespace UsageDock.Providers;

/// <summary>
/// Locates the most recently written Codex rollout files and pulls the newest rate limit
/// snapshot out of them.
///
/// File times cannot be trusted to order the sessions. Codex keeps a rollout open for as long
/// as its session lives, and Windows leaves the last write time at the moment the file was
/// created until the handle closes, even when asked through the handle itself. A session open
/// all afternoon therefore looks older than a short one started after it, and picking the
/// "newest" file showed a limit hours out of date while the real one sat at 100%. Every line
/// carries its own timestamp, so the records are compared by that instead.
/// </summary>
public sealed class CodexSessionScanner
{
    /// <summary>
    /// How many recent session files to inspect. A long lived session still ranks by its
    /// creation time, so this has to reach back past the short ones opened after it.
    /// </summary>
    private const int MaxFilesToInspect = 12;

    private readonly string _sessionsDirectory;

    public CodexSessionScanner(string? sessionsDirectory = null) =>
        _sessionsDirectory = sessionsDirectory ?? AppPaths.CodexSessionsDirectory;

    public sealed record Scan(
        CodexRateLimitParser.Result? RateLimits,
        DateTimeOffset? NewestActivityUtc);

    public Scan Run()
    {
        CodexRateLimitParser.Result? newestLimits = null;
        DateTimeOffset? newestLimitsAt = null;
        DateTimeOffset? newestActivity = null;

        foreach (var file in RecentRolloutFiles())
        {
            var written = new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero);
            var tail = ReadTail(file.FullName);

            var activity = Max(written, tail.LastLineAt);
            newestActivity = Max(newestActivity, activity);

            if (tail.RateLimits is { } limits)
            {
                // A record without a timestamp can only be dated by its file.
                var at = limits.RecordedAt ?? written;
                if (newestLimitsAt is null || at > newestLimitsAt)
                {
                    newestLimits = limits;
                    newestLimitsAt = at;
                }
            }
        }

        return new Scan(newestLimits, newestActivity);
    }

    /// <summary>Newest first, by the file time, which is only a rough order. See the class notes.</summary>
    internal List<FileInfo> RecentRolloutFiles()
    {
        try
        {
            if (!Directory.Exists(_sessionsDirectory))
            {
                return [];
            }

            return new DirectoryInfo(_sessionsDirectory)
                .EnumerateFiles("rollout-*.jsonl", SearchOption.AllDirectories)
                .OrderByDescending(static f => f.LastWriteTimeUtc)
                .Take(MaxFilesToInspect)
                .ToList();
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    internal readonly record struct Tail(CodexRateLimitParser.Result? RateLimits, DateTimeOffset? LastLineAt);

    /// <summary>
    /// Scans the tail of one rollout file backwards for the newest rate limits, and notes when
    /// the file was really last written to.
    /// </summary>
    internal static Tail ReadTail(string path)
    {
        var lines = FileTail.ReadLines(path);
        DateTimeOffset? lastLineAt = null;

        for (var i = lines.Length - 1; i >= 0; i--)
        {
            lastLineAt ??= CodexRateLimitParser.ReadLineTimestamp(lines[i]);

            if (CodexRateLimitParser.ParseLine(lines[i]) is { Windows.Count: > 0 } result)
            {
                return new Tail(result, lastLineAt ?? result.RecordedAt);
            }
        }

        return new Tail(null, lastLineAt);
    }

    private static DateTimeOffset? Max(DateTimeOffset? a, DateTimeOffset? b) =>
        a is null ? b : b is null ? a : a > b ? a : b;
}
