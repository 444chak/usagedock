using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using UsageDock.Models;
using UsageDock.Services;

namespace UsageDock.Tray;

/// <summary>
/// Notification area icon and its menu. The icon bitmap is drawn at runtime, so there is no
/// binary asset to keep in sync with the build.
/// </summary>
public sealed class TrayMenu : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _autostartItem;
    private readonly ToolStripMenuItem _installHooksItem;
    private readonly ToolStripMenuItem _uninstallHooksItem;
    private readonly ToolStripMenuItem _statusLineItem;
    private readonly ToolStripMenuItem _foldItem;
    private readonly Dictionary<DockEdge, ToolStripMenuItem> _edgeItems = [];
    private readonly HookInstaller _hooks;
    private readonly StatusLineInstaller _statusLine;
    private readonly AppConfig _config;
    private Icon? _generatedIcon;

    public TrayMenu(HookInstaller hooks, StatusLineInstaller statusLine, AppConfig config)
    {
        _hooks = hooks;
        _statusLine = statusLine;
        _config = config;

        var positionItem = new ToolStripMenuItem("Dock position");
        foreach (var edge in (DockEdge[])[DockEdge.Right, DockEdge.Left, DockEdge.Top])
        {
            var item = new ToolStripMenuItem(edge.DisplayName());
            item.Click += (_, _) => EdgeRequested?.Invoke(edge);
            _edgeItems[edge] = item;
            positionItem.DropDownItems.Add(item);
        }

        _foldItem = new ToolStripMenuItem("Fold away");
        _foldItem.Click += (_, _) => FoldToggleRequested?.Invoke();

        _autostartItem = new ToolStripMenuItem("Start with Windows") { CheckOnClick = false };
        _autostartItem.Click += (_, _) => ToggleAutostart();

        _installHooksItem = new ToolStripMenuItem("Install Claude Code hooks");
        _installHooksItem.Click += (_, _) => InstallHooks();

        _uninstallHooksItem = new ToolStripMenuItem("Uninstall Claude Code hooks");
        _uninstallHooksItem.Click += (_, _) => UninstallHooks();

        _statusLineItem = new ToolStripMenuItem("Live Claude usage from Claude Code") { CheckOnClick = false };
        _statusLineItem.Click += (_, _) => ToggleStatusLine();

        var refreshItem = new ToolStripMenuItem("Refresh usage now");
        refreshItem.Click += (_, _) => RefreshRequested?.Invoke();

        var resetItem = new ToolStripMenuItem("Reset position");
        resetItem.Click += (_, _) => ResetPositionRequested?.Invoke();

        var folderItem = new ToolStripMenuItem("Open data folder");
        folderItem.Click += (_, _) => OpenDataFolder();

        var quitItem = new ToolStripMenuItem("Quit");
        quitItem.Click += (_, _) => QuitRequested?.Invoke();

        var menu = new ContextMenuStrip();
        menu.Items.AddRange(
        [
            refreshItem,
            positionItem,
            _foldItem,
            resetItem,
            _autostartItem,
            new ToolStripSeparator(),
            _installHooksItem,
            _uninstallHooksItem,
            _statusLineItem,
            new ToolStripSeparator(),
            folderItem,
            quitItem
        ]);

        menu.Opening += (_, _) => SyncState();

        _generatedIcon = CreateIcon();

        _icon = new NotifyIcon
        {
            Icon = _generatedIcon,
            Text = "UsageDock",
            Visible = true,
            ContextMenuStrip = menu
        };

        _icon.MouseDoubleClick += (_, _) => RefreshRequested?.Invoke();

        SyncState();
    }

    public event Action? RefreshRequested;

    public event Action? ResetPositionRequested;

    public event Action<DockEdge>? EdgeRequested;

    public event Action? FoldToggleRequested;

    public event Action? QuitRequested;

    /// <summary>Short balloon free status update shown in the icon tooltip.</summary>
    public void SetTooltip(string text)
    {
        // NotifyIcon truncates past 63 characters and throws beyond 127.
        _icon.Text = text.Length <= 63 ? text : text[..63];
    }

    private void SyncState()
    {
        foreach (var (edge, item) in _edgeItems)
        {
            item.Checked = _config.Edge == edge;
        }

        _foldItem.Checked = _config.Collapsed;

        _autostartItem.Checked = AutostartService.IsEnabled();

        var installed = _hooks.IsInstalled();
        _installHooksItem.Enabled = !installed;
        _uninstallHooksItem.Enabled = installed;

        _statusLineItem.Checked = _statusLine.IsInstalled();
    }

    private void ToggleAutostart()
    {
        var target = !AutostartService.IsEnabled();
        if (AutostartService.SetEnabled(target))
        {
            Log.Info($"Autostart set to {target}");
        }

        SyncState();
    }

    private void InstallHooks()
    {
        var ok = _hooks.Install();
        Log.Info($"Hook install result: {ok}");

        if (!ok)
        {
            Notify("Could not write Claude Code settings.");
        }
        else if (_hooks.IsRunningFromBuildOutput())
        {
            // Hooks record an absolute path. Pointing them into bin\Release means the next clean
            // build deletes the target from under Claude Code.
            Notify("Hooks installed, but this copy runs from a build folder. "
                   + "Copy the app somewhere permanent and start it from there.");
        }
        else
        {
            Notify("Claude Code hooks installed. Restart Claude Code to pick them up.");
        }

        SyncState();
    }

    private void UninstallHooks()
    {
        var ok = _hooks.Uninstall();
        Log.Info($"Hook uninstall result: {ok}");
        Notify(ok ? "Claude Code hooks removed." : "No UsageDock hooks were installed.");
        SyncState();
    }

    private void ToggleStatusLine()
    {
        if (_statusLine.IsInstalled())
        {
            var removed = _statusLine.Uninstall();
            Log.Info($"Status line feed uninstall result: {removed}");
            Notify(removed
                ? "Claude Code status line restored to what it was."
                : "Could not write Claude Code settings.");
        }
        else
        {
            var installed = _statusLine.Install();
            Log.Info($"Status line feed install result: {installed}");
            Notify(installed
                ? "Claude usage now follows Claude Code in a terminal, live. "
                  + "Restart Claude Code to pick it up."
                : "Could not set up the Claude Code status line, see the log.");
        }

        SyncState();
    }

    private void Notify(string message)
    {
        _icon.BalloonTipTitle = "UsageDock";
        _icon.BalloonTipText = message;
        _icon.ShowBalloonTip(4000);
    }

    private static void OpenDataFolder()
    {
        try
        {
            AppPaths.EnsureCreated();
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = AppPaths.DataDirectory,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Log.Error("Could not open data folder", ex);
        }
    }

    /// <summary>Draws a 32x32 tray glyph: a ring on a dark rounded square.</summary>
    private static Icon CreateIcon()
    {
        using var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            using var background = new SolidBrush(Color.FromArgb(235, 24, 26, 32));
            using var path = RoundedRect(new Rectangle(1, 1, 30, 30), 8);
            g.FillPath(background, path);

            using var track = new Pen(Color.FromArgb(70, 255, 255, 255), 3.5f);
            g.DrawEllipse(track, 8, 8, 16, 16);

            using var progress = new Pen(Color.FromArgb(255, 74, 222, 128), 3.5f)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            };
            g.DrawArc(progress, 8, 8, 16, 16, -90, 250);
        }

        return Icon.FromHandle(bitmap.GetHicon());
    }

    private static GraphicsPath RoundedRect(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        var d = radius * 2;

        path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();

        return path;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _generatedIcon?.Dispose();
        _generatedIcon = null;
    }
}
