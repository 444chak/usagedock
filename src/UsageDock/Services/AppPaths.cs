namespace UsageDock.Services;

/// <summary>
/// Every path the app writes to. Nothing here ever holds a secret.
/// </summary>
public static class AppPaths
{
    /// <summary>
    /// Set this to redirect everything the app writes. The test suite uses it so a test run does
    /// not scribble into the log and cache of a real installation, and it also allows a portable
    /// install that keeps its data next to the executable.
    /// </summary>
    public const string DataDirectoryVariable = "USAGEDOCK_DATA_DIR";

    public static string DataDirectory { get; } = ResolveDataDirectory();

    private static string ResolveDataDirectory()
    {
        var overridden = Environment.GetEnvironmentVariable(DataDirectoryVariable);

        if (!string.IsNullOrWhiteSpace(overridden))
        {
            return overridden;
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "UsageDock");
    }

    public static string ConfigFile => Path.Combine(DataDirectory, "config.json");

    public static string CacheFile => Path.Combine(DataDirectory, "cache.json");

    public static string LogDirectory => Path.Combine(DataDirectory, "logs");

    /// <summary>Directory holding one small activity file per provider, written by hooks.</summary>
    public static string ActivityDirectory => Path.Combine(DataDirectory, "activity");

    public static string ActivityFile(string providerKey) =>
        Path.Combine(ActivityDirectory, providerKey + ".json");

    public static string UserProfile { get; } =
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static string ClaudeHome => Path.Combine(UserProfile, ".claude");

    public static string ClaudeCredentialsFile => Path.Combine(ClaudeHome, ".credentials.json");

    public static string ClaudeSettingsFile => Path.Combine(ClaudeHome, "settings.json");

    /// <summary>
    /// Usage history written by Claude Desktop. Read only, and the only Claude quota source on a
    /// machine that has the desktop app but not the standalone CLI.
    /// </summary>
    public static string ClaudeDesktopUsageFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Claude",
        "plan-usage-history.json");

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
