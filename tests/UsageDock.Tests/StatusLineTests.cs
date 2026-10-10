using System.Text;
using System.Text.Json.Nodes;
using UsageDock.Models;
using UsageDock.Providers;
using UsageDock.Services;
using Xunit;

namespace UsageDock.Tests;

public sealed class ClaudeStatusLineFeedTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 23, 18, 42, TimeSpan.Zero);

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "usagedock-statusline-" + Guid.NewGuid().ToString("N"));

    private string FeedPath => Path.Combine(_directory, "claude-statusline.json");

    public ClaudeStatusLineFeedTests() => Directory.CreateDirectory(_directory);

    /// <summary>Trimmed from a real payload Claude Code 2.1.265 passed to its status line.</summary>
    private static byte[] Payload(int fiveHour = 11, int sevenDay = 1) => Encoding.UTF8.GetBytes(
        "{\"session_id\":\"00000000-0000-4000-8000-000000000000\",\"cwd\":\"C:\\\\Users\\\\élodie\\\\work\","
        + "\"model\":{\"id\":\"claude-opus-5-5\"},\"rate_limits\":{"
        + "\"five_hour\":{\"used_percentage\":" + fiveHour + ",\"resets_at\":1791690000},"
        + "\"seven_day\":{\"used_percentage\":" + sevenDay + ",\"resets_at\":1792216800}}}");

    [Fact]
    public void Extracts_both_windows_with_their_exact_resets()
    {
        var limits = ClaudeStatusLineFeed.ExtractRateLimits(Payload());

        Assert.NotNull(limits);
        Assert.True(ClaudeStatusLineFeed.Store(limits!.Value, Now, FeedPath));

        var result = ClaudeStatusLineFeed.Read(FeedPath);

        Assert.NotNull(result);
        Assert.Equal(Now, result!.SampledAt);
        Assert.Equal(2, result.Windows.Count);
        Assert.Equal(11d, result.Windows[0].UsedPercent);
        Assert.Equal(TimeSpan.FromHours(5), result.Windows[0].WindowLength);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1791690000), result.Windows[0].ResetsAt);
        Assert.Equal(1d, result.Windows[1].UsedPercent);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1792216800), result.Windows[1].ResetsAt);
    }

    [Fact]
    public void Stores_nothing_but_the_rate_limits()
    {
        var limits = ClaudeStatusLineFeed.ExtractRateLimits(Payload());
        ClaudeStatusLineFeed.Store(limits!.Value, Now, FeedPath);

        var text = File.ReadAllText(FeedPath);

        Assert.DoesNotContain("session_id", text, StringComparison.Ordinal);
        Assert.DoesNotContain("cwd", text, StringComparison.Ordinal);
        Assert.DoesNotContain("model", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_payload_without_rate_limits_yields_nothing()
    {
        var payload = Encoding.UTF8.GetBytes("{\"session_id\":\"x\",\"model\":{\"id\":\"claude-opus-5-5\"}}");

        Assert.Null(ClaudeStatusLineFeed.ExtractRateLimits(payload));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2,3]")]
    [InlineData("{\"rate_limits\":\"soon\"}")]
    [InlineData("{\"rate_limits\":{\"five_hour\":{\"resets_at\":1791690000}}}")]
    public void Malformed_payloads_degrade_to_nothing(string payload)
    {
        Assert.Null(ClaudeStatusLineFeed.ExtractRateLimits(Encoding.UTF8.GetBytes(payload)));
    }

    [Fact]
    public void A_malformed_feed_file_reads_as_nothing()
    {
        File.WriteAllText(FeedPath, "{\"at\":\"yesterday\",\"rateLimits\":[");

        Assert.Null(ClaudeStatusLineFeed.Read(FeedPath));
    }

    [Fact]
    public void An_unchanged_reading_is_not_rewritten_within_a_minute()
    {
        var limits = ClaudeStatusLineFeed.ExtractRateLimits(Payload())!.Value;

        Assert.True(ClaudeStatusLineFeed.Store(limits, Now, FeedPath));
        Assert.False(ClaudeStatusLineFeed.Store(limits, Now.AddSeconds(20), FeedPath));
        Assert.True(ClaudeStatusLineFeed.Store(limits, Now.AddMinutes(2), FeedPath));

        Assert.Equal(Now.AddMinutes(2), ClaudeStatusLineFeed.Read(FeedPath)!.SampledAt);
    }

    [Fact]
    public void A_changed_reading_is_written_at_once()
    {
        ClaudeStatusLineFeed.Store(ClaudeStatusLineFeed.ExtractRateLimits(Payload(11))!.Value, Now, FeedPath);

        var changed = ClaudeStatusLineFeed.ExtractRateLimits(Payload(12))!.Value;

        Assert.True(ClaudeStatusLineFeed.Store(changed, Now.AddSeconds(3), FeedPath));
        Assert.Equal(12d, ClaudeStatusLineFeed.Read(FeedPath)!.Windows[0].UsedPercent);
    }

    [Fact]
    public void The_summary_shows_both_windows()
    {
        var limits = ClaudeStatusLineFeed.ExtractRateLimits(Payload());

        Assert.Equal("5h 11% · weekly 1%", ClaudeStatusLineFeed.Summarize(limits));
        Assert.Equal(string.Empty, ClaudeStatusLineFeed.Summarize(null));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

public class FreshestClaudeReadingTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 23, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan FiveHours = TimeSpan.FromHours(5);
    private static readonly TimeSpan SevenDays = TimeSpan.FromDays(7);

    private static ClaudeLocalUsageReader.Result Reading(
        DateTimeOffset at, double fiveHour, DateTimeOffset? fiveHourReset, double week, DateTimeOffset? weekReset) =>
        new(
        [
            QuotaWindow.Create("5 hour", fiveHour, fiveHourReset, FiveHours),
            QuotaWindow.Create("Weekly", week, weekReset, SevenDays)
        ], at);

    [Fact]
    public void A_newer_status_line_reading_wins()
    {
        var statusLine = Reading(Now, 40, Now.AddHours(2), 10, Now.AddDays(3));
        var desktop = Reading(Now.AddMinutes(-10), 35, null, 9, null);

        Assert.Same(statusLine, ClaudeUsageProvider.Freshest(statusLine, desktop));
    }

    [Fact]
    public void A_newer_desktop_sample_wins_but_keeps_the_exact_resets()
    {
        var statusLine = Reading(Now.AddMinutes(-30), 40, Now.AddHours(2), 10, Now.AddDays(3));
        var desktop = Reading(Now, 55, Now.AddHours(2.1), 12, Now.AddDays(2.9));

        var result = ClaudeUsageProvider.Freshest(statusLine, desktop)!;

        Assert.Equal(Now, result.SampledAt);
        Assert.Equal(55d, result.Windows[0].UsedPercent);
        Assert.Equal(Now.AddHours(2), result.Windows[0].ResetsAt);
        Assert.Equal(12d, result.Windows[1].UsedPercent);
        Assert.Equal(Now.AddDays(3), result.Windows[1].ResetsAt);
    }

    [Fact]
    public void A_status_line_reset_that_passed_before_the_desktop_sample_is_not_carried_over()
    {
        var statusLine = Reading(Now.AddHours(-6), 90, Now.AddHours(-1), 10, Now.AddDays(3));
        var desktop = Reading(Now, 5, null, 12, null);

        var result = ClaudeUsageProvider.Freshest(statusLine, desktop)!;

        Assert.Null(result.Windows[0].ResetsAt);
        Assert.Equal(Now.AddDays(3), result.Windows[1].ResetsAt);
    }

    [Fact]
    public void Either_source_alone_is_used_as_is()
    {
        var reading = Reading(Now, 40, null, 10, null);

        Assert.Same(reading, ClaudeUsageProvider.Freshest(reading, null));
        Assert.Same(reading, ClaudeUsageProvider.Freshest(null, reading));
        Assert.Null(ClaudeUsageProvider.Freshest(null, null));
    }
}

public sealed class StatusLineInstallerTests : IDisposable
{
    /// <summary>The status line Orca installs, verbatim, quotes and all.</summary>
    private const string OtherCommand =
        "if [ -f 'C:/Users/me/.orca/agent-hooks/claude-statusline.cmd' ]; then "
        + "'C:/Users/me/.orca/agent-hooks/claude-statusline.cmd'; else cat >/dev/null 2>&1 || :; fi";

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "usagedock-statusline-install-" + Guid.NewGuid().ToString("N"));

    private string SettingsPath => Path.Combine(_directory, "settings.json");

    public StatusLineInstallerTests() => Directory.CreateDirectory(_directory);

    private StatusLineInstaller Installer(string exe = @"C:\apps\UsageDock.exe") => new(SettingsPath, exe);

    private void WriteSettingsWithOtherStatusLine() =>
        File.WriteAllText(SettingsPath, new JsonObject
        {
            ["theme"] = "dark",
            ["statusLine"] = new JsonObject
            {
                ["type"] = "command",
                ["command"] = OtherCommand,
                ["padding"] = 0
            }
        }.ToJsonString());

    private JsonObject StatusLine() =>
        (JsonObject)JsonNode.Parse(File.ReadAllText(SettingsPath))!["statusLine"]!;

    private string Command() => StatusLine()["command"]!.GetValue<string>();

    [Fact]
    public void Without_a_status_line_it_installs_a_standalone_one()
    {
        File.WriteAllText(SettingsPath, """{ "theme": "dark" }""");

        Assert.True(Installer().Install());
        Assert.True(Installer().IsInstalled());

        Assert.Equal(@"""C:\apps\UsageDock.exe"" " + StatusLineInstaller.Marker, Command());
    }

    [Fact]
    public void An_existing_status_line_is_chained_behind_the_feed()
    {
        WriteSettingsWithOtherStatusLine();

        Assert.True(Installer().Install());

        var command = Command();
        Assert.Contains(StatusLineInstaller.TeeFlag, command, StringComparison.Ordinal);
        Assert.EndsWith("(\n" + OtherCommand + "\n)", command, StringComparison.Ordinal);
        Assert.Equal(0, StatusLine()["padding"]!.GetValue<int>());
    }

    [Fact]
    public void Uninstall_puts_the_previous_status_line_back_verbatim()
    {
        WriteSettingsWithOtherStatusLine();
        var installer = Installer();
        installer.Install();

        Assert.True(installer.Uninstall());
        Assert.False(installer.IsInstalled());

        Assert.Equal(OtherCommand, Command());
        Assert.Equal(0, StatusLine()["padding"]!.GetValue<int>());
    }

    [Fact]
    public void Uninstall_removes_a_status_line_it_created()
    {
        File.WriteAllText(SettingsPath, """{ "theme": "dark" }""");
        var installer = Installer();
        installer.Install();

        Assert.True(installer.Uninstall());

        Assert.Null(JsonNode.Parse(File.ReadAllText(SettingsPath))!["statusLine"]);
    }

    [Fact]
    public void Installing_twice_does_not_nest_the_chain()
    {
        WriteSettingsWithOtherStatusLine();
        var installer = Installer();
        installer.Install();
        var first = Command();

        Assert.False(installer.Install());

        Assert.Equal(first, Command());
        Assert.Equal(1, first.Split(StatusLineInstaller.Marker).Length - 1);
    }

    [Fact]
    public void Repointing_moves_the_path_and_keeps_the_chained_command()
    {
        WriteSettingsWithOtherStatusLine();
        Installer(@"C:\old\UsageDock.exe").Install();

        var moved = Installer(@"C:\new\UsageDock.exe");
        Assert.True(moved.NeedsRepointing());
        Assert.True(moved.Install());
        Assert.False(moved.NeedsRepointing());

        var command = Command();
        Assert.Contains(@"C:\new\UsageDock.exe", command, StringComparison.Ordinal);
        Assert.DoesNotContain(@"C:\old\", command, StringComparison.Ordinal);
        Assert.Equal(OtherCommand, StatusLineInstaller.ExtractPrevious(command));
    }

    [Fact]
    public void A_status_line_of_an_unknown_shape_is_left_alone()
    {
        const string settings = """{ "statusLine": { "type": "widget", "name": "clock" } }""";
        File.WriteAllText(SettingsPath, settings);

        Assert.False(Installer().Install());
        Assert.False(Installer().IsInstalled());
        Assert.Contains("clock", File.ReadAllText(SettingsPath), StringComparison.Ordinal);
    }

    [Fact]
    public void Hooks_and_the_status_line_feed_are_repointed_independently()
    {
        new HookInstaller(SettingsPath, @"C:\old\UsageDock.exe").Install();
        Installer(@"C:\new\UsageDock.exe").Install();

        // The feed already names the current path; the stale hooks must still be noticed.
        Assert.True(new HookInstaller(SettingsPath, @"C:\new\UsageDock.exe").NeedsRepointing());
        Assert.False(Installer(@"C:\new\UsageDock.exe").NeedsRepointing());
    }

    [Fact]
    public void The_entry_point_ignores_other_invocations()
    {
        Assert.False(StatusLineEntryPoint.TryHandle([]));
        Assert.False(StatusLineEntryPoint.TryHandle([HookInstaller.Marker, "idle"]));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
