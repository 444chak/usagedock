using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using UsageDock.Models;
using UsageDock.Services;

namespace UsageDock.Providers;

/// <summary>What one <c>account/rateLimits/read</c> exchange with the Codex app server produced.</summary>
public sealed record CodexCliOutcome(string? Response, string? Problem)
{
    public static CodexCliOutcome Failed(string problem) => new(null, problem);
}

/// <summary>Asks Codex for the live rate limits. An interface so tests never spawn it.</summary>
public interface ICodexCliUsage
{
    /// <summary>Whether a Codex executable was found.</summary>
    bool IsAvailable { get; }

    Task<CodexCliOutcome> GetRateLimitsAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Live Codex quotas through the Codex app server.
///
/// The session rollouts used to be enough: every turn ran locally and recorded the limits the
/// server reported. On 2026-11-12 the Codex desktop app was replaced by the build merged with
/// ChatGPT, which runs its conversations on OpenAI's side and writes no rollout at all, so a day
/// of use in the app left the dock on a reading from the day before.
///
/// <c>codex app-server</c> is the JSON-RPC interface the desktop app and the IDE extensions talk
/// to, and it answers <c>account/rateLimits/read</c> with the same windows the rollouts carried.
/// It signs the request with the login Codex keeps in <c>~/.codex</c>, so the dock never reads,
/// holds or writes a credential: it starts a process and reads percentages back.
/// </summary>
public sealed class CodexCli : ICodexCliUsage
{
    public static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(30);

    private const string InitializeId = "usagedock-init";
    private const string RateLimitsId = "usagedock-rate-limits";

    private const string PackagesKey =
        @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";

    public bool IsAvailable => FindExecutable() is not null;

    /// <summary>
    /// The copy the desktop app ships first, since the Store keeps it current, then the npm
    /// install, then PATH. They are the same program; the login lives in <c>~/.codex</c>.
    /// </summary>
    public static string? FindExecutable() =>
        FromDesktopPackage() ?? FromNpm() ?? FromPath();

    /// <summary>
    /// <c>C:\Program Files\WindowsApps</c> cannot be listed without administrator rights, but the
    /// package repository in the user's registry hive names each installed package's folder.
    /// </summary>
    private static string? FromDesktopPackage()
    {
        try
        {
            using var packages = Registry.CurrentUser.OpenSubKey(PackagesKey);
            if (packages is null)
            {
                return null;
            }

            return packages.GetSubKeyNames()
                .Where(static name => name.StartsWith("OpenAI.Codex_", StringComparison.OrdinalIgnoreCase))
                .Select(name =>
                {
                    using var package = packages.OpenSubKey(name);
                    var root = package?.GetValue("PackageRootFolder") as string;
                    var exe = AppPaths.IsUsable(root) ? Path.Combine(root!, "app", "resources", "codex.exe") : null;
                    return (Version: PackageVersion(name), Exe: exe);
                })
                .Where(static candidate => candidate.Exe is not null && File.Exists(candidate.Exe))
                .OrderByDescending(static candidate => candidate.Version)
                .Select(static candidate => candidate.Exe)
                .FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }

    /// <summary><c>OpenAI.Codex_26.1106.6628.0_x64__2p2nqsd0c76g0</c> carries its version second.</summary>
    internal static Version PackageVersion(string packageFullName)
    {
        var parts = packageFullName.Split('_');
        return parts.Length > 1 && Version.TryParse(parts[1], out var version) ? version : new Version(0, 0);
    }

    /// <summary>Where <c>npm install -g @openai/codex</c> unpacks the native binary.</summary>
    private static string? FromNpm()
    {
        var (package, triple) = RuntimeInformation.OSArchitecture == Architecture.Arm64
            ? ("codex-win32-arm64", "aarch64-pc-windows-msvc")
            : ("codex-win32-x64", "x86_64-pc-windows-msvc");

        var codex = Path.Combine(AppPaths.RoamingAppData, "npm", "node_modules", "@openai", "codex");

        foreach (var vendor in (string[])
                 [
                     Path.Combine(codex, "node_modules", "@openai", package, "vendor"),
                     Path.Combine(codex, "vendor")
                 ])
        {
            var candidate = Path.Combine(vendor, triple, "bin", "codex.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string? FromPath()
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim('"'), "codex.exe");
                if (AppPaths.IsUsable(candidate) && File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (ArgumentException)
            {
            }
        }

        return null;
    }

    public async Task<CodexCliOutcome> GetRateLimitsAsync(CancellationToken cancellationToken)
    {
        var exe = FindExecutable();
        if (exe is null)
        {
            return CodexCliOutcome.Failed("Codex not found");
        }

        // No project lives here, so no project config is picked up from the working directory.
        var workingDirectory = Path.Combine(AppPaths.UserProfile, ".usagedock");

        var start = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = workingDirectory
        };
        start.ArgumentList.Add("app-server");

        // A dock started from a Codex shell inherits that session's variables. CODEX_HOME is the
        // person's own choice of where Codex lives and stays.
        foreach (var key in start.Environment.Keys.ToList())
        {
            if (key.StartsWith("CODEX_", StringComparison.OrdinalIgnoreCase)
                && !key.Equals("CODEX_HOME", StringComparison.OrdinalIgnoreCase))
            {
                start.Environment.Remove(key);
            }
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ResponseTimeout);

        Process? process = null;

        try
        {
            Directory.CreateDirectory(workingDirectory);
            process = Process.Start(start);
            if (process is null)
            {
                return CodexCliOutcome.Failed("Codex did not start");
            }

            var errors = process.StandardError.ReadToEndAsync(timeout.Token);

            // The server handles messages in order, so the request may follow the handshake
            // without waiting for its answer.
            foreach (var message in (object[])
                     [
                         new
                         {
                             method = "initialize",
                             id = InitializeId,
                             @params = new { clientInfo = new { name = "usagedock", title = "UsageDock", version = "1.0" } }
                         },
                         new { method = "initialized" },
                         new { method = "account/rateLimits/read", id = RateLimitsId }
                     ])
            {
                await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(message).AsMemory(), timeout.Token).ConfigureAwait(false);
            }

            await process.StandardInput.FlushAsync(timeout.Token).ConfigureAwait(false);

            while (await process.StandardOutput.ReadLineAsync(timeout.Token).ConfigureAwait(false) is { } line)
            {
                if (CodexCliRateLimitParser.IsAnswerTo(line, RateLimitsId))
                {
                    return new CodexCliOutcome(line, null);
                }
            }

            var detail = await SafeFirstLineAsync(errors).ConfigureAwait(false);
            return CodexCliOutcome.Failed(detail is null
                ? "Codex exited without an answer"
                : $"Codex exited without an answer: {detail}");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return CodexCliOutcome.Failed($"Codex did not answer within {ResponseTimeout.TotalSeconds:0}s");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Error("Codex rate limit request failed", ex);
            return CodexCliOutcome.Failed("Codex could not be run");
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

    /// <summary>The app server exits when its input closes; one that does not is killed.</summary>
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

/// <summary>Reads the answer to an <c>account/rateLimits/read</c> request.</summary>
public static class CodexCliRateLimitParser
{
    public sealed record Result(IReadOnlyList<QuotaWindow> Windows, string? Plan, string? Error);

    internal static bool IsAnswerTo(string line, string id)
    {
        if (!line.Contains(id, StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(line);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("id", out var answered)
                && answered.ValueKind == JsonValueKind.String
                && answered.ValueEquals(id);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// <c>{"id":"…","result":{"rateLimits":{"primary":{"usedPercent":3,"windowDurationMins":300,
    /// "resetsAt":1794595644},"secondary":{…},"planType":"plus"}}}</c>, or <c>{"id":"…","error":
    /// {"message":"…"}}</c>. Returns null for anything unrecognisable.
    /// </summary>
    public static Result? Parse(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
            {
                var message = error.TryGetProperty("message", out var text) && text.ValueKind == JsonValueKind.String
                    ? text.GetString()
                    : null;
                return new Result([], null, message ?? "error");
            }

            if (!root.TryGetProperty("result", out var result)
                || result.ValueKind != JsonValueKind.Object
                || !result.TryGetProperty("rateLimits", out var limits)
                || limits.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var windows = new List<QuotaWindow>();
            foreach (var key in (string[])["primary", "secondary"])
            {
                if (ReadWindow(limits, key) is { } window)
                {
                    windows.Add(window);
                }
            }

            var plan = limits.TryGetProperty("planType", out var planType) && planType.ValueKind == JsonValueKind.String
                ? planType.GetString()
                : null;

            return new Result(windows, plan, null);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static QuotaWindow? ReadWindow(JsonElement limits, string key)
    {
        if (!limits.TryGetProperty(key, out var section)
            || section.ValueKind != JsonValueKind.Object
            || !section.TryGetProperty("usedPercent", out var usedElement)
            || usedElement.ValueKind != JsonValueKind.Number
            || !usedElement.TryGetDouble(out var used))
        {
            return null;
        }

        TimeSpan? length = section.TryGetProperty("windowDurationMins", out var minutesElement)
                           && minutesElement.ValueKind == JsonValueKind.Number
                           && minutesElement.TryGetDouble(out var minutes)
                           && minutes > 0
            ? TimeSpan.FromMinutes(minutes)
            : null;

        DateTimeOffset? resetsAt = section.TryGetProperty("resetsAt", out var resetsElement)
                                   && resetsElement.ValueKind == JsonValueKind.Number
                                   && resetsElement.TryGetInt64(out var epochSeconds)
                                   && epochSeconds > 0
                                   && epochSeconds <= DateTimeOffset.MaxValue.ToUnixTimeSeconds()
            ? DateTimeOffset.FromUnixTimeSeconds(epochSeconds)
            : null;

        return QuotaWindow.Create(CodexRateLimitParser.DescribeWindow(key, length), used, resetsAt, length);
    }
}
