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
    }

    private LibraryViewModel? ViewModel => DataContext as LibraryViewModel;

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
