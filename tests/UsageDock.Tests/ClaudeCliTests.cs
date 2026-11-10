using UsageDock.Models;
using UsageDock.Providers;
using Xunit;

namespace UsageDock.Tests;

public class ClaudeCliUsageParserTests
{
    /// <summary>Shaped like the CLI's answer, with the session totals trimmed.</summary>
    private const string SignedIn =
        """{"type":"control_response","response":{"subtype":"success","request_id":"usagedock-usage","response":{"session":{"total_cost_usd":0},"subscription_type":"pro","rate_limits_available":true,"rate_limits":{"five_hour":{"utilization":34,"resets_at":"2026-11-10T13:00:00Z"},"seven_day":{"utilization":47,"resets_at":"2026-11-14T06:00:00Z"},"seven_day_opus":null,"model_scoped":[],"extra_usage":null},"behaviors":null}}}""";

    /// <summary>Verbatim, apart from timings, what CLI 2.1.321 answered without a login.</summary>
    private const string SignedOut =
        """{"type":"control_response","response":{"subtype":"success","request_id":"usagedock-usage","response":{"session":{"total_cost_usd":0,"total_api_duration_ms":0,"total_duration_ms":378,"total_lines_added":0,"total_lines_removed":0,"model_usage":{}},"subscription_type":null,"rate_limits_available":false,"rate_limits":null,"behaviors":null}}}""";

    [Fact]
    public void Reads_the_plan_windows_and_their_resets()
    {
        var result = ClaudeCliUsageParser.Parse(SignedIn);

        Assert.NotNull(result);
        Assert.True(result!.Available);
        Assert.Equal("pro", result.Plan);
        Assert.Equal(2, result.Windows.Count);
        Assert.Equal(34d, result.Windows[0].UsedPercent);
        Assert.Equal(new DateTimeOffset(2026, 11, 14, 6, 0, 0, TimeSpan.Zero), result.Windows[1].ResetsAt);
    }

    [Fact]
    public void A_cli_without_a_login_has_nothing_available()
    {
        var result = ClaudeCliUsageParser.Parse(SignedOut);

        Assert.NotNull(result);
        Assert.False(result!.Available);
        Assert.Empty(result.Windows);
    }

    [Fact]
    public void An_error_reply_carries_its_reason()
    {
        var result = ClaudeCliUsageParser.Parse(
            """{"type":"control_response","response":{"subtype":"error","request_id":"usagedock-usage","error":"get_usage is not supported in this context"}}""");

        Assert.False(result!.Available);
        Assert.Equal("get_usage is not supported in this context", result.Error);
    }

    [Fact]
    public void Windows_without_a_figure_are_left_out()
    {
        var result = ClaudeCliUsageParser.Parse(
            """{"response":{"response":{"rate_limits_available":true,"rate_limits":{"five_hour":{"utilization":null,"resets_at":null},"seven_day":{"utilization":12,"resets_at":null}}}}}""");

        Assert.Equal("Weekly", Assert.Single(result!.Windows).Name);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"type":"control_response"}""")]
    [InlineData("""{"response":{"response":"text"}}""")]
    public void Unrecognisable_answers_read_as_null(string line) =>
        Assert.Null(ClaudeCliUsageParser.Parse(line));
}

public sealed class ClaudeUsageProviderCliTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 11, 10, 9, 0, 0, TimeSpan.Zero);

    private const string Answer =
        """{"type":"control_response","response":{"subtype":"success","request_id":"usagedock-usage","response":{"subscription_type":"pro","rate_limits_available":true,"rate_limits":{"five_hour":{"utilization":34,"resets_at":"2026-11-10T13:00:00Z"},"seven_day":{"utilization":47,"resets_at":"2026-11-14T06:00:00Z"}}}}}""";

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "usagedock-claude-cli-" + Guid.NewGuid().ToString("N"));

    private DateTimeOffset _now = Start;

    public ClaudeUsageProviderCliTests()
    {
        Directory.CreateDirectory(_directory);

        // What Claude Desktop leaves behind: the shape of a login, with the token fields blank.
        File.WriteAllText(
            Path.Combine(_directory, ".credentials.json"),
            """{ "claudeAiOauth": { "accessToken": "", "refreshToken": "", "expiresAt": 0, "subscriptionType": "pro" } }""");
    }

    private sealed class FakeCli(bool signedIn, Func<ClaudeCliOutcome> answer) : IClaudeCliUsage
    {
        public int Calls { get; private set; }

        public bool IsSignedIn => signedIn;

        public Task<ClaudeCliOutcome> GetUsageAsync(CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(answer());
        }
    }

    private sealed class NoNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The usage endpoint must not be called without a token.");
    }

    private ClaudeUsageProvider Provider(IClaudeCliUsage cli, string? credentials = null) => new(
        new HttpClient(new NoNetwork()),
        now: () => _now,
        credentialsPath: credentials ?? Path.Combine(_directory, ".credentials.json"),
        localUsagePath: Path.Combine(_directory, "missing-history.json"),
        statusLinePath: Path.Combine(_directory, "missing-statusline.json"),
        cli: cli);

    [Fact]
    public async Task Without_a_token_a_signed_in_cli_gives_live_numbers()
    {
        var snapshot = await Provider(new FakeCli(true, () => new ClaudeCliOutcome(Answer, null)))
            .FetchAsync(CancellationToken.None);

        Assert.Equal(SnapshotStatus.Ok, snapshot.Status);
        Assert.Equal(Start, snapshot.UpdatedAt);
        Assert.Equal(34d, snapshot.Windows[0].UsedPercent);
        Assert.Equal("pro", snapshot.Plan);
    }

    [Fact]
    public async Task The_cli_is_started_at_most_once_per_interval()
    {
        var cli = new FakeCli(true, () => new ClaudeCliOutcome(Answer, null));
        var provider = Provider(cli);

        await provider.FetchAsync(CancellationToken.None);

        _now = Start.AddMinutes(1);
        var between = await provider.FetchAsync(CancellationToken.None);
        Assert.Equal(1, cli.Calls);

        // The coordinator keeps the last answer on a quiet non Ok poll.
        Assert.NotEqual(SnapshotStatus.Ok, between.Status);
        Assert.Null(between.Message);

        _now = Start + ClaudeUsageProvider.MinApiInterval;
        await provider.FetchAsync(CancellationToken.None);
        Assert.Equal(2, cli.Calls);
    }

    [Fact]
    public async Task A_cli_that_is_not_signed_in_is_never_started()
    {
        var cli = new FakeCli(false, () => new ClaudeCliOutcome(Answer, null));

        var snapshot = await Provider(cli).FetchAsync(CancellationToken.None);

        Assert.Equal(0, cli.Calls);
        Assert.Equal(SnapshotStatus.NoCredentials, snapshot.Status);
    }

    [Fact]
    public async Task A_failing_cli_backs_off_beyond_the_interval()
    {
        var cli = new FakeCli(true, () => ClaudeCliOutcome.Failed("Claude Code CLI did not answer within 45s"));
        var provider = Provider(cli);

        var failed = await provider.FetchAsync(CancellationToken.None);
        Assert.Contains("did not answer", failed.Message, StringComparison.Ordinal);

        // The backoff starts below the interval and doubles, so a CLI that keeps failing ends up
        // started less often than every interval.
        const int polls = 8;
        for (var i = 1; i < polls; i++)
        {
            _now = Start + ClaudeUsageProvider.MinApiInterval * i;
            await provider.FetchAsync(CancellationToken.None);
        }

        Assert.True(cli.Calls < polls, $"started {cli.Calls} times in {polls} intervals");
    }

    [Fact]
    public async Task A_signed_out_answer_asks_for_a_new_login()
    {
        var cli = new FakeCli(true, () => new ClaudeCliOutcome(
            """{"type":"control_response","response":{"subtype":"success","request_id":"usagedock-usage","response":{"rate_limits_available":false,"rate_limits":null}}}""",
            null));

        var snapshot = await Provider(cli).FetchAsync(CancellationToken.None);

        Assert.NotEqual(SnapshotStatus.Ok, snapshot.Status);
        Assert.Contains("tray menu", snapshot.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_usable_token_is_preferred_and_the_cli_left_alone()
    {
        var withToken = Path.Combine(_directory, "token.json");
        File.WriteAllText(withToken, """{ "claudeAiOauth": { "accessToken": "test-token", "expiresAt": 0 } }""");

        var cli = new FakeCli(true, () => new ClaudeCliOutcome(Answer, null));

        // NoNetwork throws, which the provider turns into a network error: the point is only
        // that the CLI was not the one asked.
        await Provider(cli, withToken).FetchAsync(CancellationToken.None);

        Assert.Equal(0, cli.Calls);
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
