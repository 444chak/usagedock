using UsageDock.Models;
using UsageDock.Providers;
using Xunit;

namespace UsageDock.Tests;

public class ClaudeUsageParserTests
{
    private const string FullPayload = """
    {
      "five_hour":       { "utilization": 23, "resets_at": "2026-09-08T22:00:00Z" },
      "seven_day":       { "utilization": 41.5, "resets_at": "2026-09-12T09:30:00Z" },
      "seven_day_opus":  { "utilization": 12, "resets_at": "2026-09-12T09:30:00Z" }
    }
    """;

    [Fact]
    public void Parses_every_known_window()
    {
        var windows = ClaudeUsageParser.Parse(FullPayload);

        Assert.Equal(3, windows.Count);
        Assert.Equal(["5 hour", "Weekly", "Weekly (Opus)"], windows.Select(w => w.Name));
        Assert.Equal(23d, windows[0].UsedPercent);
        Assert.Equal(41.5d, windows[1].UsedPercent);
    }

    [Fact]
    public void Parses_reset_timestamps_as_utc()
    {
        var windows = ClaudeUsageParser.Parse(FullPayload);

        Assert.Equal(
            new DateTimeOffset(2026, 9, 8, 22, 0, 0, TimeSpan.Zero),
            windows[0].ResetsAt);
    }

    [Fact]
    public void Accepts_epoch_seconds_for_the_reset()
    {
        var windows = ClaudeUsageParser.Parse("""{ "five_hour": { "utilization": 5, "resets_at": 1788913590 } }""");

        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1788913590), windows[0].ResetsAt);
    }

    [Fact]
    public void Missing_windows_are_skipped_rather_than_failing()
    {
        var windows = ClaudeUsageParser.Parse("""{ "seven_day": { "utilization": 7 } }""");

        Assert.Single(windows);
        Assert.Equal("Weekly", windows[0].Name);
        Assert.Null(windows[0].ResetsAt);
    }

    [Fact]
    public void Window_without_a_utilization_field_is_dropped() =>
        Assert.Empty(ClaudeUsageParser.Parse("""{ "five_hour": { "resets_at": "2026-09-08T22:00:00Z" } }"""));

    [Fact]
    public void Out_of_range_utilization_is_clamped()
    {
        var windows = ClaudeUsageParser.Parse("""{ "five_hour": { "utilization": 340 } }""");

        Assert.Equal(100d, windows[0].UsedPercent);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{ "five_hour": 12 }""")]
    public void Malformed_payloads_yield_no_windows(string payload) =>
        Assert.Empty(ClaudeUsageParser.Parse(payload));
}

public class ClaudeCredentialsReaderTests
{
    [Fact]
    public void Reads_token_and_metadata()
    {
        var result = ClaudeCredentialsReader.Parse("""
        {
          "claudeAiOauth": {
            "accessToken": "token-value",
            "expiresAt": 1788913590000,
            "subscriptionType": "pro"
          }
        }
        """);

        Assert.True(result.HasToken);
        Assert.Equal("pro", result.SubscriptionType);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1788913590000), result.ExpiresAt);
    }

    [Fact]
    public void Empty_token_counts_as_signed_out()
    {
        var result = ClaudeCredentialsReader.Parse("""{ "claudeAiOauth": { "accessToken": "" } }""");

        Assert.False(result.HasToken);
    }

    [Fact]
    public void Missing_oauth_section_counts_as_signed_out() =>
        Assert.False(ClaudeCredentialsReader.Parse("""{ "mcpOAuth": {} }""").HasToken);

    [Fact]
    public void Malformed_json_does_not_throw() =>
        Assert.False(ClaudeCredentialsReader.Parse("{ broken").HasToken);

    [Fact]
    public void Expiry_of_zero_is_treated_as_unknown_not_expired()
    {
        var result = ClaudeCredentialsReader.Parse("""
        { "claudeAiOauth": { "accessToken": "t", "expiresAt": 0 } }
        """);

        Assert.False(result.IsExpired(DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Past_expiry_is_reported_as_expired()
    {
        var result = ClaudeCredentialsReader.Parse("""
        { "claudeAiOauth": { "accessToken": "t", "expiresAt": 1000000000000 } }
        """);

        Assert.True(result.IsExpired(DateTimeOffset.UtcNow));
        Assert.False(result.IsExpired(DateTimeOffset.FromUnixTimeMilliseconds(999_000_000_000)));
    }
}
