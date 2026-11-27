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
    }

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    private void OnHamburgerClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null)
        {
            return;
        }

        ViewModel.ToggleSidebar();

        var targetWidth = ViewModel.IsSidebarOpen ? 220d : 60d;
        var animation = new DoubleAnimation(targetWidth, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop
        };
        animation.Completed += (_, _) => Sidebar.Width = targetWidth;
        Sidebar.BeginAnimation(WidthProperty, animation);
        Sidebar.Width = targetWidth;
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnNavListPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel is null || sender is not ListBox navList)
        {
            return;
        }

        if (e.OriginalSource is DependencyObject source &&
            ItemsControl.ContainerFromElement(navList, source) is ListBoxItem { IsSelected: true } &&
            navList.SelectedItem is NavItem nav)
        {
            ViewModel.NotifyNavReactivated(nav);
        }
    }

    private void OnMaximizeRestoreClick(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
