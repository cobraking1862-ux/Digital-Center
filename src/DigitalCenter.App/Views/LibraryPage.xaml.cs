using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using DigitalCenter.ViewModels;

namespace DigitalCenter.App.Views;

public partial class LibraryPage : UserControl
{
    public LibraryPage()
    {
        InitializeComponent();
        Loaded += OnLibraryPageLoaded;
    }

    private async void OnLibraryPageLoaded(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm)
        {
            if (vm.Items.Count == 0 && !vm.IsLoading)
            {
                await vm.RefreshSafeAsync();
            }

            // Ensure layout re-measures once the visual tree and ScrollViewer viewport are realized.
            _ = Dispatcher.InvokeAsync(InvalidateMeasure, DispatcherPriority.Loaded);
        }
    }

    private LibraryViewModel? ViewModel => DataContext as LibraryViewModel;

    private void OnCardClick(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel is not { } vm || (sender as FrameworkElement)?.DataContext is not LibraryItemViewModel item)
        {
            return;
        }

        var toggle = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        foreach (var other in vm.Items)
        {
            other.IsSelected = toggle
                ? (ReferenceEquals(other, item) ? !other.IsSelected : other.IsSelected)
                : ReferenceEquals(other, item);
        }

        e.Handled = true;
    }

    private void OnClearSearchClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm)
        {
            vm.SearchText = string.Empty;
        }

        LibrarySearchBox.Focus();
    }
}