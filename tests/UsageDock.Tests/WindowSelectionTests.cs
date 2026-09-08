using UsageDock.Models;
using Xunit;

namespace UsageDock.Tests;

/// <summary>
/// The dock draws two rings, so a snapshot has to say which window is the short one and which
/// is the long one regardless of which provider produced it.
/// </summary>
public class WindowSelectionTests
{
    private static UsageSnapshot Snapshot(params QuotaWindow[] windows) => new()
    {
        Provider = ProviderId.Claude,
        Status = SnapshotStatus.Ok,
        Windows = windows
    };

    [Fact]
    public void Windows_are_matched_by_their_declared_length()
    {
        var snapshot = Snapshot(
            QuotaWindow.Create("Weekly", 14d, null, TimeSpan.FromDays(7)),
            QuotaWindow.Create("5 hour", 64d, null, TimeSpan.FromHours(5)));

        Assert.Equal(64d, snapshot.ShortWindow!.UsedPercent);
        Assert.Equal(14d, snapshot.LongWindow!.UsedPercent);
    }

    [Fact]
    public void Length_wins_over_a_misleading_label()
    {
        var snapshot = Snapshot(
            QuotaWindow.Create("Short window", 30d, null, TimeSpan.FromHours(5)),
            QuotaWindow.Create("Long window", 40d, null, TimeSpan.FromDays(7)));

        Assert.Equal(30d, snapshot.ShortWindow!.UsedPercent);
        Assert.Equal(40d, snapshot.LongWindow!.UsedPercent);
    }

    [Fact]
    public void Labels_are_used_when_no_length_is_reported()
    {
        var snapshot = Snapshot(
            QuotaWindow.Create("5 hour", 21d),
            QuotaWindow.Create("Weekly", 9d));

        Assert.Equal(21d, snapshot.ShortWindow!.UsedPercent);
        Assert.Equal(9d, snapshot.LongWindow!.UsedPercent);
    }

    [Fact]
    public void The_opus_weekly_window_also_counts_as_long()
    {
        var snapshot = Snapshot(QuotaWindow.Create("Weekly (Opus)", 12d));

        Assert.Equal(12d, snapshot.LongWindow!.UsedPercent);
    }

    [Fact]
    public void A_lone_window_becomes_the_short_one_and_there_is_no_long_one()
    {
        var snapshot = Snapshot(QuotaWindow.Create("Rolling", 55d));

        Assert.Equal(55d, snapshot.ShortWindow!.UsedPercent);
        Assert.Null(snapshot.LongWindow);
    }

    [Fact]
    public void An_empty_snapshot_has_neither()
    {
        var snapshot = Snapshot();

        Assert.Null(snapshot.ShortWindow);
        Assert.Null(snapshot.LongWindow);
    }
}
