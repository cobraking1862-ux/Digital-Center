using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DigitalCenter.Core.Logging;
using DigitalCenter.Core.Services;
using DigitalCenter.ViewModels.Services;

namespace DigitalCenter.ViewModels;

/// <summary>One entry in the navigation sidebar, with an optional badge (" (3)").</summary>
public sealed partial class NavItem : ObservableObject
{
    public NavItem(string title, string glyph, ObservableObject page)
    {
        Title = title;
        Glyph = glyph;
        Page = page;
        Badge = string.Empty;
    }

    public string Title { get; }

    public string Glyph { get; }

    public ObservableObject Page { get; }

    [ObservableProperty]
    public partial string Badge { get; set; }

    /// <summary>Badge count shown in the sidebar pill (0 hides the pill).</summary>
    [ObservableProperty]
    public partial int BadgeCount { get; set; }

    /// <summary>Text shown in the sidebar; combines title and badge.</summary>
    public string DisplayTitle => Badge.Length == 0 ? Title : $"{Title}{Badge}";

    partial void OnBadgeChanged(string value) => OnPropertyChanged(nameof(DisplayTitle));

    public override string ToString() => Title;
}

/// <summary>
/// Application shell: hamburger-controlled sidebar with the four primary
/// destinations (Home, USB Cart, Management, Settings), in-application
/// navigation to the details screen, and the wiring between the Watch Folder
/// and the Management dashboard.
/// </summary>
public partial class MainViewModel : ObservableObject
{
    private readonly WatchFolderService _watchFolder;
    private readonly IUiDispatcher _uiDispatcher;
    private readonly IAppLogger _logger;

    public MainViewModel(
        LibraryViewModel library,
        UsbCartViewModel usbCart,
        ManagementViewModel management,
        SettingsViewModel settings,
        WatchFolderService watchFolder,
        IUiDispatcher uiDispatcher,
        IAppLogger logger)
    {
        Library = library;
        UsbCart = usbCart;
        Management = management;
        Settings = settings;
        _watchFolder = watchFolder;
        _uiDispatcher = uiDispatcher;
        _logger = logger;

        NavItems =
        [
            new NavItem("Home", "\uE80F", library),
            new NavItem("USB Cart", "\uE7BF", usbCart),
            new NavItem("Management", "\uE8B7", management),
            new NavItem("Settings", "\uE713", settings)
        ];

        SelectedNavItem = NavItems[0];
        CurrentPage = library;
        IsSidebarOpen = true;

        // Sidebar badge: number of titles waiting in the USB cart.
        usbCart.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(UsbCartViewModel.CartCount))
            {
                NavItems[1].Badge = usbCart.CartCount > 0 ? $" ({usbCart.CartCount})" : string.Empty;
                NavItems[1].BadgeCount = usbCart.CartCount;
            }
        };

        // Refresh the Management dashboard whenever the Watch Folder finds something.
        watchFolder.PendingImportsChanged += () => _uiDispatcher.Post(() =>
        {
            try
            {
                _ = Management.RefreshAsync();
            }
            catch (Exception ex)
            {
                _logger.Warn($"Management refresh after watch-folder event failed: {ex.Message}");
            }
        });

        // A confirmed import changes the library → refresh the Home grid.
        management.ImportCompleted += () => _uiDispatcher.Post(() => _ = Library.RefreshSafeAsync());

        // Card clicks in the library open the details page inside the window.
        library.DetailsRequested += titleId => ShowDetails(titleId);

        // Adding to the cart updates the cart view + badge.
        library.CartChanged += () => _uiDispatcher.Post(() => _ = UsbCart.RefreshCartAsync());
    }

    public IReadOnlyList<NavItem> NavItems { get; }

    public LibraryViewModel Library { get; }

    public UsbCartViewModel UsbCart { get; }

    public ManagementViewModel Management { get; }

    public SettingsViewModel Settings { get; }

    [ObservableProperty]
    public partial bool IsSidebarOpen { get; set; }

    [ObservableProperty]
    public partial NavItem SelectedNavItem { get; set; }

    [ObservableProperty]
    public partial ObservableObject CurrentPage { get; set; }

    /// <summary>True while the details screen is shown instead of a navigation destination.</summary>
    public bool IsShowingDetails => ReferenceEquals(CurrentPage, Details);

    public DetailsViewModel? Details { get; private set; }

    partial void OnSelectedNavItemChanged(NavItem value)
    {
        Details = null;
        CurrentPage = value.Page;
        OnPropertyChanged(nameof(IsShowingDetails));

        switch (CurrentPage)
        {
            case LibraryViewModel:
                _ = Library.RefreshSafeAsync();
                break;
            case UsbCartViewModel:
                _ = UsbCart.OnNavigatedToAsync();
                break;
            case ManagementViewModel:
                _ = Management.OnNavigatedToAsync();
                break;
        }
    }

    /// <summary>Factory for detail view models; wired by the composition root (DI).</summary>
    public Func<int, DetailsViewModel>? DetailsFactory { get; set; }

    /// <summary>Opens the details screen for a title inside the main window.</summary>
    public void ShowDetails(int mediaTitleId)
    {
        if (DetailsFactory is null)
        {
            return;
        }

        Details = DetailsFactory(mediaTitleId);
        Details.CloseRequested += (_, _) => _uiDispatcher.Post(CloseDetails);
        Details.SavedChanges += () => _uiDispatcher.Post(() => _ = Library.RefreshSafeAsync());
        CurrentPage = Details;
        OnPropertyChanged(nameof(Details));
        OnPropertyChanged(nameof(IsShowingDetails));
        _ = Details.InitializeAsync();
    }

    public void CloseDetails()
    {
        Details = null;
        CurrentPage = SelectedNavItem.Page;
        OnPropertyChanged(nameof(Details));
        OnPropertyChanged(nameof(IsShowingDetails));
    }

    public void ToggleSidebar() => IsSidebarOpen = !IsSidebarOpen;

    /// <summary>Called once at startup: initial loads and Watch Folder auto-start.</summary>
    public async Task InitializeAsync(Core.Configuration.AppSettings settings)
    {
        _ = Library.RefreshSafeAsync();
        await UsbCart.RefreshCartAsync();

        var path = settings.WatchFolderPath;
        if (settings.WatchFolderEnabled && !string.IsNullOrWhiteSpace(path))
        {
            _watchFolder.StabilityDelay = TimeSpan.FromSeconds(Math.Clamp(settings.StabilityDelaySeconds, 2, 600));
            if (!_watchFolder.IsRunning)
            {
                _watchFolder.Start(path);
            }

            if (settings.ScanOnStartup)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _watchFolder.RescanOnceAsync(path);
                    }
                    catch (Exception ex)
                    {
                        _logger.Warn($"Startup watch-folder scan failed: {ex.Message}");
                    }
                });
            }
        }
    }
}
