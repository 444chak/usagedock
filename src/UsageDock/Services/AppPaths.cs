namespace UsageDock.Services;

/// <summary>
/// Every path the app reads or writes. Nothing here ever holds a secret.
///
/// Two things shape this class. First, <see cref="Environment.GetFolderPath(Environment.SpecialFolder)"/>
/// returns an empty string when the shell cannot resolve a folder, so every root is taken from
/// the first candidate that is fully qualified rather than trusted blindly.
///
/// Second, Claude Desktop from the Microsoft Store is an MSIX package, and MSIX virtualises the
/// three AppData folders for every process inside its container. That has two consequences that
/// each cost a day: the desktop app writes its usage history into the package's own
/// <c>LocalCache\Roaming</c>, not into the real <c>%APPDATA%</c>; and Claude Code, which runs
/// inside that same container, launches the hooks inside it too, so anything a hook writes under
/// AppData lands in the container copy, invisible to a dock started normally by Windows. The
/// profile root is not virtualised, which is why <c>.claude</c> and <c>.codex</c> are shared.
/// </summary>
public static class AppPaths
{
    /// <summary>
    /// Set this to redirect everything the app writes. The test suite uses it so a test run does
    /// not scribble into the log and cache of a real installation, and it also allows a portable
    /// install that keeps its data next to the executable.
    /// </summary>
    public const string DataDirectoryVariable = "USAGEDOCK_DATA_DIR";

    private const string UsageHistoryFileName = "plan-usage-history.json";

    /// <summary>Roaming application data, guaranteed to be an absolute path.</summary>
    public static string RoamingAppData { get; } = ResolveRoamingAppData();

    /// <summary>Local application data, guaranteed to be an absolute path.</summary>
    public static string LocalAppData { get; } = ResolveLocalAppData();

    /// <summary>The user profile folder, guaranteed to be an absolute path.</summary>
    public static string UserProfile { get; } = ResolveUserProfile();

    public static string DataDirectory { get; } = ResolveDataDirectory();

    /// <summary>
    /// How each root was resolved, so a startup line can show it. When something goes wrong at
    /// login, the first useful question is which folder the app actually decided on.
    /// </summary>
    public static string Describe() =>
        $"data={DataDirectory}, appdata={RoamingAppData}, profile={UserProfile}, claude={ClaudeDesktopUsageFile}, statusline={ClaudeStatusLineFile}";

    internal static string ResolveRoamingAppData() => FirstUsable(
        () => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        () => Environment.GetEnvironmentVariable("APPDATA"),
        () => CombineIfRooted(Environment.GetEnvironmentVariable("USERPROFILE"), "AppData", "Roaming"),
        () => CombineIfRooted(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "Roaming"));

    internal static string ResolveLocalAppData() => FirstUsable(
        () => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        () => Environment.GetEnvironmentVariable("LOCALAPPDATA"),
        () => CombineIfRooted(Environment.GetEnvironmentVariable("USERPROFILE"), "AppData", "Local"),
        () => CombineIfRooted(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "Local"));

    internal static string ResolveUserProfile() => FirstUsable(
        () => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        () => Environment.GetEnvironmentVariable("USERPROFILE"),
        () => CombineIfRooted(Environment.GetEnvironmentVariable("HOMEDRIVE"),
            Environment.GetEnvironmentVariable("HOMEPATH") ?? string.Empty));

    private static string ResolveDataDirectory()
    {
        var overridden = Environment.GetEnvironmentVariable(DataDirectoryVariable);

        if (IsUsable(overridden))
        {
            return overridden!;
        }

        return Path.Combine(RoamingAppData, "UsageDock");
    }

    /// <summary>Returns the first candidate that is a rooted path, or throws if none is.</summary>
    internal static string FirstUsable(params Func<string?>[] candidates)
    {
        foreach (var candidate in candidates)
        {
            string? value;

            try
            {
                value = candidate();
            }
            catch (Exception)
            {
                continue;
            }

            if (IsUsable(value))
            {
                return value!;
            }
        }

        // Every fallback failed, which means the environment is broken in a way this app cannot
        // paper over. Failing loudly beats writing into the working directory.
        throw new InvalidOperationException(
            "Could not resolve a usable user folder. Set " + DataDirectoryVariable + " to a path.");
    }

    /// <summary>
    /// A path is usable only if it is absolute. A relative one is exactly the failure mode this
    /// whole dance exists to prevent.
    /// </summary>
    internal static bool IsUsable(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            return Path.IsPathRooted(path) && Path.IsPathFullyQualified(path);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static string? CombineIfRooted(string? root, params string[] parts)
    {
        if (!IsUsable(root))
        {
            return null;
        }

        return Path.Combine([root!, .. parts]);
    }

    public static string ConfigFile => Path.Combine(DataDirectory, "config.json");

    public static string CacheFile => Path.Combine(DataDirectory, "cache.json");

    public static string LogDirectory => Path.Combine(DataDirectory, "logs");

    /// <summary>
    /// Directory holding one small activity file per provider, written by hooks and read by the
    /// dock.
    ///
    /// This is the one exchange between a process inside the Claude Desktop container and one
    /// outside it, so it must not live under AppData: a hook writing there from inside the
    /// container lands in the package's virtualised copy, and a dock started by Windows reads the
    /// real folder and sees nothing, forever. The profile root is not virtualised, so a dotfolder
    /// there, in the same spirit as .claude and .codex, is visible identically to both sides.
    /// </summary>
    public static string ActivityDirectory => Path.Combine(UserProfile, ".usagedock", "activity");

    public static string ActivityFile(string providerKey) =>
        Path.Combine(ActivityDirectory, providerKey + ".json");

    /// <summary>
    /// Latest quotas Claude Code passed to its status line. Written from inside the Claude Desktop
    /// container just like the activity files, so it lives under the profile root for the same
    /// reason.
    /// </summary>
    public static string ClaudeStatusLineFile => Path.Combine(UserProfile, ".usagedock", "claude-statusline.json");

    public static string ClaudeHome => Path.Combine(UserProfile, ".claude");

    public static string ClaudeCredentialsFile => Path.Combine(ClaudeHome, ".credentials.json");

    public static string ClaudeSettingsFile => Path.Combine(ClaudeHome, "settings.json");

    /// <summary>
    /// Usage history written by Claude Desktop, read only. Resolved on every access, because the
    /// desktop app can be the classic installer or the Store package, and the two keep the file
    /// in different places.
    /// </summary>
    public static string ClaudeDesktopUsageFile => ChooseUsageFile(
        ClaudeDesktopUsageCandidates().ToList(),
        File.Exists,
        p => File.GetLastWriteTimeUtc(p),
        p => Directory.Exists(Path.GetDirectoryName(p)));

    /// <summary>
    /// Where the usage history might be, most conventional first: the classic install writes to
    /// the real roaming folder; the Store package writes to its own virtualised copy under
    /// Packages. The publisher hash after the underscore is not hard coded, so a re-signed
    /// package still matches.
    /// </summary>
    internal static IEnumerable<string> ClaudeDesktopUsageCandidates()
    {
        yield return Path.Combine(RoamingAppData, "Claude", UsageHistoryFileName);

        var packages = Path.Combine(LocalAppData, "Packages");
        IEnumerable<string> packageDirs;

        try
        {
            packageDirs = Directory.Exists(packages)
                ? Directory.EnumerateDirectories(packages, "Claude_*").OrderBy(static d => d, StringComparer.OrdinalIgnoreCase)
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var dir in packageDirs)
        {
            yield return Path.Combine(dir, "LocalCache", "Roaming", "Claude", UsageHistoryFileName);
        }
    }

    /// <summary>
    /// Picks the candidate to use. An existing file always wins, the newest one when several
    /// exist, so an old classic install does not shadow a live Store one. Failing that, the first
    /// candidate whose folder already exists is the one a watcher can attach to. Failing that, the
    /// first candidate, so callers always get a path to try.
    /// </summary>
    internal static string ChooseUsageFile(
        IReadOnlyList<string> candidates,
        Func<string, bool> fileExists,
        Func<string, DateTime> lastWriteUtc,
        Func<string, bool> folderExists)
    {
        if (candidates.Count == 0)
        {
            throw new ArgumentException("At least one candidate is required.", nameof(candidates));
        }

        string? newest = null;
        var newestTime = DateTime.MinValue;

        foreach (var candidate in candidates)
        {
            if (!fileExists(candidate))
            {
                continue;
            }

            var time = lastWriteUtc(candidate);
            if (newest is null || time > newestTime)
            {
                newest = candidate;
                newestTime = time;
            }
        }

        if (newest is not null)
        {
            return newest;
        }

        foreach (var candidate in candidates)
        {
            if (folderExists(candidate))
            {
                return candidate;
            }
        }

        return candidates[0];
    }

    public static string CodexHome => Path.Combine(UserProfile, ".codex");

    public static string CodexAuthFile => Path.Combine(CodexHome, "auth.json");

    public static string CodexSessionsDirectory => Path.Combine(CodexHome, "sessions");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(LogDirectory);
        Directory.CreateDirectory(ActivityDirectory);
    }
}
