using System.Windows;
using System.Windows.Controls;
using DigitalCenter.ViewModels;

namespace DigitalCenter.App.Views;

public partial class ManagementPage : UserControl
{
    public ManagementPage()
    {
        InitializeComponent();
    }

    private ManagementViewModel? ViewModel => DataContext as ManagementViewModel;

    private void OnToggleMonitoringClick(object sender, RoutedEventArgs e)
    {
        ViewModel?.ToggleMonitoringCommand.Execute(null);
    }
}
