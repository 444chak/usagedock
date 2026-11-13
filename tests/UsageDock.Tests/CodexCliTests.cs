using UsageDock.Models;
using UsageDock.Providers;
using UsageDock.Services;
using Xunit;

namespace UsageDock.Tests;

public class CodexCliRateLimitParserTests
{
    /// <summary>What Codex 0.167.0 answered on 2026-11-13, with the reset credits trimmed.</summary>
    internal const string Answer =
        """{"id":"usagedock-rate-limits","result":{"ordinaryUsageAllowed":true,"rateLimits":{"limitId":"codex","limitName":null,"primary":{"usedPercent":3,"windowDurationMins":300,"resetsAt":1794595644},"secondary":{"usedPercent":10,"windowDurationMins":10080,"resetsAt":1794901454},"credits":{"hasCredits":false,"unlimited":false,"balance":"0"},"individualLimit":null,"spendControlReached":false,"planType":"plus","rateLimitReachedType":null},"rateLimitResetCredits":{"availableCount":0,"credits":[]}}}""";

    [Fact]
    public void Reads_both_windows_their_resets_and_the_plan()
    {
        var result = CodexCliRateLimitParser.Parse(Answer);

        Assert.NotNull(result);
        Assert.Null(result!.Error);
        Assert.Equal("plus", result.Plan);
        Assert.Equal(["5 hour", "Weekly"], result.Windows.Select(static w => w.Name));
        Assert.Equal(3d, result.Windows[0].UsedPercent);
        Assert.Equal(10d, result.Windows[1].UsedPercent);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1794901454), result.Windows[1].ResetsAt);
        Assert.Equal(TimeSpan.FromDays(7), result.Windows[1].WindowLength);
    }

    [Fact]
    public void An_error_reply_carries_its_reason()
    {
        var result = CodexCliRateLimitParser.Parse(
            """{"id":"usagedock-rate-limits","error":{"code":-32600,"message":"codex account authentication required to read rate limits"}}""");

        Assert.Empty(result!.Windows);
        Assert.Equal("codex account authentication required to read rate limits", result.Error);
    }

    [Fact]
    public void Windows_without_a_figure_are_left_out()
    {
        var result = CodexCliRateLimitParser.Parse(
            """{"id":"x","result":{"rateLimits":{"primary":{"usedPercent":null,"windowDurationMins":300},"secondary":{"usedPercent":12,"windowDurationMins":10080,"resetsAt":99999999999999}}}}""");

        var weekly = Assert.Single(result!.Windows);
        Assert.Equal("Weekly", weekly.Name);
        Assert.Null(weekly.ResetsAt);
    }

    [Fact]
    public void Only_the_answer_to_our_request_is_taken()
    {
        Assert.True(CodexCliRateLimitParser.IsAnswerTo(Answer, "usagedock-rate-limits"));
        Assert.False(CodexCliRateLimitParser.IsAnswerTo(
            """{"method":"account/updated","params":{"note":"usagedock-rate-limits"}}""", "usagedock-rate-limits"));
        Assert.False(CodexCliRateLimitParser.IsAnswerTo("usagedock-rate-limits {", "usagedock-rate-limits"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"id":"x"}""")]
    [InlineData("""{"id":"x","result":{"rateLimits":"text"}}""")]
    public void Unrecognisable_answers_read_as_null(string line) =>
        Assert.Null(CodexCliRateLimitParser.Parse(line));

    [Theory]
    [InlineData("OpenAI.Codex_26.1106.6628.0_x64__2p2nqsd0c76g0", "26.1106.6628.0")]
    [InlineData("OpenAI.Codex", "0.0")]
    [InlineData("OpenAI.Codex_garbage_x64", "0.0")]
    public void The_package_version_is_read_from_its_full_name(string name, string expected) =>
        Assert.Equal(Version.Parse(expected), CodexCli.PackageVersion(name));
}

public sealed class CodexUsageProviderCliTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 11, 13, 14, 0, 0, TimeSpan.Zero);

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "usagedock-codex-cli-" + Guid.NewGuid().ToString("N"));

    private DateTimeOffset _now = Start;

    public CodexUsageProviderCliTests()
    {
        Directory.CreateDirectory(Path.Combine(_directory, "sessions"));
        File.WriteAllText(
            Path.Combine(_directory, "auth.json"),
            """{ "auth_mode": "chatgpt", "tokens": { "access_token": "test-token" } }""");
    }

    private sealed class FakeCli(Func<CodexCliOutcome> answer, bool available = true) : ICodexCliUsage
    {
        public int Calls { get; private set; }

        public bool IsAvailable => available;

        public Task<CodexCliOutcome> GetRateLimitsAsync(CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(answer());
        }
    }

    private static FakeCli Answering() => new(static () => new CodexCliOutcome(CodexCliRateLimitParserTests.Answer, null));

    private CodexUsageProvider Provider(ICodexCliUsage? cli) => new(
        new CodexSessionScanner(Path.Combine(_directory, "sessions")),
        now: () => _now,
        authPath: Path.Combine(_directory, "auth.json"),
        cli: cli);

    private void WriteRollout(DateTimeOffset recordedAt, double used) =>
        File.WriteAllText(
            Path.Combine(_directory, "sessions", "rollout-test.jsonl"),
            "{\"timestamp\":\"" + recordedAt.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ") + "\",\"type\":\"event_msg\","
            + "\"payload\":{\"type\":\"token_count\",\"rate_limits\":{\"primary\":{\"used_percent\":" + used
            + ",\"window_minutes\":300,\"resets_at\":1794455164}}}}\n");

    [Fact]
    public async Task A_live_answer_beats_an_older_rollout()
    {
        // The situation of 2026-11-13: the last local session a day old, the app used since.
        WriteRollout(Start.AddDays(-1), 0);

        var snapshot = await Provider(Answering()).FetchAsync(CancellationToken.None);

        Assert.Equal(SnapshotStatus.Ok, snapshot.Status);
        Assert.Equal(Start, snapshot.UpdatedAt);
        Assert.Equal(3d, snapshot.Windows[0].UsedPercent);
        Assert.Equal("plus", snapshot.Plan);
    }

    [Fact]
    public async Task Between_asks_the_live_reading_is_offered_again_not_the_older_rollout()
    {
        WriteRollout(Start.AddDays(-1), 0);
        var cli = Answering();
        var provider = Provider(cli);

        await provider.FetchAsync(CancellationToken.None);

        _now = Start.AddMinutes(1);
        var between = await provider.FetchAsync(CancellationToken.None);

        Assert.Equal(1, cli.Calls);
        Assert.Equal(SnapshotStatus.Ok, between.Status);
        Assert.Equal(Start, between.UpdatedAt);
        Assert.Equal(3d, between.Windows[0].UsedPercent);

        // Kept stable so the coordinator neither serves it from cache nor logs a change.
        var (reconciled, servedFromCache) = UsageCoordinator.Reconcile(between, between);
        Assert.False(servedFromCache);
        Assert.Equal(Start, reconciled.UpdatedAt);

        _now = Start + CodexUsageProvider.MinCliInterval;
        await provider.FetchAsync(CancellationToken.None);
        Assert.Equal(2, cli.Calls);
    }

    [Fact]
    public async Task A_local_session_newer_than_the_last_ask_wins()
    {
        var provider = Provider(Answering());
        await provider.FetchAsync(CancellationToken.None);

        WriteRollout(Start.AddSeconds(30), 42);
        _now = Start.AddMinutes(1);

        var snapshot = await provider.FetchAsync(CancellationToken.None);

        Assert.Equal(42d, snapshot.Windows[0].UsedPercent);
    }

    [Fact]
    public async Task A_failing_cli_falls_back_to_the_rollout_and_backs_off()
    {
        WriteRollout(Start.AddHours(-1), 20);
        var cli = new FakeCli(static () => CodexCliOutcome.Failed("Codex did not answer within 30s"));
        var provider = Provider(cli);

        var first = await provider.FetchAsync(CancellationToken.None);

        Assert.Equal(SnapshotStatus.Ok, first.Status);
        Assert.Equal(20d, first.Windows[0].UsedPercent);
        Assert.Contains("did not answer", first.Message, StringComparison.Ordinal);

        const int polls = 10;
        for (var i = 1; i < polls; i++)
        {
            _now = Start + CodexUsageProvider.MinCliInterval * i;
            await provider.FetchAsync(CancellationToken.None);
        }

        Assert.True(cli.Calls < polls, $"started {cli.Calls} times in {polls} intervals");
    }

    [Fact]
    public async Task An_error_answer_is_reported_when_nothing_else_is_known()
    {
        var cli = new FakeCli(static () => new CodexCliOutcome(
            """{"id":"usagedock-rate-limits","error":{"message":"not signed in"}}""", null));

        var snapshot = await Provider(cli).FetchAsync(CancellationToken.None);

        Assert.Equal(SnapshotStatus.Error, snapshot.Status);
        Assert.Equal("Codex: not signed in", snapshot.Message);
    }

    [Fact]
    public async Task Without_a_codex_session_nothing_is_started()
    {
        File.Delete(Path.Combine(_directory, "auth.json"));
        var cli = Answering();

        var snapshot = await Provider(cli).FetchAsync(CancellationToken.None);

        Assert.Equal(0, cli.Calls);
        Assert.Equal(SnapshotStatus.NoCredentials, snapshot.Status);
    }

    [Fact]
    public async Task Without_an_executable_the_rollouts_still_work()
    {
        WriteRollout(Start.AddHours(-1), 20);
        var cli = new FakeCli(static () => throw new InvalidOperationException("must not be asked"), available: false);

        var snapshot = await Provider(cli).FetchAsync(CancellationToken.None);

        Assert.Equal(20d, snapshot.Windows[0].UsedPercent);
        Assert.Null(snapshot.Message);
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
