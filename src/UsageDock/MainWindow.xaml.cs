using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using UsageDock.Models;
using UsageDock.Native;
using UsageDock.Services;
using UsageDock.Ui;

namespace UsageDock;

/// <summary>
/// The dock itself: a small always on top strip glued to the right edge of a monitor.
///
/// The window is sized to exactly the visible pill and nothing more, so there is no large
/// invisible surface anywhere on screen. The only pixels that are not painted are the four
/// rounded corners, and WM_NCHITTEST reports those as transparent so clicks pass through.
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>Logical size of the visible pill. The window is never bigger than this.</summary>
    private const double LogicalWidth = 58d;

    private const double LogicalCellHeight = 72d;
    private const double LogicalVerticalPadding = 6d;
    private const double LogicalCornerRadius = 10d;

    private readonly List<ProviderCell> _cells = [];
    private readonly HoverCard _card = new();
    private readonly DispatcherTimer _topmostTimer;
    private readonly AppConfig _config;

    private nint _hwnd;
    private NativeMethods.RECT _bounds;
    private double _scale = 1d;

    public MainWindow(AppConfig config, IReadOnlyList<ProviderViewModel> providers)
    {
        _config = config;
        InitializeComponent();

        foreach (var provider in providers)
        {
            var cell = new ProviderCell { DataContext = provider, Height = LogicalCellHeight };
            cell.MouseEnter += (_, _) => ShowCard(cell);
            _cells.Add(cell);
            ProviderStack.Children.Add(cell);
        }

        MouseLeave += (_, _) => _card.HideCard();

        // A dock has to survive other windows claiming topmost. Re-asserting rarely is cheap
        // and avoids the dock quietly sinking behind another always on top app.
        _topmostTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(5)
        };
        _topmostTimer.Tick += (_, _) => WindowInterop.BringToTop(_hwnd);
    }

    private double LogicalHeight => (LogicalVerticalPadding * 2d) + (LogicalCellHeight * _cells.Count);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        _hwnd = new WindowInteropHelper(this).Handle;
        WindowInterop.ApplyDockStyles(_hwnd);

        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);

        Reposition();
        _topmostTimer.Start();
    }

    /// <summary>
    /// Recomputes size and position for the configured monitor. Everything is done in physical
    /// pixels through SetWindowPos, so a secondary display at a different scale lands exactly
    /// on its own right edge.
    /// </summary>
    public void Reposition()
    {
        var monitor = MonitorLayout.Resolve(_config.MonitorDeviceName) ?? MonitorLayout.Primary();
        if (monitor is null)
        {
            return;
        }

        _scale = monitor.Scale;

        var width = monitor.ToPhysical(LogicalWidth);
        var height = monitor.ToPhysical(LogicalHeight);
        var margin = monitor.ToPhysical(_config.EdgeMargin);

        var work = monitor.WorkArea;
        var x = work.Right - width - margin;
        var available = Math.Max(0, work.Height - height);
        var y = work.Top + (int)Math.Round(available * _config.VerticalAnchor);

        _bounds = new NativeMethods.RECT { Left = x, Top = y, Right = x + width, Bottom = y + height };

        WindowInterop.MoveResize(_hwnd, x, y, width, height);
    }

    /// <summary>Moves the dock back to the default position on the primary monitor.</summary>
    public void ResetPosition()
    {
        _config.MonitorDeviceName = string.Empty;
        _config.VerticalAnchor = 0.5d;
        _config.EdgeMargin = 8d;
        Reposition();
    }

    private void ShowCard(ProviderCell cell)
    {
        if (cell.Provider is not { } provider)
        {
            return;
        }

        var monitor = WindowInterop.MonitorFor(_hwnd) ?? MonitorLayout.Primary();
        if (monitor is null)
        {
            return;
        }

        RefreshBounds();

        var index = _cells.IndexOf(cell);
        var cellTop = _bounds.Top
                      + (int)Math.Round(LogicalVerticalPadding * _scale)
                      + (int)Math.Round(index * LogicalCellHeight * _scale);
        var centreY = cellTop + (int)Math.Round(LogicalCellHeight * _scale / 2d);

        _card.ShowFor(provider, _bounds, centreY, monitor);
    }

    private void RefreshBounds()
    {
        if (NativeMethods.GetWindowRect(_hwnd, out var rect))
        {
            _bounds = rect;
        }
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        switch (msg)
        {
            case NativeMethods.WM_NCHITTEST:
            {
                handled = true;
                return HitTestPoint(lParam);
            }

            // Clicking the dock must never pull focus away from the app the user is typing in.
            case NativeMethods.WM_MOUSEACTIVATE:
                handled = true;
                return NativeMethods.MA_NOACTIVATE;

            case NativeMethods.WM_DPICHANGED:
            case NativeMethods.WM_DISPLAYCHANGE:
                Dispatcher.BeginInvoke(Reposition, DispatcherPriority.Background);
                handled = msg == NativeMethods.WM_DPICHANGED;
                return nint.Zero;

            case NativeMethods.WM_SETTINGCHANGE:
                // Covers taskbar auto hide and work area changes.
                Dispatcher.BeginInvoke(Reposition, DispatcherPriority.Background);
                return nint.Zero;

            default:
                return nint.Zero;
        }
    }

    /// <summary>
    /// Reports the rounded corners as transparent so the desktop underneath stays clickable.
    /// </summary>
    private nint HitTestPoint(nint lParam)
    {
        RefreshBounds();

        var screenX = (short)(lParam.ToInt64() & 0xFFFF);
        var screenY = (short)((lParam.ToInt64() >> 16) & 0xFFFF);

        var x = screenX - _bounds.Left;
        var y = screenY - _bounds.Top;

        var inside = HitTest.IsInsideRoundedRect(
            x, y, _bounds.Width, _bounds.Height, LogicalCornerRadius * _scale);

        if (!inside)
        {
            _card.HideCard();
            return NativeMethods.HTTRANSPARENT;
        }

        return NativeMethods.HTCLIENT;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        // Swallowed on purpose: the dock is a read only indicator and must not take focus.
        e.Handled = true;
    }

    protected override void OnClosed(EventArgs e)
    {
        _topmostTimer.Stop();
        _card.Close();
        base.OnClosed(e);
    }
}
