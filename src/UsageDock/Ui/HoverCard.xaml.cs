using System.Windows;
using System.Windows.Interop;
using UsageDock.Models;
using UsageDock.Native;

namespace UsageDock.Ui;

/// <summary>
/// The detail card shown beside the dock on hover.
///
/// It is deliberately WS_EX_TRANSPARENT, so it is purely informational and can never intercept
/// a click: the mouse always reaches whatever window sits underneath it.
/// </summary>
public partial class HoverCard : Window
{
    /// <summary>Logical width. Must stay in step with <see cref="PercentToWidthConverter.TrackWidth"/>.</summary>
    public const double LogicalWidth = 248d;

    /// <summary>Logical gap between the card and the dock.</summary>
    private const double GapFromDock = 8d;

    private nint _hwnd;

    public HoverCard()
    {
        InitializeComponent();

        // Parked off screen at its real width until the first ShowFor positions it. Without an
        // explicit size WPF picks a large default, which would exist as a big hidden window and
        // flash at the wrong size and place on the very first hover.
        Width = LogicalWidth;
        Height = 40d;
        Left = -32000d;
        Top = -32000d;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        _hwnd = new WindowInteropHelper(this).Handle;
        WindowInterop.ApplyDockStyles(_hwnd);
        MakeClickThrough(_hwnd);
    }

    /// <summary>Adds WS_EX_TRANSPARENT so every hit test falls through this window.</summary>
    private static void MakeClickThrough(nint hwnd)
    {
        var style = NativeMethods.GetWindowLongPtrW(hwnd, NativeMethods.GWL_EXSTYLE);
        NativeMethods.SetWindowLongPtrW(
            hwnd, NativeMethods.GWL_EXSTYLE, style | NativeMethods.WS_EX_TRANSPARENT);
    }

    /// <summary>
    /// Shows the card for a provider beside the dock, on the side away from the screen edge,
    /// aligned with the hovered cell and clamped so it never runs off the monitor work area.
    /// </summary>
    /// <param name="dockBounds">Dock window rectangle, physical pixels.</param>
    /// <param name="cellCentreX">Horizontal centre of the hovered cell, physical pixels.</param>
    /// <param name="cellCentreY">Vertical centre of the hovered cell, physical pixels.</param>
    public void ShowFor(
        ProviderViewModel provider,
        NativeMethods.RECT dockBounds,
        int cellCentreX,
        int cellCentreY,
        DockEdge edge,
        MonitorInfo monitor)
    {
        DataContext = null;
        DataContext = provider;

        if (!IsVisible)
        {
            Show();
            _hwnd = new WindowInteropHelper(this).Handle;
        }

        var width = monitor.ToPhysical(LogicalWidth);
        var height = MeasureHeight(monitor.Scale);
        var gap = monitor.ToPhysical(GapFromDock);

        var (x, y) = edge switch
        {
            DockEdge.Left => (dockBounds.Right + gap, cellCentreY - (height / 2)),
            DockEdge.Top => (cellCentreX - (width / 2), dockBounds.Bottom + gap),
            _ => (dockBounds.Left - width - gap, cellCentreY - (height / 2))
        };

        // Keep the card fully inside the work area on this monitor.
        var work = monitor.WorkArea;
        x = Math.Clamp(x, work.Left, Math.Max(work.Left, work.Right - width));
        y = Math.Clamp(y, work.Top, Math.Max(work.Top, work.Bottom - height));

        WindowInterop.MoveResize(_hwnd, x, y, width, height);
    }

    /// <summary>Measures the content so the window is exactly as tall as what it displays.</summary>
    private int MeasureHeight(double scale)
    {
        if (Content is not FrameworkElement root)
        {
            return (int)Math.Round(160 * scale);
        }

        root.UpdateLayout();
        root.Measure(new Size(LogicalWidth, double.PositiveInfinity));

        var logical = root.DesiredSize.Height;
        if (double.IsNaN(logical) || logical < 40d)
        {
            logical = 160d;
        }

        return (int)Math.Ceiling(logical * scale);
    }

    public void HideCard()
    {
        if (IsVisible)
        {
            Hide();
        }
    }
}
