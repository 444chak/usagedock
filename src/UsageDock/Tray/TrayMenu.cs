using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
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
    private readonly HookInstaller _hooks;
    private Icon? _generatedIcon;

    public TrayMenu(HookInstaller hooks)
    {
        _hooks = hooks;

        _autostartItem = new ToolStripMenuItem("Start with Windows") { CheckOnClick = false };
        _autostartItem.Click += (_, _) => ToggleAutostart();

        _installHooksItem = new ToolStripMenuItem("Install Claude Code hooks");
        _installHooksItem.Click += (_, _) => InstallHooks();

        _uninstallHooksItem = new ToolStripMenuItem("Uninstall Claude Code hooks");
        _uninstallHooksItem.Click += (_, _) => UninstallHooks();

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
            resetItem,
            _autostartItem,
            new ToolStripSeparator(),
            _installHooksItem,
            _uninstallHooksItem,
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

    public event Action? QuitRequested;

    /// <summary>Short balloon free status update shown in the icon tooltip.</summary>
    public void SetTooltip(string text)
    {
        // NotifyIcon truncates past 63 characters and throws beyond 127.
        _icon.Text = text.Length <= 63 ? text : text[..63];
    }

    private void SyncState()
    {
        _autostartItem.Checked = AutostartService.IsEnabled();

        var installed = _hooks.IsInstalled();
        _installHooksItem.Enabled = !installed;
        _uninstallHooksItem.Enabled = installed;
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
        Notify(ok
            ? "Claude Code hooks installed. Restart Claude Code to pick them up."
            : "Could not write Claude Code settings.");
        SyncState();
    }

    private void UninstallHooks()
    {
        var ok = _hooks.Uninstall();
        Log.Info($"Hook uninstall result: {ok}");
        Notify(ok ? "Claude Code hooks removed." : "No UsageDock hooks were installed.");
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
