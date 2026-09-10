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
    public void The_claude_usage_file_sits_under_roaming_appdata() =>
        Assert.StartsWith(AppPaths.RoamingAppData, AppPaths.ClaudeDesktopUsageFile, StringComparison.OrdinalIgnoreCase);

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
