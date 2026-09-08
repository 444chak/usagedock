using System.Windows;

namespace UsageDock;

/// <summary>
/// First cut of the dock: a small borderless strip parked against the right edge of the primary
/// screen. Native styling, DPI handling and hit testing arrive in later steps.
/// </summary>
public partial class MainWindow : Window
{
    private const double EdgeMargin = 8d;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => Reposition();
    }

    private void Reposition()
    {
        var work = SystemParameters.WorkArea;
        Left = work.Right - Width - EdgeMargin;
        Top = work.Top + ((work.Height - Height) / 2d);
    }
}
