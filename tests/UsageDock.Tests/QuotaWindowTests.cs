using UsageDock.Models;
using Xunit;

namespace UsageDock.Tests;

public class QuotaWindowTests
{
    [Theory]
    [InlineData(0d, 0d)]
    [InlineData(42.5d, 42.5d)]
    [InlineData(100d, 100d)]
    [InlineData(-3d, 0d)]
    [InlineData(1000d, 100d)]
    [InlineData(double.NaN, 0d)]
    // Non finite input means the source was garbage, so it reads as 0 rather than as a
    // full ring that would look like an exhausted quota.
    [InlineData(double.PositiveInfinity, 0d)]
    [InlineData(double.NegativeInfinity, 0d)]
    public void Clamp_keeps_percentages_in_range(double input, double expected) =>
        Assert.Equal(expected, QuotaWindow.Clamp(input));

    [Theory]
    [InlineData(0d, 0d)]
    [InlineData(0.235d, 23.5d)]
    [InlineData(1d, 100d)]
    [InlineData(1.5d, 100d)]
    public void FromRatio_scales_to_percent(double ratio, double expected) =>
        Assert.Equal(expected, QuotaWindow.FromRatio(ratio), precision: 6);

    [Fact]
    public void Left_percent_is_the_complement_of_used()
    {
        var window = QuotaWindow.Create("5 hour", 23.5d);

        Assert.Equal(23.5d, window.UsedPercent);
        Assert.Equal(76.5d, window.LeftPercent);
    }

    [Fact]
    public void Create_clamps_out_of_range_input()
    {
        Assert.Equal(100d, QuotaWindow.Create("w", 140d).UsedPercent);
        Assert.Equal(0d, QuotaWindow.Create("w", -5d).UsedPercent);
    }

    public static TheoryData<int, string> ResetCases => new()
    {
        { 0, "now" },
        { -60, "now" },
        { 30, "<1m" },
        { 90, "1m" },
        { 60 * 45, "45m" },
        { (60 * 60 * 2) + (60 * 14), "2h 14m" },
        { 60 * 60 * 30, "1d 6h" }
    };

    [Theory]
    [MemberData(nameof(ResetCases))]
    public void FormatResetIn_describes_the_countdown(int secondsFromNow, string expected)
    {
        var now = new DateTimeOffset(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);
        var window = QuotaWindow.Create("w", 10d, now.AddSeconds(secondsFromNow));

        Assert.Equal(expected, window.FormatResetIn(now));
    }

    [Fact]
    public void FormatResetIn_reports_unknown_without_a_reset()
    {
        var window = QuotaWindow.Create("w", 10d);

        Assert.Equal("unknown", window.FormatResetIn(DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Primary_window_is_the_most_used_one()
    {
        var snapshot = new UsageSnapshot
        {
            Provider = ProviderId.Codex,
            Status = SnapshotStatus.Ok,
            Windows =
            [
                QuotaWindow.Create("5 hour", 6d),
                QuotaWindow.Create("Weekly", 19d)
            ]
        };

        Assert.Equal("Weekly", snapshot.Primary?.Name);
    }

    [Fact]
    public void Primary_is_null_without_windows() =>
        Assert.Null(UsageSnapshot.Failure(ProviderId.Claude, SnapshotStatus.Error, "x").Primary);
}
