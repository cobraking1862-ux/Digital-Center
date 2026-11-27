using System.Windows;
using System.Windows.Controls;
using DigitalCenter.ViewModels;

namespace DigitalCenter.App.Views;

public partial class LookupPage : UserControl
{
    public LookupPage()
    {
        InitializeComponent();
    }

    private void OnClearSearchClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is LookupViewModel vm)
        {
            vm.SearchQuery = string.Empty;
        }

        LookupSearchBox.Focus();
    }
}
