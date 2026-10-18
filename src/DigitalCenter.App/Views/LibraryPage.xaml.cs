using System.Windows;
using System.Windows.Controls;
using DigitalCenter.ViewModels;
using DigitalCenter.ViewModels.Services;

namespace DigitalCenter.App.Views;

public partial class LibraryPage : UserControl
{
    public LibraryPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private LibraryViewModel? ViewModel => DataContext as LibraryViewModel;

    /// <summary>
    /// Safety net for "the grid is empty until I touch a filter": a freshly
    /// created page (first start, returning from the details screen, navigating
    /// back to Home) asks for a load when the collection is still empty. A page
    /// that already has items is left alone, so this never causes a reload.
    /// </summary>
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { Items.Count: 0 } vm && !vm.IsLoading)
        {
            _ = vm.RefreshSafeAsync();
        }
    }

    /// <summary>Collections tab shortcut to the genre browser.</summary>
    private void OnBrowseGenresClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm)
        {
            vm.SelectedTab = LibraryTab.Genres;
        }
    }

    /// <summary>Clears the search box (the ✕ affordance inside the field).</summary>
    private void OnClearSearchClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm)
        {
            vm.SearchText = string.Empty;
        }
    }
}
