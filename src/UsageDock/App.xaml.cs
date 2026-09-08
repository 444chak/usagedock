using System.Windows;

namespace UsageDock;

public partial class App : Application
{
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
        new MainWindow().Show();
    }
}
