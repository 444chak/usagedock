using System.Diagnostics;
using System.Text;
using System.Text.Json;
using UsageDock.Models;
using UsageDock.Services;

namespace UsageDock.Providers;

/// <summary>What one <c>get_usage</c> exchange with the Claude Code CLI produced.</summary>
public sealed record ClaudeCliOutcome(string? Response, string? Problem)
{
    public static ClaudeCliOutcome Failed(string problem) => new(null, problem);
}

/// <summary>Asks the Claude Code CLI for the plan usage. An interface so tests never spawn it.</summary>
public interface IClaudeCliUsage
{
    /// <summary>Whether the dedicated CLI login exists. Presence only, nothing is read from it.</summary>
    bool IsSignedIn { get; }

    Task<ClaudeCliOutcome> GetUsageAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Live Claude quotas through the Claude Code CLI, for machines where Claude Desktop holds the
/// only session.
///
/// Claude Desktop shows usage from claude.ai with its own session cookie and keeps nothing usable
/// on disk. The CLI it runs for the Code tab gets its token from the desktop app in memory and
/// shares <c>~/.claude</c> with the standalone CLI, whose token fields were found blank on
/// 2026-11-08 with the refresh token still months from expiry, most likely rewritten by that
/// shared CLI. A separate login, in a config folder of its own that nothing else touches, is the
/// way around both.
///
/// The CLI answers a <c>get_usage</c> control request on its stream-json interface with the very
/// windows the usage endpoint returns, and it refreshes its own token while doing so. The dock
/// therefore never reads, holds or writes a credential: it starts a process and reads percentages
/// back. Anthropic marks the request experimental, so the parser degrades to nothing on any shape
/// it does not recognise, and the desktop history remains the fallback.
/// </summary>
public sealed class ClaudeCli : IClaudeCliUsage
{
    /// <summary>Generous: the first start after an update unpacks a few hundred megabytes.</summary>
    public static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(45);

    private const string RequestId = "usagedock-usage";

    public bool IsSignedIn =>
        File.Exists(Path.Combine(AppPaths.ClaudeCliConfigDirectory, ".credentials.json"))
        && FindExecutable() is not null;

    /// <summary>
    /// The standalone install first, then PATH, then the copy Claude Desktop ships for its Code
    /// tab, newest version first. All are the same program; the login lives in the config folder,
    /// not in the executable.
    /// </summary>
    public static string? FindExecutable()
    {
        var standalone = Path.Combine(AppPaths.UserProfile, ".local", "bin", "claude.exe");
        if (File.Exists(standalone))
        {
            return standalone;
        }

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim('"'), "claude.exe");
                if (AppPaths.IsUsable(candidate) && File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (ArgumentException)
            {
            }
        }

        // Claude-Data\<install>\claude-code\<version>\<build>\claude.exe. Walked level by level:
        // this runs on every poll, and a recursive search would crawl the browser caches beside it.
        try
        {
            var root = Path.Combine(AppPaths.LocalAppData, "Claude-Data");
            if (!Directory.Exists(root))
            {
                return null;
            }

            return Directory.EnumerateDirectories(root)
                .Select(static install => Path.Combine(install, "claude-code"))
                .Where(Directory.Exists)
                .SelectMany(Directory.EnumerateDirectories)
                .SelectMany(Directory.EnumerateDirectories)
                .Select(static build => Path.Combine(build, "claude.exe"))
                .Where(File.Exists)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Opens a console running <c>claude auth login</c> against the dedicated config folder. The
    /// person signs in in their browser; the dock never sees any of it.
    /// </summary>
    public static bool StartLogin()
    {
        var exe = FindExecutable();
        if (exe is null)
        {
            return false;
        }

        try
        {
            Directory.CreateDirectory(AppPaths.ClaudeCliConfigDirectory);

            // cmd /k keeps the window open on the outcome; the CLI alone would vanish with it.
            var start = new ProcessStartInfo("cmd.exe", $"/k \"\"{exe}\" auth login\"")
            {
                UseShellExecute = false,
                CreateNoWindow = false,
                WorkingDirectory = AppPaths.ClaudeCliConfigDirectory
            };
            PrepareEnvironment(start);

            using var _ = Process.Start(start);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("Could not start the Claude Code CLI login", ex);
            return false;
        }
    }

    public async Task<ClaudeCliOutcome> GetUsageAsync(CancellationToken cancellationToken)
    {
        var exe = FindExecutable();
        if (exe is null)
        {
            return ClaudeCliOutcome.Failed("Claude Code CLI not found");
        }

        var start = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,

            // The config folder holds no project settings, so none are picked up from the cwd.
            WorkingDirectory = AppPaths.ClaudeCliConfigDirectory
        };

        foreach (var argument in (string[])
                 [
                     "-p",
                     "--input-format", "stream-json",
                     "--output-format", "stream-json",
                     "--verbose",
                     "--no-session-persistence",
                     "--strict-mcp-config",

                     // UsageDock's own hooks would otherwise report this probe as Claude working.
                     "--settings", """{"disableAllHooks":true}"""
                 ])
        {
            start.ArgumentList.Add(argument);
        }

        PrepareEnvironment(start);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ResponseTimeout);

        Process? process = null;

        try
        {
            // It doubles as the working directory, and a missing one fails the start outright.
            Directory.CreateDirectory(AppPaths.ClaudeCliConfigDirectory);
            process = Process.Start(start);
            if (process is null)
            {
                return ClaudeCliOutcome.Failed("Claude Code CLI did not start");
            }

            var errors = process.StandardError.ReadToEndAsync(timeout.Token);

            var request = JsonSerializer.Serialize(new
            {
                type = "control_request",
                request_id = RequestId,
                request = new { subtype = "get_usage", skip_behaviors = true }
            });

            await process.StandardInput.WriteLineAsync(request.AsMemory(), timeout.Token).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(timeout.Token).ConfigureAwait(false);

            while (await process.StandardOutput.ReadLineAsync(timeout.Token).ConfigureAwait(false) is { } line)
            {
                if (IsOurResponse(line))
                {
                    return new ClaudeCliOutcome(line, null);
                }
            }

            var detail = await SafeFirstLineAsync(errors).ConfigureAwait(false);
            return ClaudeCliOutcome.Failed(detail is null
                ? $"Claude Code CLI exited without an answer"
                : $"Claude Code CLI exited without an answer: {detail}");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ClaudeCliOutcome.Failed($"Claude Code CLI did not answer within {ResponseTimeout.TotalSeconds:0}s");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Error("Claude Code CLI usage request failed", ex);
            return ClaudeCliOutcome.Failed("Claude Code CLI could not be run");
        }
        finally
        {
            if (process is not null)
            {
                await StopAsync(process).ConfigureAwait(false);
                process.Dispose();
            }
        }
    }

    /// <summary>
    /// Runs the CLI as if started fresh. A dock launched from a Claude Code shell inherits the
    /// desktop session's variables, which would point the child at the host's auth and session;
    /// the dedicated folder is what makes the login its own. Updates are left to the person's own
    /// use of the CLI rather than triggered every five minutes from here.
    /// </summary>
    private static void PrepareEnvironment(ProcessStartInfo start)
    {
        foreach (var key in start.Environment.Keys.ToList())
        {
            if (key.StartsWith("CLAUDE", StringComparison.OrdinalIgnoreCase)
                || key.StartsWith("ANTHROPIC", StringComparison.OrdinalIgnoreCase))
            {
                start.Environment.Remove(key);
            }
        }

        start.Environment["CLAUDE_CONFIG_DIR"] = AppPaths.ClaudeCliConfigDirectory;
        start.Environment["DISABLE_AUTOUPDATER"] = "1";
    }

    private static bool IsOurResponse(string line)
    {
        if (!line.Contains(RequestId, StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(line);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("type", out var type)
                && type.ValueEquals("control_response")
                && document.RootElement.TryGetProperty("response", out var response)
                && response.ValueKind == JsonValueKind.Object
                && response.TryGetProperty("request_id", out var id)
                && id.ValueEquals(RequestId);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static async Task<string?> SafeFirstLineAsync(Task<string> errors)
    {
        try
        {
            var text = await errors.ConfigureAwait(false);
            var first = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
            return first is null ? null : first.Length <= 160 ? first : first[..160];
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Closing stdin ends a stream-json session; a CLI that ignores it is killed.</summary>
    private static async Task StopAsync(Process process)
    {
        try
        {
            process.StandardInput.Close();
        }
        catch (Exception)
        {
        }

        try
        {
            using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(grace.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception)
            {
            }
        }
    }
}

/// <summary>Reads the answer to a <c>get_usage</c> control request.</summary>
public static class ClaudeCliUsageParser
{
    /// <param name="Available">False when the CLI has no plan limits to report, usually because it is not signed in.</param>
    public sealed record Result(bool Available, IReadOnlyList<QuotaWindow> Windows, string? Plan, string? Error);

    /// <summary>
    /// <c>{"type":"control_response","response":{"subtype":"success","request_id":"…","response":
    /// {"subscription_type":"pro","rate_limits_available":true,"rate_limits":{"five_hour":{…},…}}}}</c>.
    /// The windows share the usage endpoint's shape, so <see cref="ClaudeUsageParser"/> reads them.
    /// Returns null for anything unrecognisable.
    /// </summary>
    public static Result? Parse(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);

            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("response", out var envelope)
                || envelope.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (envelope.TryGetProperty("subtype", out var subtype) && subtype.ValueEquals("error"))
            {
                var error = envelope.TryGetProperty("error", out var message) && message.ValueKind == JsonValueKind.String
                    ? message.GetString()
                    : null;
                return new Result(false, [], null, error ?? "error");
            }

            if (!envelope.TryGetProperty("response", out var body) || body.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var plan = body.TryGetProperty("subscription_type", out var type) && type.ValueKind == JsonValueKind.String
                ? type.GetString()
                : null;

            var available = !body.TryGetProperty("rate_limits_available", out var flag) || flag.ValueKind != JsonValueKind.False;

            var windows = body.TryGetProperty("rate_limits", out var limits) && limits.ValueKind == JsonValueKind.Object
                ? ClaudeUsageParser.Parse(limits.GetRawText())
                : [];

            return new Result(available && windows.Count > 0, windows, plan, null);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
