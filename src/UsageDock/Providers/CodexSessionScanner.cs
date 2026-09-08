using UsageDock.Services;

namespace UsageDock.Providers;

/// <summary>
/// Locates the most recently written Codex rollout files and pulls the newest rate limit
/// snapshot out of them.
/// </summary>
public sealed class CodexSessionScanner
{
    /// <summary>How many recent session files to inspect before giving up.</summary>
    private const int MaxFilesToInspect = 8;

    private readonly string _sessionsDirectory;

    public CodexSessionScanner(string? sessionsDirectory = null) =>
        _sessionsDirectory = sessionsDirectory ?? AppPaths.CodexSessionsDirectory;

    public sealed record Scan(
        CodexRateLimitParser.Result? RateLimits,
        DateTimeOffset? NewestActivityUtc);

    public Scan Run()
    {
        var files = RecentRolloutFiles();
        if (files.Count == 0)
        {
            return new Scan(null, null);
        }

        var newestActivity = new DateTimeOffset(files[0].LastWriteTimeUtc, TimeSpan.Zero);

        foreach (var file in files)
        {
            if (FindLatestRateLimits(file.FullName) is { } result)
            {
                return new Scan(result, newestActivity);
            }
        }

        return new Scan(null, newestActivity);
    }

    /// <summary>Newest first.</summary>
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

    /// <summary>Scans the tail of one rollout file backwards for the newest rate limits.</summary>
    internal static CodexRateLimitParser.Result? FindLatestRateLimits(string path)
    {
        var lines = FileTail.ReadLines(path);

        for (var i = lines.Length - 1; i >= 0; i--)
        {
            if (CodexRateLimitParser.ParseLine(lines[i]) is { Windows.Count: > 0 } result)
            {
                return result;
            }
        }

        return null;
    }
}
