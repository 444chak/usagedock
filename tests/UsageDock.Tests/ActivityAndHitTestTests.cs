using UsageDock.Models;
using UsageDock.Native;
using UsageDock.Services;
using Xunit;

namespace UsageDock.Tests;

public class ActivityStateTests
{
    [Theory]
    [InlineData("working", ActivityState.Working)]
    [InlineData("running", ActivityState.Working)]
    [InlineData("WORKING", ActivityState.Working)]
    [InlineData(" waiting ", ActivityState.Waiting)]
    [InlineData("attention", ActivityState.Waiting)]
    [InlineData("idle", ActivityState.Idle)]
    [InlineData("done", ActivityState.Idle)]
    [InlineData("nonsense", ActivityState.Unknown)]
    [InlineData(null, ActivityState.Unknown)]
    public void Hook_state_words_map_onto_states(string? value, ActivityState expected) =>
        Assert.Equal(expected, ActivityRecord.ParseState(value));

    [Fact]
    public void A_fresh_working_record_stays_working()
    {
        var now = DateTimeOffset.UtcNow;
        var record = new ActivityRecord { State = "working", At = now.AddMinutes(-1) };

        Assert.Equal(ActivityState.Working, record.ResolveState(now));
    }

    [Fact]
    public void A_stale_working_record_decays_to_idle()
    {
        var now = DateTimeOffset.UtcNow;
        var stale = now - ActivityRecord.StaleAfter.Add(TimeSpan.FromSeconds(1));
        var record = new ActivityRecord { State = "working", At = stale };

        Assert.Equal(ActivityState.Idle, record.ResolveState(now));
    }

    [Fact]
    public void A_stale_idle_record_stays_idle()
    {
        var now = DateTimeOffset.UtcNow;
        var record = new ActivityRecord { State = "idle", At = now.AddHours(-4) };

        Assert.Equal(ActivityState.Idle, record.ResolveState(now));
    }

    [Fact]
    public void Codex_activity_follows_the_freshness_of_its_session_log()
    {
        var now = new DateTimeOffset(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);
        var service = new ActivityService(activityDirectory: Path.GetTempPath(), now: () => now);
        var stale = now - ActivityService.CodexActiveWindow.Add(TimeSpan.FromSeconds(1));

        Assert.Equal(ActivityState.Working, service.InferFromLastWrite(now.AddSeconds(-10)));
        Assert.Equal(ActivityState.Idle, service.InferFromLastWrite(stale));
        Assert.Equal(ActivityState.Idle, service.InferFromLastWrite(null));
    }
}

public class HitTestTests
{
    private const double Width = 46d;
    private const double Height = 120d;
    private const double Radius = 10d;

    [Theory]
    [InlineData(23, 60)]
    [InlineData(23, 0)]
    [InlineData(0, 60)]
    [InlineData(46, 60)]
    [InlineData(10, 10)]
    public void Points_on_the_visible_pill_are_inside(double x, double y) =>
        Assert.True(HitTest.IsInsideRoundedRect(x, y, Width, Height, Radius));

    [Theory]
    [InlineData(0, 0)]
    [InlineData(46, 0)]
    [InlineData(0, 120)]
    [InlineData(46, 120)]
    [InlineData(1.5, 1.5)]
    public void The_rounded_corners_are_outside(double x, double y) =>
        Assert.False(HitTest.IsInsideRoundedRect(x, y, Width, Height, Radius));

    [Theory]
    [InlineData(-1, 60)]
    [InlineData(47, 60)]
    [InlineData(23, -1)]
    [InlineData(23, 121)]
    public void Points_beyond_the_window_are_outside(double x, double y) =>
        Assert.False(HitTest.IsInsideRoundedRect(x, y, Width, Height, Radius));

    [Fact]
    public void A_zero_radius_behaves_like_a_plain_rectangle() =>
        Assert.True(HitTest.IsInsideRoundedRect(0, 0, Width, Height, 0));

    [Fact]
    public void A_degenerate_window_is_never_hit()
    {
        Assert.False(HitTest.IsInsideRoundedRect(0, 0, 0, Height, Radius));
        Assert.False(HitTest.IsInsideRoundedRect(0, 0, Width, 0, Radius));
    }

    [Fact]
    public void The_radius_cannot_exceed_half_the_shortest_side() =>
        Assert.True(HitTest.IsInsideRoundedRect(Width / 2d, Height / 2d, Width, Height, 500d));
}
