using UsageDock.Models;
using UsageDock.Native;

namespace UsageDock.Ui;

/// <summary>
/// Where the dock window goes and how big it is, in physical pixels, for every edge and state.
/// Kept free of WPF so the placement rules can be tested on their own.
///
/// The window is always exactly the visible shape. The hover strip is not reserved space: it is
/// added only while the cursor is over the dock, and it grows away from the cells so that they
/// stay where they were on screen. Room for it is kept free at the start of the edge, so the
/// strip never pushes the dock into a different place.
/// </summary>
public static class DockGeometry
{
    public const double CellWidth = 56d;
    public const double CellHeight = 72d;

    /// <summary>Space before the first and after the last cell, including the 1px border.</summary>
    public const double Padding = 6d;

    public const double Border = 1d;

    /// <summary>Thickness of the hover strip that holds the grip and the fold arrow.</summary>
    public const double StripThickness = 16d;

    /// <summary>The folded tab: short along the edge, thin across it.</summary>
    public const double TabLength = 44d;

    public const double TabThickness = 12d;

    /// <summary>Logical size of the expanded dock without its strip: (along the edge, across it).</summary>
    public static (double Along, double Across) PillSize(DockEdge edge, int cells) =>
        edge.IsVertical()
            ? ((Padding * 2d) + (CellHeight * cells), CellWidth + (Border * 2d))
            : ((Padding * 2d) + (CellWidth * cells), CellHeight + (Border * 2d));

    /// <summary>The window rectangle for the given state.</summary>
    public static NativeMethods.RECT Place(
        DockEdge edge,
        NativeMethods.RECT work,
        double scale,
        double anchor,
        double margin,
        int cells,
        bool collapsed,
        bool showStrip)
    {
        var (alongLogical, acrossLogical) = PillSize(edge, cells);
        var along = Physical(alongLogical, scale);
        var strip = Physical(StripThickness, scale);
        var pillStart = PillStart(edge, work, along, strip, anchor);

        if (collapsed)
        {
            // Centred where the dock was, so it folds and unfolds in place.
            var tabAlong = Physical(TabLength, scale);
            var tabAcross = Physical(TabThickness, scale);
            var tabStart = pillStart + ((along - tabAlong) / 2);

            return Build(edge, work, tabStart, tabAlong, tabAcross, margin: 0);
        }

        var start = showStrip ? pillStart - strip : pillStart;
        var length = showStrip ? along + strip : along;

        return Build(edge, work, start, length, Physical(acrossLogical, scale), Physical(margin, scale));
    }

    /// <summary>
    /// The anchor that puts the start of the cells (not the strip) at <paramref name="pillStart"/>.
    /// Used while dragging, where the position comes from the cursor.
    /// </summary>
    public static double AnchorFor(DockEdge edge, NativeMethods.RECT work, double scale, int cells, int pillStart)
    {
        var along = Physical(PillSize(edge, cells).Along, scale);
        var strip = Physical(StripThickness, scale);
        var available = Available(edge, work, along, strip);

        if (available <= 0)
        {
            return 0.5d;
        }

        var offset = pillStart - (WorkStart(edge, work) + strip);
        return Math.Clamp(offset / (double)available, 0d, 1d);
    }

    /// <summary>Where the cells begin along the edge. The strip, when shown, sits just before.</summary>
    private static int PillStart(DockEdge edge, NativeMethods.RECT work, int along, int strip, double anchor) =>
        WorkStart(edge, work) + strip + (int)Math.Round(Available(edge, work, along, strip) * Math.Clamp(anchor, 0d, 1d));

    private static int Available(DockEdge edge, NativeMethods.RECT work, int along, int strip) =>
        Math.Max(0, (edge.IsVertical() ? work.Height : work.Width) - along - strip);

    private static int WorkStart(DockEdge edge, NativeMethods.RECT work) =>
        edge.IsVertical() ? work.Top : work.Left;

    private static NativeMethods.RECT Build(
        DockEdge edge, NativeMethods.RECT work, int start, int length, int thickness, int margin)
    {
        return edge switch
        {
            DockEdge.Left => Rect(work.Left + margin, start, thickness, length),
            DockEdge.Top => Rect(start, work.Top + margin, length, thickness),
            _ => Rect(work.Right - thickness - margin, start, thickness, length)
        };
    }

    private static NativeMethods.RECT Rect(int x, int y, int width, int height) =>
        new() { Left = x, Top = y, Right = x + width, Bottom = y + height };

    private static int Physical(double logical, double scale) =>
        (int)Math.Round(logical * scale, MidpointRounding.AwayFromZero);
}
