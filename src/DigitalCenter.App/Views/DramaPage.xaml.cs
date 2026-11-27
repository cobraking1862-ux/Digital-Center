using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DigitalCenter.ViewModels;

namespace DigitalCenter.App.Views;

public partial class DramaPage : UserControl
{
    public DramaPage()
    {
        InitializeComponent();
    }

    private void OnDramaCardClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: DramaCardViewModel card } &&
            DataContext is DramaViewModel vm)
        {
            vm.OpenDetailsCommand.Execute(card);
        }
    }

    private void OnClearSearchClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is DramaViewModel vm)
        {
            vm.SearchText = string.Empty;
        }

        DramaSearchBox.Focus();
    }
}
