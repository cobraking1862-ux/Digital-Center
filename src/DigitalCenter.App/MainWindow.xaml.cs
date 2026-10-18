using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using DigitalCenter.ViewModels;

namespace DigitalCenter.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        StateChanged += (_, _) => UpdateCaptionTooltips();
    }

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    private const double SidebarExpandedWidth = 248d;
    private const double SidebarCollapsedWidth = 64d;

    /// <summary>
    /// The single hamburger control opens/closes the navigation rail. The state
    /// lives on the view model (<see cref="MainViewModel.IsSidebarOpen"/>) and the
    /// rail width follows it, so the button, the labels and the rail can never
    /// disagree — and the toggle still collapses the rail even if the view model
    /// is not attached yet.
    /// </summary>
    private void OnHamburgerClick(object sender, RoutedEventArgs e)
    {
        var open = ViewModel is { } vm
            ? vm.ToggleSidebar()
            : Sidebar.ActualWidth <= SidebarCollapsedWidth;

        AnimateSidebar(open);
        e.Handled = true;
    }

    private void AnimateSidebar(bool open)
    {
        var animation = new DoubleAnimation(
            open ? SidebarExpandedWidth : SidebarCollapsedWidth,
            TimeSpan.FromMilliseconds(200))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        Sidebar.BeginAnimation(WidthProperty, animation);
    }

    /// <summary>
    /// Clicking the row that is already selected must still navigate. While the
    /// details screen is open the ListBox selection does not change, so no
    /// SelectionChanged is raised and the click used to be ignored completely.
    /// </summary>
    private void OnNavRowClick(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel is null || sender is not ListBox { SelectedItem: NavItem item })
        {
            return;
        }

        ViewModel.NavigateTo(item);
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximizeRestoreClick(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void UpdateCaptionTooltips()
    {
        // Kept simple: the maximize/restore glyph swap is driven by XAML
        // DataTriggers; tooltips are static per button.
    }
}
