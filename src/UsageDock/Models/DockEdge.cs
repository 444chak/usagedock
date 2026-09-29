namespace UsageDock.Models;

/// <summary>The screen edge the dock is glued to.</summary>
public enum DockEdge
{
    Right,
    Left,
    Top
}

public static class DockEdgeExtensions
{
    /// <summary>True for the side edges, where the dock is a column and moves up and down.</summary>
    public static bool IsVertical(this DockEdge edge) => edge != DockEdge.Top;

    public static string DisplayName(this DockEdge edge) => edge switch
    {
        DockEdge.Left => "Left",
        DockEdge.Top => "Top",
        _ => "Right"
    };
}
