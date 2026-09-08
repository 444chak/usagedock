namespace UsageDock.Native;

/// <summary>
/// Rounded rectangle containment, used by WM_NCHITTEST so that the few transparent pixels in
/// the corners of the dock pass clicks through to whatever is underneath.
/// </summary>
public static class HitTest
{
    /// <summary>
    /// True when the point lies inside a rounded rectangle anchored at the origin.
    /// Coordinates are client relative, in the same unit as width, height and radius.
    /// </summary>
    public static bool IsInsideRoundedRect(
        double x, double y, double width, double height, double radius)
    {
        if (width <= 0 || height <= 0)
        {
            return false;
        }

        if (x < 0 || y < 0 || x > width || y > height)
        {
            return false;
        }

        var r = Math.Min(radius, Math.Min(width, height) / 2d);
        if (r <= 0)
        {
            return true;
        }

        // Inside the cross shaped core, no corner can be involved.
        if ((x >= r && x <= width - r) || (y >= r && y <= height - r))
        {
            return true;
        }

        // Otherwise the point is in a corner square: measure against that corner circle.
        var cx = x < r ? r : width - r;
        var cy = y < r ? r : height - r;
        var dx = x - cx;
        var dy = y - cy;

        return (dx * dx) + (dy * dy) <= r * r;
    }
}
