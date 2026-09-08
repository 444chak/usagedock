using System.Windows.Controls;

namespace UsageDock.Ui;

public partial class ProviderCell : UserControl
{
    public ProviderCell()
    {
        InitializeComponent();
    }

    public ProviderViewModel? Provider => DataContext as ProviderViewModel;
}
