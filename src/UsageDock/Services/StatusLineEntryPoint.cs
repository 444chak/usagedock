using System.Text;
using UsageDock.Providers;

namespace UsageDock.Services;

/// <summary>
/// Handles the <c>--usagedock-statusline</c> invocation. Claude Code runs this on every status
/// line refresh, so like the hooks it must stay cheap, load no WPF, and never fail: a broken
/// status line is visible on every single turn.
/// </summary>
public static class StatusLineEntryPoint
{
    public static bool TryHandle(string[] args)
    {
        if (!args.Contains(StatusLineInstaller.Marker, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        var tee = args.Contains(StatusLineInstaller.TeeFlag, StringComparer.OrdinalIgnoreCase);

        try
        {
            var input = ReadAll(Console.OpenStandardInput());

            // When chained in front of another status line, hand it the payload first and
            // byte for byte: it is waiting on this pipe, and re-encoding would mangle any
            // non ASCII path in it.
            if (tee)
            {
                using var stdout = Console.OpenStandardOutput();
                stdout.Write(input);
            }

            var limits = ClaudeStatusLineFeed.ExtractRateLimits(input);

            if (!tee)
            {
                using var stdout = Console.OpenStandardOutput();
                stdout.Write(Encoding.UTF8.GetBytes(ClaudeStatusLineFeed.Summarize(limits)));
            }

            if (limits is { } found)
            {
                ClaudeStatusLineFeed.Store(found, DateTimeOffset.UtcNow);
            }
        }
        catch
        {
            // Swallowed on purpose, see the class comment.
        }

        return true;
    }

    private static byte[] ReadAll(Stream stream)
    {
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
