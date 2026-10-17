using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DigitalCenter.ViewModels;

namespace DigitalCenter.App.Views;

public partial class UsbCartPage : UserControl
{
    public UsbCartPage()
    {
        InitializeComponent();
    }

    private UsbCartViewModel? ViewModel => DataContext as UsbCartViewModel;

    /// <summary>Clicking a cart card selects it (for "Remove Selected").</summary>
    private void OnCardMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel is { } vm && (sender as FrameworkElement)?.DataContext is CartItemViewModel item)
        {
            vm.SelectedItem = item;
        }
    }
}
