using System.Net;
using UsageDock.Models;
using UsageDock.Providers;
using UsageDock.Services;
using Xunit;

namespace UsageDock.Tests;

public sealed class ClaudeUsageProviderTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 10, 19, 10, 0, 0, TimeSpan.Zero);

    private const string UsageBody =
        """{ "five_hour": { "utilization": 28, "resets_at": "2026-10-19T12:00:00Z" }, "seven_day": { "utilization": 7, "resets_at": "2026-10-23T00:00:00Z" } }""";

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "usagedock-claude-provider-" + Guid.NewGuid().ToString("N"));

    private DateTimeOffset _now = Start;

    public ClaudeUsageProviderTests()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(
            Path.Combine(_directory, ".credentials.json"),
            """{ "claudeAiOauth": { "accessToken": "test-token", "expiresAt": 0, "subscriptionType": "pro" } }""");
    }

    private sealed class StubHandler(Func<HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(respond());
        }
    }

    private ClaudeUsageProvider Provider(StubHandler handler) => new(
        new HttpClient(handler),
        now: () => _now,
        credentialsPath: Path.Combine(_directory, ".credentials.json"),
        localUsagePath: Path.Combine(_directory, "missing-history.json"),
        statusLinePath: Path.Combine(_directory, "missing-statusline.json"));

    private static HttpResponseMessage Ok() => new(HttpStatusCode.OK) { Content = new StringContent(UsageBody) };

    [Fact]
    public async Task The_usage_endpoint_is_asked_at_most_once_per_interval()
    {
        var handler = new StubHandler(Ok);
        var provider = Provider(handler);

        var first = await provider.FetchAsync(CancellationToken.None);
        Assert.Equal(SnapshotStatus.Ok, first.Status);
        Assert.Equal(28d, first.Windows[0].UsedPercent);

        _now = Start.AddMinutes(1);
        await provider.FetchAsync(CancellationToken.None);
        Assert.Equal(1, handler.Calls);

        _now = Start + ClaudeUsageProvider.MinApiInterval;
        await provider.FetchAsync(CancellationToken.None);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task Between_calls_nothing_alarming_is_reported()
    {
        var provider = Provider(new StubHandler(Ok));
        await provider.FetchAsync(CancellationToken.None);

        _now = Start.AddMinutes(1);
        var between = await provider.FetchAsync(CancellationToken.None);

        // Not Ok, so the coordinator keeps the last answer, and without a message to show on it.
        Assert.NotEqual(SnapshotStatus.Ok, between.Status);
        Assert.Null(between.Message);
    }

    [Fact]
    public async Task A_throttled_endpoint_is_not_asked_again_a_minute_later()
    {
        var handler = new StubHandler(() => new HttpResponseMessage(HttpStatusCode.TooManyRequests));
        var provider = Provider(handler);

        var throttled = await provider.FetchAsync(CancellationToken.None);
        Assert.Contains("429", throttled.Message, StringComparison.Ordinal);

        _now = Start.AddMinutes(1);
        await provider.FetchAsync(CancellationToken.None);

        Assert.Equal(1, handler.Calls);
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

public class SnapshotReconcileTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 19, 10, 0, 0, TimeSpan.Zero);

    private static UsageSnapshot Reading(DateTimeOffset at, double fiveHour, string? message = null) => new()
    {
        Provider = ProviderId.Claude,
        Status = SnapshotStatus.Ok,
        Windows = [QuotaWindow.Create("5 hour", fiveHour, null, TimeSpan.FromHours(5))],
        UpdatedAt = at,
        Message = message
    };

    [Fact]
    public void An_older_reading_does_not_replace_a_newer_one()
    {
        // The case seen in the wild: the usage endpoint throttled, the fallback was a desktop
        // sample from the day before, and the dock dropped from 28% to a rolled over 0%.
        var live = Reading(Now.AddMinutes(-1), 28);
        var staleFallback = Reading(Now.AddHours(-17), 4, "Throttled by Anthropic (429)");

        var (shown, fromCache) = UsageCoordinator.Reconcile(staleFallback, live);

        Assert.True(fromCache);
        Assert.Equal(28d, shown.Windows[0].UsedPercent);
        Assert.Equal("Throttled by Anthropic (429)", shown.Message);
    }

    [Fact]
    public void A_newer_reading_replaces_the_cached_one()
    {
        var (shown, fromCache) = UsageCoordinator.Reconcile(Reading(Now, 31), Reading(Now.AddMinutes(-5), 28));

        Assert.False(fromCache);
        Assert.Equal(31d, shown.Windows[0].UsedPercent);
    }

    [Fact]
    public void A_failure_keeps_the_cached_numbers_with_its_reason()
    {
        var failure = UsageSnapshot.Failure(ProviderId.Claude, SnapshotStatus.Error, "Network error");

        var (shown, fromCache) = UsageCoordinator.Reconcile(failure, Reading(Now, 28));

        Assert.True(fromCache);
        Assert.Equal(28d, shown.Windows[0].UsedPercent);
        Assert.Equal("Network error", shown.Message);
    }

    [Fact]
    public void With_nothing_cached_the_poll_is_shown_as_it_is()
    {
        var failure = UsageSnapshot.Failure(ProviderId.Claude, SnapshotStatus.NoCredentials, "No local Claude usage found");

        var (shown, fromCache) = UsageCoordinator.Reconcile(failure, null);

        Assert.False(fromCache);
        Assert.Same(failure, shown);
    }
}
