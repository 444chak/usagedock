namespace UsageDock.Services;

/// <summary>
/// Every path the app reads or writes. Nothing here ever holds a secret.
///
/// The two roots are resolved defensively rather than trusted, because
/// <see cref="Environment.GetFolderPath(Environment.SpecialFolder)"/> returns an empty string
/// when the shell cannot resolve a folder, which happens to a process launched early at Windows
/// login. An empty root makes every path below it relative, so the app quietly reads and writes
/// next to its working directory, which for an autostarted process is a place it cannot write to.
/// Logging swallows its own errors, so the symptom is an app that runs and says nothing at all.
/// </summary>
public static class AppPaths
{
    /// <summary>
    /// Set this to redirect everything the app writes. The test suite uses it so a test run does
    /// not scribble into the log and cache of a real installation, and it also allows a portable
    /// install that keeps its data next to the executable.
    /// </summary>
    public const string DataDirectoryVariable = "USAGEDOCK_DATA_DIR";

    /// <summary>Roaming application data, guaranteed to be an absolute path.</summary>
    public static string RoamingAppData { get; } = ResolveRoamingAppData();

    /// <summary>The user profile folder, guaranteed to be an absolute path.</summary>
    public static string UserProfile { get; } = ResolveUserProfile();

    public static string DataDirectory { get; } = ResolveDataDirectory();

    /// <summary>
    /// How each root was resolved, so a startup line can show it. When something goes wrong at
    /// login, the first useful question is which folder the app actually decided on.
    /// </summary>
    public static string Describe() =>
        $"data={DataDirectory}, appdata={RoamingAppData}, profile={UserProfile}";

    internal static string ResolveRoamingAppData() => FirstUsable(
        () => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        () => Environment.GetEnvironmentVariable("APPDATA"),
        () => CombineIfRooted(Environment.GetEnvironmentVariable("USERPROFILE"), "AppData", "Roaming"),
        () => CombineIfRooted(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "Roaming"));

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

    /// <summary>Directory holding one small activity file per provider, written by hooks.</summary>
    public static string ActivityDirectory => Path.Combine(DataDirectory, "activity");

    public static string ActivityFile(string providerKey) =>
        Path.Combine(ActivityDirectory, providerKey + ".json");

    public static string ClaudeHome => Path.Combine(UserProfile, ".claude");

    public static string ClaudeCredentialsFile => Path.Combine(ClaudeHome, ".credentials.json");

    public static string ClaudeSettingsFile => Path.Combine(ClaudeHome, "settings.json");

    /// <summary>
    /// Usage history written by Claude Desktop. Read only, and the only Claude quota source on a
    /// machine that has the desktop app but not the standalone CLI.
    /// </summary>
    public static string ClaudeDesktopUsageFile =>
        Path.Combine(RoamingAppData, "Claude", "plan-usage-history.json");

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
