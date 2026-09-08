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

    [STAThread]
    public static int Main(string[] args)
    {
        // Hook invocations must be as cheap as possible: no WPF, no window, just a file write.
        if (HookEntryPoint.TryHandle(args))
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
            // A second copy would add a second tray icon and a second dock. Bow out quietly.
            Shutdown();
            return;
        }

        AppPaths.EnsureCreated();
        Log.Info("UsageDock starting");

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Error("Unhandled exception", args.ExceptionObject as Exception);

        _configService = new ConfigService();
        _config = _configService.Load();
        _cache = new CacheService();
        _activity = new ActivityService();

        _providers = new Dictionary<ProviderId, ProviderViewModel>
        {
            [ProviderId.Claude] = new(ProviderId.Claude, "C"),
            [ProviderId.Codex] = new(ProviderId.Codex, "X")
        };

        _dock = new MainWindow(_config, [_providers[ProviderId.Claude], _providers[ProviderId.Codex]]);
        _dock.Show();

        _tray = new TrayMenu(new HookInstaller());
        _tray.RefreshRequested += () => _ = _coordinator.RefreshNowAsync();
        _tray.ResetPositionRequested += OnResetPosition;
        _tray.QuitRequested += () => Dispatcher.BeginInvoke(Shutdown);

        _coordinator = new UsageCoordinator(_cache, _activity);
        _coordinator.Updated += OnProviderUpdated;
        _coordinator.Start(_config.PollSeconds);
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

                viewModel.Update(update.Snapshot);
                viewModel.UpdateActivity(update.Activity);
                UpdateTrayTooltip();
            });
    }

    private void UpdateTrayTooltip()
    {
        var parts = _providers.Values.Select(p => $"{p.Name} {p.PercentText}%");
        _tray.SetTooltip(string.Join("  ", parts));
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
