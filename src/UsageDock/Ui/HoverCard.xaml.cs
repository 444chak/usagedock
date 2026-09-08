using System.Windows;
using System.Windows.Interop;
using UsageDock.Native;

namespace UsageDock.Ui;

/// <summary>
/// The detail card shown to the left of the dock on hover.
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
        Width = LogicalWidth;
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
    /// Shows the card for a provider, left of the dock and vertically aligned with the hovered
    /// cell, clamped so it never runs off the monitor work area.
    /// </summary>
    /// <param name="dockBounds">Dock window rectangle, physical pixels.</param>
    /// <param name="cellCentreY">Vertical centre of the hovered cell, physical pixels.</param>
    public void ShowFor(
        ProviderViewModel provider,
        NativeMethods.RECT dockBounds,
        int cellCentreY,
        MonitorInfo monitor)
    {
        DataContext = null;
        DataContext = provider;

        if (!IsVisible)
        {
            Show();
            _hwnd = new WindowInteropHelper(this).Handle;
        }

        var scale = monitor.Scale;
        var width = monitor.ToPhysical(LogicalWidth);
        var height = MeasureHeight(scale);

        var x = dockBounds.Left - width - monitor.ToPhysical(GapFromDock);
        var y = cellCentreY - (height / 2);

        // Keep the card fully inside the work area on this monitor.
        x = Math.Max(monitor.WorkArea.Left, x);
        y = Math.Clamp(y, monitor.WorkArea.Top, Math.Max(monitor.WorkArea.Top, monitor.WorkArea.Bottom - height));

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
