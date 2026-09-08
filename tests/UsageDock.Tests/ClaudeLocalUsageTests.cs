using UsageDock.Providers;
using Xunit;

namespace UsageDock.Tests;

public class ClaudeLocalUsageReaderTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 8, 20, 30, 0, TimeSpan.Zero);

    private static long Ms(DateTimeOffset when) => when.ToUnixTimeMilliseconds();

    /// <summary>One sample, shaped exactly like the ones Claude Desktop writes.</summary>
    private static string Sample(long t, int fh, int sd) =>
        "{\"t\":" + t + ",\"org\":\"ignored\",\"u\":{\"fh\":" + fh + ",\"sd\":" + sd + "}}";

    private static string Wrap(params string[] samples) =>
        "{\"version\":2,\"samples\":[" + string.Join(",", samples) + "]}";

    /// <summary>Builds a document from (minutes before now, fh, sd) triples.</summary>
    private static string Document(params (int MinutesAgo, int FiveHour, int SevenDay)[] samples) =>
        Wrap(samples
            .OrderByDescending(s => s.MinutesAgo)
            .Select(s => Sample(Ms(Now.AddMinutes(-s.MinutesAgo)), s.FiveHour, s.SevenDay))
            .ToArray());

    [Fact]
    public void Reads_the_latest_sample_as_the_two_windows()
    {
        var result = ClaudeLocalUsageReader.Parse(
            Document((45, 0, 8), (30, 19, 10), (15, 36, 12), (0, 52, 14)), Now);

        Assert.NotNull(result);
        Assert.Equal(2, result!.Windows.Count);
        Assert.Equal("5 hour", result.Windows[0].Name);
        Assert.Equal(52d, result.Windows[0].UsedPercent);
        Assert.Equal(48d, result.Windows[0].LeftPercent);
        Assert.Equal("Weekly", result.Windows[1].Name);
        Assert.Equal(14d, result.Windows[1].UsedPercent);
    }

    [Fact]
    public void Reports_the_age_of_the_newest_sample()
    {
        var result = ClaudeLocalUsageReader.Parse(Document((60, 5, 5), (20, 9, 6)), Now);

        Assert.Equal(Now.AddMinutes(-20), result!.SampledAt);
    }

    [Fact]
    public void Window_lengths_are_five_hours_and_seven_days()
    {
        var result = ClaudeLocalUsageReader.Parse(Document((0, 10, 10)), Now);

        Assert.Equal(TimeSpan.FromHours(5), result!.Windows[0].WindowLength);
        Assert.Equal(TimeSpan.FromDays(7), result.Windows[1].WindowLength);
    }

    [Fact]
    public void Reset_is_inferred_from_the_start_of_the_current_run()
    {
        // Usage restarts between the 60 and 45 minute samples, so the window opened at their
        // midpoint and rolls five hours later.
        var result = ClaudeLocalUsageReader.Parse(
            Document((75, 0, 3), (60, 0, 3), (45, 4, 4), (30, 19, 5), (0, 52, 6)), Now);

        var expected = Now.AddMinutes(-52.5).Add(TimeSpan.FromHours(5));

        Assert.Equal(expected, result!.Windows[0].ResetsAt);
    }

    [Fact]
    public void A_drop_rather_than_a_zero_also_marks_a_new_window()
    {
        var result = ClaudeLocalUsageReader.Parse(
            Document((60, 62, 8), (45, 5, 8), (30, 13, 9), (0, 20, 9)), Now);

        var expected = Now.AddMinutes(-52.5).Add(TimeSpan.FromHours(5));

        Assert.Equal(expected, result!.Windows[0].ResetsAt);
    }

    [Fact]
    public void No_reset_is_reported_when_nothing_is_accruing()
    {
        var result = ClaudeLocalUsageReader.Parse(Document((30, 0, 0), (0, 0, 0)), Now);

        Assert.Null(result!.Windows[0].ResetsAt);
        Assert.Null(result.Windows[1].ResetsAt);
    }

    [Fact]
    public void No_reset_is_reported_when_the_run_starts_before_the_history()
    {
        // Every retained sample is inside the run, so its start cannot be seen.
        var result = ClaudeLocalUsageReader.Parse(Document((60, 10, 4), (30, 20, 5), (0, 30, 6)), Now);

        Assert.Null(result!.Windows[0].ResetsAt);
    }

    [Fact]
    public void An_estimate_that_has_already_passed_is_dropped()
    {
        // The run opened more than five hours ago, so the estimate is in the past and reporting
        // it would be worse than saying nothing.
        var result = ClaudeLocalUsageReader.Parse(
            Document((600, 0, 1), (595, 5, 2), (0, 40, 3)), Now);

        Assert.Null(result!.Windows[0].ResetsAt);
    }

    [Fact]
    public void Samples_are_ordered_before_the_latest_one_is_taken()
    {
        var unordered = Wrap(
            Sample(Ms(Now), 52, 14),
            Sample(Ms(Now.AddMinutes(-30)), 19, 10));

        var result = ClaudeLocalUsageReader.Parse(unordered, Now);

        Assert.Equal(52d, result!.Windows[0].UsedPercent);
        Assert.Equal(Now, result.SampledAt);
    }

    [Fact]
    public void Out_of_range_percentages_are_clamped()
    {
        var result = ClaudeLocalUsageReader.Parse(Document((0, 250, -4)), Now);

        Assert.Equal(100d, result!.Windows[0].UsedPercent);
        Assert.Equal(0d, result.Windows[1].UsedPercent);
    }

    [Fact]
    public void Malformed_samples_are_skipped_rather_than_failing()
    {
        var mixed = Wrap(
            Sample(0, 9, 9),
            "\"garbage\"",
            "{\"u\":{\"fh\":9,\"sd\":9}}",
            Sample(Ms(Now), 31, 7));

        var result = ClaudeLocalUsageReader.Parse(mixed, Now);

        Assert.Equal(31d, result!.Windows[0].UsedPercent);
    }

    [Fact]
    public void A_missing_usage_field_reads_as_zero()
    {
        var partial = Wrap("{\"t\":" + Ms(Now) + ",\"u\":{\"fh\":12}}");

        var result = ClaudeLocalUsageReader.Parse(partial, Now);

        Assert.Equal(12d, result!.Windows[0].UsedPercent);
        Assert.Equal(0d, result.Windows[1].UsedPercent);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{\"version\":2,\"samples\":[]}")]
    [InlineData("{\"version\":2,\"samples\":\"nope\"}")]
    public void Unusable_documents_read_as_null(string json) =>
        Assert.Null(ClaudeLocalUsageReader.Parse(json, Now));
}
