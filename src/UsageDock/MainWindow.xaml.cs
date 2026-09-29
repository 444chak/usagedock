using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using UsageDock.Models;
using UsageDock.Native;
using UsageDock.Services;
using UsageDock.Ui;

namespace UsageDock;

/// <summary>
/// The dock itself: a small always on top strip glued to the right, left or top edge of a
/// monitor.
///
/// The window is sized to exactly the visible pill and nothing more, so there is no large
/// invisible surface anywhere on screen. The only pixels that are not painted are the four
/// rounded corners, and WM_NCHITTEST reports those as transparent so clicks pass through.
///
/// While the cursor is over it, a thin strip appears at the start of the dock with a grip to
/// drag it along its edge and an arrow to fold it away into a small tab. The strip is part of
/// the window only while it is visible; see <see cref="DockGeometry"/>.
/// </summary>
public partial class MainWindow : Window
{
    private const double PillCornerRadius = 10d;

    /// <summary>Matches DWMWCP_ROUNDSMALL, which the tab uses.</summary>
    private const double TabCornerRadius = 4d;

    private readonly List<ProviderCell> _cells = [];
    private readonly HoverCard _card = new();
    private readonly DispatcherTimer _topmostTimer;
    private readonly AppConfig _config;

    private nint _hwnd;
    private NativeMethods.RECT _bounds;
    private double _scale = 1d;
    private bool _stripShown;

    private bool _dragging;

    /// <summary>Cursor distance from the start of the cells when the drag began, physical pixels.</summary>
    private int _grabOffset;

    public MainWindow(AppConfig config, IReadOnlyList<ProviderViewModel> providers)
    {
        _config = config;
        InitializeComponent();

        foreach (var provider in providers)
        {
            var cell = new ProviderCell { DataContext = provider };
            cell.MouseEnter += (_, _) => ShowCard(cell);
            _cells.Add(cell);
            ProviderStack.Children.Add(cell);
        }

        MouseEnter += (_, _) => SetStripShown(true);
        MouseLeave += (_, _) => OnDockMouseLeave();

        Strip.MouseEnter += (_, _) => _card.HideCard();
        Strip.MouseLeftButtonDown += BeginDrag;
        Strip.MouseMove += ContinueDrag;
        Strip.MouseLeftButtonUp += (_, e) => EndDrag(e);
        Strip.LostMouseCapture += (_, _) => EndDrag(null);

        FoldButton.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            SetCollapsed(true);
        };

        Tab.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            SetCollapsed(false);
        };

        ApplyEdgeLayout();
        ApplyCollapsedLayout();

        // A dock has to survive other windows claiming topmost. Re-asserting rarely is cheap
        // and avoids the dock quietly sinking behind another always on top app.
        _topmostTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(5)
        };
        _topmostTimer.Tick += (_, _) => WindowInterop.BringToTop(_hwnd);
    }

    /// <summary>Raised when the user moved or folded the dock, so the change can be saved.</summary>
    public event Action? PlacementChanged;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        _hwnd = new WindowInteropHelper(this).Handle;
        WindowInterop.ApplyDockStyles(_hwnd);
        ApplyCornerPreference();

        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);

        Reposition();
        _topmostTimer.Start();
    }

    /// <summary>
    /// Recomputes size and position for the configured monitor. Everything is done in physical
    /// pixels through SetWindowPos, so a secondary display at a different scale lands exactly
    /// on its own edge.
    /// </summary>
    public void Reposition()
    {
        var monitor = MonitorLayout.Resolve(_config.MonitorDeviceName) ?? MonitorLayout.Primary();
        if (monitor is null)
        {
            return;
        }

        _scale = monitor.Scale;

        _bounds = DockGeometry.Place(
            _config.Edge,
            monitor.WorkArea,
            monitor.Scale,
            _config.Anchor,
            _config.EdgeMargin,
            _cells.Count,
            _config.Collapsed,
            _stripShown && !_config.Collapsed);

        WindowInterop.MoveResize(_hwnd, _bounds.Left, _bounds.Top, _bounds.Width, _bounds.Height);
    }

    /// <summary>Moves the dock back to the default position on the primary monitor, keeping its edge.</summary>
    public void ResetPosition()
    {
        _config.MonitorDeviceName = string.Empty;
        _config.Anchor = 0.5d;
        _config.EdgeMargin = 8d;
        _config.Collapsed = false;
        ApplyCollapsedLayout();
        Reposition();
    }

    public void SetEdge(DockEdge edge)
    {
        if (_config.Edge == edge)
        {
            return;
        }

        _config.Edge = edge;
        _card.HideCard();
        ApplyEdgeLayout();
        Reposition();
        PlacementChanged?.Invoke();
    }

    public void SetCollapsed(bool collapsed)
    {
        if (_config.Collapsed == collapsed)
        {
            return;
        }

        _config.Collapsed = collapsed;
        _card.HideCard();
        EndDrag(null);
        ApplyCollapsedLayout();
        Reposition();
        PlacementChanged?.Invoke();
    }

    /// <summary>
    /// Lays the cells and the strip out along the edge: a column on the sides, a row on top,
    /// with the strip at the start and the fold arrow on the side that faces the screen edge.
    /// </summary>
    private void ApplyEdgeLayout()
    {
        var edge = _config.Edge;
        var vertical = edge.IsVertical();

        ProviderStack.Orientation = vertical ? Orientation.Vertical : Orientation.Horizontal;
        ProviderStack.Margin = vertical ? new Thickness(0, 5, 0, 5) : new Thickness(5, 0, 5, 0);

        foreach (var cell in _cells)
        {
            cell.Width = DockGeometry.CellWidth;
            cell.Height = DockGeometry.CellHeight;
        }

        DockPanel.SetDock(Strip, vertical ? Dock.Top : Dock.Left);
        Strip.Width = vertical ? double.NaN : DockGeometry.StripThickness;
        Strip.Height = vertical ? DockGeometry.StripThickness : double.NaN;
        Strip.Cursor = vertical ? Cursors.SizeNS : Cursors.SizeWE;

        Grip.Width = vertical ? 18 : 3;
        Grip.Height = vertical ? 3 : 18;

        FoldButton.HorizontalAlignment = edge switch
        {
            DockEdge.Left => HorizontalAlignment.Left,
            DockEdge.Right => HorizontalAlignment.Right,
            _ => HorizontalAlignment.Center
        };
        FoldButton.VerticalAlignment = vertical ? VerticalAlignment.Center : VerticalAlignment.Top;
        FoldButton.Margin = edge switch
        {
            DockEdge.Left => new Thickness(3, 0, 0, 0),
            DockEdge.Right => new Thickness(0, 0, 3, 0),
            _ => new Thickness(0, 3, 0, 0)
        };

        // The fold arrow points at the edge it folds into, the tab arrow away from it.
        (FoldRotation.Angle, TabRotation.Angle) = edge switch
        {
            DockEdge.Left => (180d, 0d),
            DockEdge.Top => (270d, 90d),
            _ => (0d, 180d)
        };
    }

    private void ApplyCollapsedLayout()
    {
        var collapsed = _config.Collapsed;

        if (collapsed)
        {
            _stripShown = false;
            Strip.Visibility = Visibility.Collapsed;
        }

        Expanded.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        Tab.Visibility = collapsed ? Visibility.Visible : Visibility.Collapsed;
        Frame.CornerRadius = new CornerRadius(collapsed ? TabCornerRadius : PillCornerRadius);
        ApplyCornerPreference();
    }

    private void ApplyCornerPreference() =>
        WindowInterop.RoundCorners(
            _hwnd,
            _config.Collapsed
                ? NativeMethods.DWM_WINDOW_CORNER_PREFERENCE.DWMWCP_ROUNDSMALL
                : NativeMethods.DWM_WINDOW_CORNER_PREFERENCE.DWMWCP_ROUND);

    private void SetStripShown(bool shown)
    {
        if (_config.Collapsed || _stripShown == shown)
        {
            return;
        }

        _stripShown = shown;
        Strip.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        Reposition();
    }

    /// <summary>
    /// Growing the window under the cursor can make WPF report a leave that did not happen,
    /// which would hide the strip, shrink the window, and start the cycle again. Only a cursor
    /// that is really outside the window counts.
    /// </summary>
    private void OnDockMouseLeave()
    {
        _card.HideCard();

        if (_dragging || CursorIsInside())
        {
            return;
        }

        SetStripShown(false);
    }

    private bool CursorIsInside()
    {
        RefreshBounds();

        return NativeMethods.GetCursorPos(out var point)
               && point.X >= _bounds.Left && point.X < _bounds.Right
               && point.Y >= _bounds.Top && point.Y < _bounds.Bottom;
    }

    private void BeginDrag(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;

        // Where the button went down, not where the cursor is by the time this runs: a quick
        // flick can already be far away, and the grab point would be off by that much.
        var packed = NativeMethods.GetMessagePos();
        var point = new NativeMethods.POINT { X = (short)(packed & 0xFFFF), Y = (short)(packed >> 16) };

        RefreshBounds();

        // The strip is showing, so the cells start one strip further along.
        var strip = (int)Math.Round(DockGeometry.StripThickness * _scale, MidpointRounding.AwayFromZero);
        var pillStart = (_config.Edge.IsVertical() ? _bounds.Top : _bounds.Left) + strip;

        _grabOffset = Along(point) - pillStart;
        _dragging = Strip.CaptureMouse();
    }

    private void ContinueDrag(object sender, MouseEventArgs e)
    {
        if (_dragging)
        {
            FollowCursor();
        }
    }

    private void FollowCursor()
    {
        if (!NativeMethods.GetCursorPos(out var point))
        {
            return;
        }

        // Following the cursor across displays lets the dock be moved to another monitor.
        var monitor = MonitorLayout.UnderCursor()
                      ?? MonitorLayout.Resolve(_config.MonitorDeviceName)
                      ?? MonitorLayout.Primary();
        if (monitor is null)
        {
            return;
        }

        _config.MonitorDeviceName = monitor.DeviceName;
        _config.Anchor = DockGeometry.AnchorFor(
            _config.Edge, monitor.WorkArea, monitor.Scale, _cells.Count, Along(point) - _grabOffset);

        Reposition();
    }

    private void EndDrag(MouseButtonEventArgs? e)
    {
        if (!_dragging)
        {
            return;
        }

        if (e is not null)
        {
            e.Handled = true;

            // The release can be handled before the last move, which would leave the dock
            // short of where the cursor let go.
            FollowCursor();
        }

        _dragging = false;

        if (Strip.IsMouseCaptured)
        {
            Strip.ReleaseMouseCapture();
        }

        PlacementChanged?.Invoke();

        if (!CursorIsInside())
        {
            SetStripShown(false);
        }
    }

    private int Along(NativeMethods.POINT point) => _config.Edge.IsVertical() ? point.Y : point.X;

    private void ShowCard(ProviderCell cell)
    {
        if (_dragging || cell.Provider is not { } provider)
        {
            return;
        }

        var monitor = WindowInterop.MonitorFor(_hwnd) ?? MonitorLayout.Primary();
        if (monitor is null)
        {
            return;
        }

        // The strip may have just appeared; the cell's position has to reflect it.
        UpdateLayout();
        RefreshBounds();

        var centre = cell.PointToScreen(new Point(cell.ActualWidth / 2d, cell.ActualHeight / 2d));

        _card.ShowFor(
            provider,
            _bounds,
            (int)Math.Round(centre.X),
            (int)Math.Round(centre.Y),
            _config.Edge,
            monitor);
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

        var radius = _config.Collapsed ? TabCornerRadius : PillCornerRadius;
        var inside = HitTest.IsInsideRoundedRect(x, y, _bounds.Width, _bounds.Height, radius * _scale);

        if (!inside)
        {
            _card.HideCard();
            return NativeMethods.HTTRANSPARENT;
        }

        return NativeMethods.HTCLIENT;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        // Swallowed on purpose: the dock must not take focus. The strip and the tab handle
        // their own clicks before this is reached.
        e.Handled = true;
    }

    protected override void OnClosed(EventArgs e)
    {
        _topmostTimer.Stop();
        _card.Close();
        base.OnClosed(e);
    }
}
