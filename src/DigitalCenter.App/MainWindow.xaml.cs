using System.Windows;
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

    /// <summary>The single hamburger control opens/closes the sidebar.</summary>
    private void OnHamburgerClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null)
        {
            return;
        }

        ViewModel.ToggleSidebar();

        var targetWidth = ViewModel.IsSidebarOpen ? 248d : 64d;
        var animation = new DoubleAnimation(targetWidth, TimeSpan.FromMilliseconds(200))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        Sidebar.BeginAnimation(WidthProperty, animation);
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
