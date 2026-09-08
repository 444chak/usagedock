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
    public void Supports_the_older_relative_countdown_field()
    {
        var line = """
        {"rate_limits":{"primary":{"used_percent":50,"window_minutes":300,"resets_in_seconds":600}}}
        """;

        var result = CodexRateLimitParser.ParseLine(line)!;
        var resets = result.Windows[0].ResetsAt;

        Assert.NotNull(resets);
        var delta = resets!.Value - DateTimeOffset.UtcNow;
        Assert.InRange(delta.TotalSeconds, 570, 630);
    }

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
