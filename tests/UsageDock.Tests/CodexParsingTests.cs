using UsageDock.Providers;
using Xunit;

namespace UsageDock.Tests;

public class CodexRateLimitParserTests
{
    /// <summary>A real token_count event as the Codex CLI writes it into a rollout file.</summary>
    private const string RolloutLine = """
    {"timestamp":"2026-09-08T19:35:46.158Z","type":"event_msg","payload":{"type":"token_count",
    "info":{"model_context_window":258400},
    "rate_limits":{"limit_id":"codex",
      "primary":{"used_percent":6.0,"window_minutes":300,"resets_at":1788913590},
      "secondary":{"used_percent":19.0,"window_minutes":10080,"resets_at":1789455095},
      "plan_type":"plus"}}}
    """;

    [Fact]
    public void Finds_rate_limits_nested_inside_the_event()
    {
        var result = CodexRateLimitParser.ParseLine(RolloutLine);

        Assert.NotNull(result);
        Assert.Equal(2, result!.Windows.Count);
        Assert.Equal("plus", result.PlanType);
    }

    [Fact]
    public void Names_windows_from_their_length()
    {
        var result = CodexRateLimitParser.ParseLine(RolloutLine)!;

        Assert.Equal("5 hour", result.Windows[0].Name);
        Assert.Equal("Weekly", result.Windows[1].Name);
    }

    [Fact]
    public void Reads_used_percent_and_reset_epoch()
    {
        var result = CodexRateLimitParser.ParseLine(RolloutLine)!;

        Assert.Equal(6d, result.Windows[0].UsedPercent);
        Assert.Equal(94d, result.Windows[0].LeftPercent);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1788913590), result.Windows[0].ResetsAt);
        Assert.Equal(TimeSpan.FromMinutes(300), result.Windows[0].WindowLength);
    }

    [Fact]
    public void The_older_relative_countdown_is_measured_from_the_record()
    {
        // Measured from the read instead, the reset would slide back on every poll and a spent
        // window would never be seen to roll over.
        var line = """
        {"timestamp":"2026-09-08T19:00:00Z","rate_limits":{"primary":{"used_percent":50,"window_minutes":300,"resets_in_seconds":600}}}
        """;

        var result = CodexRateLimitParser.ParseLine(line)!;

        Assert.Equal(new DateTimeOffset(2026, 9, 8, 19, 10, 0, TimeSpan.Zero), result.Windows[0].ResetsAt);
        Assert.Equal(new DateTimeOffset(2026, 9, 8, 19, 0, 0, TimeSpan.Zero), result.RecordedAt);
    }

    [Fact]
    public void A_relative_countdown_without_a_timestamp_falls_back_to_now()
    {
        var line = """
        {"rate_limits":{"primary":{"used_percent":50,"window_minutes":300,"resets_in_seconds":600}}}
        """;

        var result = CodexRateLimitParser.ParseLine(line)!;
        var delta = result.Windows[0].ResetsAt!.Value - DateTimeOffset.UtcNow;

        Assert.InRange(delta.TotalSeconds, 570, 630);
    }

    [Theory]
    [InlineData("""{"timestamp":"2026-09-29T19:34:51.900Z","type":"x"}""", "2026-09-29T19:34:51.900Z")]
    [InlineData("""{"type":"x","timestamp":"2026-09-29T19:34:51.900Z"}""", null)]
    [InlineData("""{"timestamp":"garbage"}""", null)]
    [InlineData("""{"timestamp":"2026""", null)]
    [InlineData("", null)]
    public void The_leading_timestamp_is_read_without_parsing_the_line(string line, string? expected) =>
        Assert.Equal(
            expected is null ? null : DateTimeOffset.Parse(expected, System.Globalization.CultureInfo.InvariantCulture),
            CodexRateLimitParser.ReadLineTimestamp(line));

    [Fact]
    public void Only_the_windows_that_are_present_come_back()
    {
        var line = """{"rate_limits":{"primary":{"used_percent":12,"window_minutes":300}}}""";

        var result = CodexRateLimitParser.ParseLine(line)!;

        Assert.Single(result.Windows);
        Assert.Null(result.PlanType);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("""{"type":"message","payload":{}}""")]
    [InlineData("""{"rate_limits": broken""")]
    public void Lines_without_usable_rate_limits_return_null(string line) =>
        Assert.Null(CodexRateLimitParser.ParseLine(line));

    [Fact]
    public void Window_without_used_percent_is_dropped()
    {
        var result = CodexRateLimitParser.ParseLine("""{"rate_limits":{"primary":{"window_minutes":300}}}""");

        Assert.NotNull(result);
        Assert.Empty(result!.Windows);
    }

    [Theory]
    [InlineData(300, "5 hour")]
    [InlineData(10080, "Weekly")]
    [InlineData(60, "1 hour")]
    [InlineData(30, "30 min")]
    [InlineData(4320, "3 day")]
    public void Window_labels_follow_the_window_length(int minutes, string expected) =>
        Assert.Equal(expected, CodexRateLimitParser.DescribeWindow("primary", TimeSpan.FromMinutes(minutes)));

    [Fact]
    public void Window_labels_fall_back_when_the_length_is_unknown()
    {
        Assert.Equal("Short window", CodexRateLimitParser.DescribeWindow("primary", null));
        Assert.Equal("Long window", CodexRateLimitParser.DescribeWindow("secondary", null));
    }
}

public class CodexAuthReaderTests
{
    [Fact]
    public void Detects_a_chatgpt_session_without_exposing_the_token()
    {
        var result = CodexAuthReader.Parse("""
        {
          "auth_mode": "chatgpt",
          "OPENAI_API_KEY": null,
          "tokens": { "access_token": "secret", "refresh_token": "secret" },
          "last_refresh": "2026-09-01T10:24:00.000Z"
        }
        """);

        Assert.True(result.HasSession);
        Assert.Equal("chatgpt", result.AuthMode);
        Assert.Equal(new DateTimeOffset(2026, 9, 1, 10, 24, 0, TimeSpan.Zero), result.LastRefresh);
    }

    [Fact]
    public void Detects_an_api_key_session()
    {
        var result = CodexAuthReader.Parse("""{ "auth_mode": "apikey", "OPENAI_API_KEY": "sk-x" }""");

        Assert.True(result.HasSession);
    }

    [Fact]
    public void Empty_tokens_count_as_signed_out() =>
        Assert.False(CodexAuthReader.Parse("""{ "tokens": { "access_token": "" } }""").HasSession);

    [Fact]
    public void Malformed_json_does_not_throw() =>
        Assert.False(CodexAuthReader.Parse("{{{").HasSession);
}

public sealed class CodexSessionScannerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "usagedock-codex-" + Guid.NewGuid().ToString("N"));

    public CodexSessionScannerTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static string Limits(string timestamp, double used) =>
        "{\"timestamp\":\"" + timestamp + "\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\","
        + "\"rate_limits\":{\"primary\":{\"used_percent\":" + used + ",\"window_minutes\":300,\"resets_at\":1790716716}}}}";

    private static string Line(string timestamp) =>
        "{\"timestamp\":\"" + timestamp + "\",\"type\":\"response_item\",\"payload\":{}}";

    private void Write(string name, DateTime lastWriteUtc, params string[] lines)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllLines(path, lines);
        File.SetLastWriteTimeUtc(path, lastWriteUtc);
    }

    [Fact]
    public void The_newest_record_wins_even_from_a_file_that_looks_older()
    {
        // What Windows really reports for a session Codex still holds open: its write time is
        // stuck at creation, so it ranks below a short session that started later.
        Write("rollout-long.jsonl", new DateTime(2026, 9, 29, 17, 15, 0, DateTimeKind.Utc),
            Limits("2026-09-29T17:20:00Z", 10),
            Limits("2026-09-29T19:34:51Z", 100),
            Line("2026-09-29T19:34:52Z"));
        Write("rollout-short.jsonl", new DateTime(2026, 9, 29, 19, 1, 0, DateTimeKind.Utc),
            Limits("2026-09-29T19:01:03Z", 80));

        var scan = new CodexSessionScanner(_root).Run();

        Assert.Equal(100d, scan.RateLimits!.Windows[0].UsedPercent);
        Assert.Equal(new DateTimeOffset(2026, 9, 29, 19, 34, 52, TimeSpan.Zero), scan.NewestActivityUtc);
    }

    [Fact]
    public void A_file_without_limits_still_counts_as_activity()
    {
        Write("rollout-a.jsonl", new DateTime(2026, 9, 29, 10, 0, 0, DateTimeKind.Utc),
            Limits("2026-09-29T10:00:00Z", 40));
        Write("rollout-b.jsonl", new DateTime(2026, 9, 29, 11, 0, 0, DateTimeKind.Utc),
            Line("2026-09-29T12:30:00Z"));

        var scan = new CodexSessionScanner(_root).Run();

        Assert.Equal(40d, scan.RateLimits!.Windows[0].UsedPercent);
        Assert.Equal(new DateTimeOffset(2026, 9, 29, 12, 30, 0, TimeSpan.Zero), scan.NewestActivityUtc);
    }

    [Fact]
    public void A_missing_directory_scans_as_empty()
    {
        var scan = new CodexSessionScanner(Path.Combine(_root, "absent")).Run();

        Assert.Null(scan.RateLimits);
        Assert.Null(scan.NewestActivityUtc);
    }
}
