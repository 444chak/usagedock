using UsageDock.Models;
using UsageDock.Native;
using UsageDock.Ui;
using Xunit;

namespace UsageDock.Tests;

public class DockGeometryTests
{
    /// <summary>A 1920x1040 work area: a 1080p screen with the taskbar at the bottom.</summary>
    private static readonly NativeMethods.RECT Work = new() { Left = 0, Top = 0, Right = 1920, Bottom = 1040 };

    private static NativeMethods.RECT Place(
        DockEdge edge, double anchor = 0.5d, bool collapsed = false, bool strip = false, double scale = 1d) =>
        DockGeometry.Place(edge, Work, scale, anchor, margin: 8d, cells: 2, collapsed, strip);

    [Fact]
    public void A_right_dock_is_a_column_against_the_right_edge()
    {
        var rect = Place(DockEdge.Right);

        Assert.Equal(58, rect.Width);
        Assert.Equal(156, rect.Height);
        Assert.Equal(1920 - 8, rect.Right);
    }

    [Fact]
    public void A_left_dock_mirrors_the_right_one()
    {
        var right = Place(DockEdge.Right);
        var left = Place(DockEdge.Left);

        Assert.Equal(8, left.Left);
        Assert.Equal(right.Top, left.Top);
        Assert.Equal(right.Height, left.Height);
    }

    [Fact]
    public void A_top_dock_is_a_row_against_the_top_edge()
    {
        var rect = Place(DockEdge.Top);

        Assert.Equal(124, rect.Width);
        Assert.Equal(74, rect.Height);
        Assert.Equal(8, rect.Top);
    }

    [Theory]
    [InlineData(DockEdge.Right)]
    [InlineData(DockEdge.Left)]
    [InlineData(DockEdge.Top)]
    public void Showing_the_strip_grows_the_window_without_moving_the_cells(DockEdge edge)
    {
        var plain = Place(edge);
        var withStrip = Place(edge, strip: true);

        if (edge.IsVertical())
        {
            Assert.Equal(plain.Bottom, withStrip.Bottom);
            Assert.Equal(plain.Top - 16, withStrip.Top);
            Assert.Equal(plain.Width, withStrip.Width);
        }
        else
        {
            Assert.Equal(plain.Right, withStrip.Right);
            Assert.Equal(plain.Left - 16, withStrip.Left);
            Assert.Equal(plain.Height, withStrip.Height);
        }
    }

    [Theory]
    [InlineData(DockEdge.Right)]
    [InlineData(DockEdge.Top)]
    public void The_strip_always_fits_even_at_the_very_start_of_the_edge(DockEdge edge)
    {
        var rect = Place(edge, anchor: 0d, strip: true);

        Assert.Equal(0, edge.IsVertical() ? rect.Top : rect.Left);
    }

    [Fact]
    public void The_end_of_the_edge_is_reachable()
    {
        var rect = Place(DockEdge.Right, anchor: 1d);

        Assert.Equal(1040, rect.Bottom);
    }

    [Theory]
    [InlineData(DockEdge.Right)]
    [InlineData(DockEdge.Left)]
    [InlineData(DockEdge.Top)]
    public void A_folded_dock_is_a_small_tab_flush_with_its_edge_and_centred_on_the_dock(DockEdge edge)
    {
        var open = Place(edge);
        var tab = Place(edge, collapsed: true);

        switch (edge)
        {
            case DockEdge.Right:
                Assert.Equal(1920, tab.Right);
                Assert.Equal((12, 44), (tab.Width, tab.Height));
                break;
            case DockEdge.Left:
                Assert.Equal(0, tab.Left);
                Assert.Equal((12, 44), (tab.Width, tab.Height));
                break;
            default:
                Assert.Equal(0, tab.Top);
                Assert.Equal((44, 12), (tab.Width, tab.Height));
                break;
        }

        if (edge.IsVertical())
        {
            Assert.Equal(open.Top + (open.Height / 2), tab.Top + (tab.Height / 2));
        }
        else
        {
            Assert.Equal(open.Left + (open.Width / 2), tab.Left + (tab.Width / 2));
        }
    }

    [Fact]
    public void The_strip_is_never_added_to_a_folded_tab()
    {
        Assert.Equal(Place(DockEdge.Right, collapsed: true), Place(DockEdge.Right, collapsed: true, strip: true));
    }

    [Fact]
    public void Sizes_scale_with_the_monitor()
    {
        var rect = Place(DockEdge.Right, scale: 1.5d);

        Assert.Equal(87, rect.Width);
        Assert.Equal(234, rect.Height);
    }

    [Theory]
    [InlineData(DockEdge.Right, 0d)]
    [InlineData(DockEdge.Right, 0.3d)]
    [InlineData(DockEdge.Right, 1d)]
    [InlineData(DockEdge.Top, 0.75d)]
    public void Anchor_for_the_placed_position_round_trips(DockEdge edge, double anchor)
    {
        var rect = Place(edge, anchor);
        var start = edge.IsVertical() ? rect.Top : rect.Left;

        var recovered = DockGeometry.AnchorFor(edge, Work, 1d, 2, start);

        Assert.Equal(anchor, recovered, precision: 2);
    }

    [Fact]
    public void Dragging_past_either_end_clamps_the_anchor()
    {
        Assert.Equal(0d, DockGeometry.AnchorFor(DockEdge.Right, Work, 1d, 2, -500));
        Assert.Equal(1d, DockGeometry.AnchorFor(DockEdge.Right, Work, 1d, 2, 5000));
    }
}
