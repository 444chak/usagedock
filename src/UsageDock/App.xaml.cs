using System.Windows;
using System.Windows.Threading;
using UsageDock.Models;
using UsageDock.Services;
using UsageDock.Tray;
using UsageDock.Ui;

namespace UsageDock;

public partial class App : Application
{
    private const string SingleInstanceMutexName = @"Local\UsageDock.SingleInstance";

    private Mutex? _singleInstance;
    private ConfigService _configService = null!;
    private AppConfig _config = null!;
    private CacheService _cache = null!;
    private ActivityService _activity = null!;
    private UsageCoordinator _coordinator = null!;
    private TrayMenu _tray = null!;
    private MainWindow _dock = null!;
    private Dictionary<ProviderId, ProviderViewModel> _providers = null!;
    private bool _isDuplicateInstance;
    private bool _shuttingDown;
    private string _tooltip = string.Empty;

    [STAThread]
    public static int Main(string[] args)
    {
        // Hook and status line invocations must be as cheap as possible: no WPF, no window, just
        // a file write.
        if (HookEntryPoint.TryHandle(args) || StatusLineEntryPoint.TryHandle(args))
        {
            return 0;
        }

        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstance = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var isFirst);
        if (!isFirst)
        {
            // A second copy would add a second tray icon and a second dock. Bow out quietly,
            // without touching the data folder or the log of the instance that is running.
            _isDuplicateInstance = true;
            Shutdown();
            return;
        }

        AppPaths.EnsureCreated();
        Log.Info("UsageDock starting");
        Log.Info($"Paths: {AppPaths.Describe()}");

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Error("Unhandled exception", args.ExceptionObject as Exception);

        _configService = new ConfigService();
        _config = _configService.Load();
        _cache = new CacheService();
        _activity = new ActivityService();

        _providers = new Dictionary<ProviderId, ProviderViewModel>
        {
            [ProviderId.Claude] = new(ProviderId.Claude),
            [ProviderId.Codex] = new(ProviderId.Codex)
        };

        _dock = new MainWindow(_config, [_providers[ProviderId.Claude], _providers[ProviderId.Codex]]);

        // The dock is the only window, so closing it means quitting. That is also what lets an
        // installer end a running copy cleanly with WM_CLOSE during an upgrade.
        _dock.Closed += (_, _) => RequestShutdown();
        _dock.PlacementChanged += () => _configService.Save(_config);
        _dock.Show();

        var hooks = new HookInstaller();
        var statusLine = new StatusLineInstaller();
        RepointIfMoved("Claude Code hooks", hooks.NeedsRepointing, hooks.Install);
        RepointIfMoved("Claude Code status line feed", statusLine.NeedsRepointing, statusLine.Install);

        _tray = new TrayMenu(hooks, statusLine, _config);
        _tray.RefreshRequested += () => _ = _coordinator.RefreshNowAsync();
        _tray.ResetPositionRequested += OnResetPosition;
        _tray.EdgeRequested += edge => Dispatcher.BeginInvoke(() => _dock.SetEdge(edge));
        _tray.FoldToggleRequested += () => Dispatcher.BeginInvoke(() => _dock.SetCollapsed(!_config.Collapsed));
        _tray.QuitRequested += () => Dispatcher.BeginInvoke(RequestShutdown);

        _coordinator = new UsageCoordinator(_cache, _activity);
        _coordinator.Updated += OnProviderUpdated;
        _coordinator.Start(_config.PollSeconds);
    }

    /// <summary>
    /// Hooks and the status line feed store an absolute path to this executable. If the app has
    /// been moved or reinstalled since they were written, Claude Code would launch something that
    /// is no longer there and Windows would pop its "install .NET" dialog on every event.
    /// Rewriting our own entries to the current path costs one file write and only happens when
    /// the path actually differs.
    /// </summary>
    private static void RepointIfMoved(string what, Func<bool> needsRepointing, Func<bool> install)
    {
        try
        {
            if (!needsRepointing())
            {
                return;
            }

            if (install())
            {
                Log.Info($"{what} re-pointed to the current executable");
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Could not re-point {what}", ex);
        }
    }

    private void RequestShutdown()
    {
        if (_shuttingDown)
        {
            return;
        }

        _shuttingDown = true;
        Shutdown();
    }

    private void OnResetPosition()
    {
        Dispatcher.BeginInvoke(() =>
        {
            _dock.ResetPosition();
            _configService.Save(_config);
        });
    }

    private void OnProviderUpdated(ProviderUpdate update)
    {
        // The coordinator raises this from a timer thread.
        Dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            () =>
            {
                if (!_providers.TryGetValue(update.Provider, out var viewModel))
                {
                    return;
                }

                if (update.Snapshot is { } snapshot)
                {
                    viewModel.Update(snapshot);
                    UpdateTrayTooltip();
                }

                viewModel.UpdateActivity(update.Activity);
            });
    }

    private void UpdateTrayTooltip()
    {
        var parts = _providers.Values.Select(p => $"{p.Name} {p.PercentText}%");
        var text = string.Join("  ", parts);

        if (text == _tooltip)
        {
            return;
        }

        _tooltip = text;
        _tray.SetTooltip(text);
    }

    private static void OnDispatcherUnhandledException(
        object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Unhandled UI exception", e.Exception);

        // A rendering or binding glitch should not take the dock down.
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_isDuplicateInstance)
        {
            base.OnExit(e);
            return;
        }

        Log.Info("UsageDock exiting");

        try
        {
            _configService?.Save(_config);
        }
        catch (Exception ex)
        {
            Log.Error("Could not save config on exit", ex);
        }

        _tray?.Dispose();
        _coordinator?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));
        _singleInstance?.Dispose();

        base.OnExit(e);
    }
}
