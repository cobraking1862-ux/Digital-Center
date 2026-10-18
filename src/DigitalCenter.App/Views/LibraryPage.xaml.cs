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
        // "Nothing loaded yet" differs per tab: the Home dashboard keeps its
        // rows in HomeMovies/HomeSeries, the category tabs in Items.
        if (ViewModel is { IsLoading: false } vm
            && (vm.IsHomeTab ? !vm.HasHomeItems : vm.Items.Count == 0))
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

    /// <summary>Home dashboard shortcut: every movie in its own category.</summary>
    private void OnShowAllMoviesClick(object sender, RoutedEventArgs e)
    {
        ViewModel?.ShowAllMovies();
    }

    /// <summary>Home dashboard shortcut: every series in its own category.</summary>
    private void OnShowAllSeriesClick(object sender, RoutedEventArgs e)
    {
        ViewModel?.ShowAllSeries();
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
