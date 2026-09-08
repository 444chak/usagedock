using System.Windows;
using UsageDock.Models;
using UsageDock.Services;
using UsageDock.Ui;

namespace UsageDock;

public partial class App : Application
{
    private ConfigService _configService = null!;
    private AppConfig _config = null!;
    private MainWindow _dock = null!;
    private Dictionary<ProviderId, ProviderViewModel> _providers = null!;

    [STAThread]
    public static int Main()
    {
        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        AppPaths.EnsureCreated();
        Log.Info("UsageDock starting");

        _configService = new ConfigService();
        _config = _configService.Load();

        _providers = new Dictionary<ProviderId, ProviderViewModel>
        {
            [ProviderId.Claude] = new(ProviderId.Claude, "C"),
            [ProviderId.Codex] = new(ProviderId.Codex, "X")
        };

        _dock = new MainWindow(_config, [_providers[ProviderId.Claude], _providers[ProviderId.Codex]]);
        _dock.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Info("UsageDock exiting");
        _configService?.Save(_config);
        base.OnExit(e);
    }
}
