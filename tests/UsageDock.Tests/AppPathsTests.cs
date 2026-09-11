using UsageDock.Services;
using Xunit;

namespace UsageDock.Tests;

/// <summary>
/// These exist because of a real failure. A process launched early at Windows login got an empty
/// string back from GetFolderPath, every path below it became relative, and the app spent ten
/// hours reading and writing next to its working directory without a single log line to say so.
/// </summary>
public class AppPathsTests
{
    public static TheoryData<string, string> Roots => new()
    {
        { nameof(AppPaths.DataDirectory), AppPaths.DataDirectory },
        { nameof(AppPaths.RoamingAppData), AppPaths.RoamingAppData },
        { nameof(AppPaths.UserProfile), AppPaths.UserProfile }
    };

    public static TheoryData<string, string> Derived => new()
    {
        { nameof(AppPaths.ConfigFile), AppPaths.ConfigFile },
        { nameof(AppPaths.CacheFile), AppPaths.CacheFile },
        { nameof(AppPaths.LogDirectory), AppPaths.LogDirectory },
        { nameof(AppPaths.ActivityDirectory), AppPaths.ActivityDirectory },
        { nameof(AppPaths.ClaudeHome), AppPaths.ClaudeHome },
        { nameof(AppPaths.ClaudeCredentialsFile), AppPaths.ClaudeCredentialsFile },
        { nameof(AppPaths.ClaudeSettingsFile), AppPaths.ClaudeSettingsFile },
        { nameof(AppPaths.ClaudeDesktopUsageFile), AppPaths.ClaudeDesktopUsageFile },
        { nameof(AppPaths.CodexHome), AppPaths.CodexHome },
        { nameof(AppPaths.CodexAuthFile), AppPaths.CodexAuthFile },
        { nameof(AppPaths.CodexSessionsDirectory), AppPaths.CodexSessionsDirectory }
    };

    [Theory]
    [MemberData(nameof(Roots))]
    public void Roots_are_absolute(string name, string value)
    {
        Assert.False(string.IsNullOrWhiteSpace(value), $"{name} is empty");
        Assert.True(Path.IsPathFullyQualified(value), $"{name} is not absolute: {value}");
    }

    [Theory]
    [MemberData(nameof(Derived))]
    public void Every_derived_path_is_absolute(string name, string value)
    {
        Assert.False(string.IsNullOrWhiteSpace(value), $"{name} is empty");
        Assert.True(Path.IsPathFullyQualified(value), $"{name} is not absolute: {value}");
    }

    [Fact]
    public void Codex_and_claude_homes_sit_under_the_user_profile()
    {
        Assert.StartsWith(AppPaths.UserProfile, AppPaths.ClaudeHome, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(AppPaths.UserProfile, AppPaths.CodexHome, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_activity_file_lands_inside_the_activity_directory() =>
        Assert.Equal(
            Path.Combine(AppPaths.ActivityDirectory, "claude.json"),
            AppPaths.ActivityFile("claude"));

    [Fact]
    public void Describe_names_all_three_roots()
    {
        var text = AppPaths.Describe();

        Assert.Contains(AppPaths.DataDirectory, text, StringComparison.Ordinal);
        Assert.Contains(AppPaths.RoamingAppData, text, StringComparison.Ordinal);
        Assert.Contains(AppPaths.UserProfile, text, StringComparison.Ordinal);
    }
}

/// <summary>
/// Direct tests of the candidate selection, which is the part that had to change: the old code
/// used the first answer it got, empty string included.
/// </summary>
public class AppPathsFallbackTests
{
    [Fact]
    public void An_empty_first_candidate_is_skipped()
    {
        var chosen = AppPaths.FirstUsable(() => string.Empty, () => @"C:\Users\someone\AppData\Roaming");

        Assert.Equal(@"C:\Users\someone\AppData\Roaming", chosen);
    }

    [Fact]
    public void A_relative_candidate_is_skipped()
    {
        // This is the exact shape of the bug: a relative root sends every path below it next to
        // the working directory.
        var chosen = AppPaths.FirstUsable(() => "UsageDock", () => @"C:\data");

        Assert.Equal(@"C:\data", chosen);
    }

    [Fact]
    public void A_throwing_candidate_is_skipped()
    {
        var chosen = AppPaths.FirstUsable(
            () => throw new InvalidOperationException("no shell"),
            () => @"C:\data");

        Assert.Equal(@"C:\data", chosen);
    }

    [Fact]
    public void The_first_usable_candidate_wins()
    {
        var chosen = AppPaths.FirstUsable(() => @"C:\first", () => @"C:\second");

        Assert.Equal(@"C:\first", chosen);
    }

    [Fact]
    public void No_usable_candidate_throws_rather_than_returning_something_relative()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => AppPaths.FirstUsable(() => string.Empty, () => null, () => "  "));

        Assert.Contains(AppPaths.DataDirectoryVariable, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("UsageDock", false)]
    [InlineData(@"AppData\Roaming", false)]
    // Rooted but not fully qualified: it still depends on whatever the current drive happens
    // to be, which is exactly the kind of path that must not become a data directory.
    [InlineData(@"\relative-to-drive", false)]
    [InlineData(@"C:\Users\someone", true)]
    [InlineData(@"\\server\share\folder", true)]
    public void Only_absolute_paths_are_usable(string? path, bool expected) =>
        Assert.Equal(expected, AppPaths.IsUsable(path));
}

/// <summary>
/// Claude Desktop from the Store keeps its files inside its MSIX package rather than in the
/// real roaming folder, so the usage file has more than one possible home.
/// </summary>
public class ClaudeUsageFileSelectionTests
{
    private static readonly string Classic = Path.Combine("C:", "u", "AppData", "Roaming", "Claude", "plan-usage-history.json");
    private static readonly string Store = Path.Combine("C:", "u", "AppData", "Local", "Packages", "Claude_abc", "LocalCache", "Roaming", "Claude", "plan-usage-history.json");

    private static readonly DateTime Older = new(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Newer = new(2026, 9, 11, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void The_only_existing_file_wins_even_when_it_is_not_first()
    {
        var chosen = AppPaths.ChooseUsageFile(
            [Classic, Store],
            fileExists: p => p == Store,
            lastWriteUtc: _ => Newer,
            folderExists: _ => true);

        Assert.Equal(Store, chosen);
    }

    [Fact]
    public void The_newest_file_wins_when_both_exist()
    {
        // A stale classic install must not shadow the live Store package.
        var chosen = AppPaths.ChooseUsageFile(
            [Classic, Store],
            fileExists: _ => true,
            lastWriteUtc: p => p == Store ? Newer : Older,
            folderExists: _ => true);

        Assert.Equal(Store, chosen);
    }

    [Fact]
    public void Without_a_file_the_first_candidate_with_a_folder_wins()
    {
        // Nothing written yet, but the Store package is installed: that is where to watch.
        var chosen = AppPaths.ChooseUsageFile(
            [Classic, Store],
            fileExists: _ => false,
            lastWriteUtc: _ => Older,
            folderExists: p => p == Store);

        Assert.Equal(Store, chosen);
    }

    [Fact]
    public void With_nothing_at_all_the_first_candidate_is_returned()
    {
        var chosen = AppPaths.ChooseUsageFile(
            [Classic, Store],
            fileExists: _ => false,
            lastWriteUtc: _ => Older,
            folderExists: _ => false);

        Assert.Equal(Classic, chosen);
    }

    [Fact]
    public void The_classic_location_is_always_the_first_candidate() =>
        Assert.StartsWith(AppPaths.RoamingAppData, AppPaths.ClaudeDesktopUsageCandidates().First(), StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void The_activity_exchange_lives_outside_appdata()
    {
        // Hooks run inside the Claude Desktop container, the dock does not, and AppData is
        // virtualised between them. The profile root is shared.
        Assert.StartsWith(AppPaths.UserProfile, AppPaths.ActivityDirectory, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("AppData", AppPaths.ActivityDirectory, StringComparison.OrdinalIgnoreCase);
    }
}
