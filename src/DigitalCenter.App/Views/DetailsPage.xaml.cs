using System.Windows;
using System.Windows.Controls;
using DigitalCenter.ViewModels;

namespace DigitalCenter.App.Views;

public partial class DetailsPage : UserControl
{
    public DetailsPage()
    {
        InitializeComponent();
    }

    private DetailsViewModel? ViewModel => DataContext as DetailsViewModel;

    private void OnBackClick(object sender, RoutedEventArgs e)
    {
        (Application.Current as App)?.CloseDetails();
    }
}
