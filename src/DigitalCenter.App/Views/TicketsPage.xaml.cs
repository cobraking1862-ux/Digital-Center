using System.Windows;
using System.Windows.Controls;
using DigitalCenter.ViewModels;

namespace DigitalCenter.App.Views;

public partial class TicketsPage : UserControl
{
    public TicketsPage()
    {
        InitializeComponent();
    }

    private void OnClearSearchClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is TicketsViewModel vm)
        {
            vm.SearchText = string.Empty;
        }

        TicketsSearchBox.Focus();
    }
}
