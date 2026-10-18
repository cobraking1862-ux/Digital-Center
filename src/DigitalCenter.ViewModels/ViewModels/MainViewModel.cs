using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
    private readonly IUiDispatcher _uiDispatcher;
    private readonly IAppLogger _logger;

    public MainViewModel(
        LibraryViewModel library,
        UsbCartViewModel usbCart,
        ManagementViewModel management,
        SettingsViewModel settings,
        WatchFolderService watchFolder,
        ImportService imports,
        ActivityFeed activity,
        IUiDispatcher uiDispatcher,
        IAppLogger logger)
    {
        Library = library;
        UsbCart = usbCart;
        Management = management;
        Settings = settings;
        Activity = activity;
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

        // Background import work (automatic metadata matching) reports into the
        // same feed as everything else.
        imports.ActivityReported += message => activity.Publish(message, ActivityKind.Background);

        // Refresh the Management dashboard whenever the Watch Folder finds something.
        watchFolder.PendingImportsChanged += () =>
        {
            activity.Publish("Fetching files from the Watch Folder — new file(s) detected.", ActivityKind.Background);
            _uiDispatcher.Post(() =>
            {
                try
                {
                    _ = Management.RefreshAsync();
                }
                catch (Exception ex)
                {
                    _logger.Warn($"Management refresh after watch-folder event failed: {ex.Message}");
                    activity.Publish($"Management refresh failed: {ex.Message}", ActivityKind.Error);
                }
            });
        };

        // A confirmed import changes the library → refresh the Home grid.
        management.ImportCompleted += () => _uiDispatcher.Post(() =>
        {
            activity.Publish("Library changed — refreshing Home.", ActivityKind.Background);
            _ = Library.RefreshSafeAsync();
        });

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

    /// <summary>
    /// The consolidated "Current Activity &amp; Status" feed shown in the sidebar.
    /// Every screen publishes its runtime messages here.
    /// </summary>
    public ActivityFeed Activity { get; }

    [ObservableProperty]
    public partial bool IsSidebarOpen { get; set; }

    [ObservableProperty]
    public partial NavItem SelectedNavItem { get; set; }

    [ObservableProperty]
    public partial ObservableObject CurrentPage { get; set; }

    /// <summary>True while the details screen is shown instead of a navigation destination.</summary>
    public bool IsShowingDetails => ReferenceEquals(CurrentPage, Details);

    public DetailsViewModel? Details { get; private set; }

    partial void OnSelectedNavItemChanged(NavItem value) => ApplyNav(value);

    /// <summary>Puts a destination on screen and loads it (one place only).</summary>
    private void ApplyNav(NavItem value)
    {
        Details = null;
        CurrentPage = value.Page;
        OnPropertyChanged(nameof(Details));
        OnPropertyChanged(nameof(IsShowingDetails));

        Activity.Publish($"Browsing {value.Title}.", ActivityKind.Navigation);

        switch (value.Page)
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

    /// <summary>
    /// Navigates to a destination from a click on the sidebar. Clicking the row
    /// that is ALREADY selected has to work as well: while the details screen is
    /// open the selection never changes, so the ListBox raises no
    /// SelectionChanged and the click used to do nothing at all — the user had
    /// to pick another destination and come back to see the library again.
    /// </summary>
    public void NavigateTo(NavItem item)
    {
        if (!ReferenceEquals(item, SelectedNavItem))
        {
            SelectedNavItem = item;   // raises OnSelectedNavItemChanged -> ApplyNav
            return;
        }

        if (!ReferenceEquals(CurrentPage, item.Page))
        {
            // Same destination, another screen on top (the details page).
            ApplyNav(item);
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
        Activity.Publish($"Opening title #{mediaTitleId} …", ActivityKind.Navigation);
        Details.CloseRequested += (_, _) => _uiDispatcher.Post(CloseDetails);
        Details.SavedChanges += () => _uiDispatcher.Post(() => _ = Library.RefreshSafeAsync());
        CurrentPage = Details;
        OnPropertyChanged(nameof(Details));
        OnPropertyChanged(nameof(IsShowingDetails));
        _ = Details.InitializeAsync();
    }

    /// <summary>
    /// Returns from the details screen to the selected destination and reloads
    /// it, so metadata edits, an added cart item or a completed import are
    /// visible immediately — no extra click on another destination needed.
    /// </summary>
    public void CloseDetails()
    {
        if (Details is null)
        {
            return;
        }

        Details = null;
        CurrentPage = SelectedNavItem.Page;
        OnPropertyChanged(nameof(Details));
        OnPropertyChanged(nameof(IsShowingDetails));
        Activity.Publish($"Back to {SelectedNavItem.Title}.", ActivityKind.Navigation);

        switch (SelectedNavItem.Page)
        {
            case LibraryViewModel:
                _ = Library.RefreshSafeAsync();
                break;
            case UsbCartViewModel:
                _ = UsbCart.RefreshCartAsync();
                break;
            case ManagementViewModel:
                _ = Management.RefreshAsync();
                break;
        }
    }

    /// <summary>Empties the consolidated activity feed (sidebar "Clear").</summary>
    [RelayCommand]
    private void ClearActivity() => Activity.Clear();

    /// <summary>Flips the navigation rail and reports the new state.</summary>
    public bool ToggleSidebar() => IsSidebarOpen = !IsSidebarOpen;

    /// <summary>
    /// Called once at startup for the initial screen loads. Watch Folder
    /// monitoring is deliberately NOT started here: <c>App.RunStartupMaintenanceAsync</c>
    /// is the single startup path for it, so the monitor can never be started
    /// twice from two places with two different ideas of the settings.
    /// </summary>
    public async Task InitializeAsync()
    {
        // The two loads are independent, so they run together; both are awaited
        // so the first screens are guaranteed to be filled (an empty Home grid
        // until the user touched a filter was exactly this being skipped).
        var library = Library.RefreshSafeAsync();
        var cart = UsbCart.RefreshCartAsync();
        await Task.WhenAll(library, cart);

        Activity.Publish(
            $"Home loaded: {Library.HomeMovies.Count} movie(s) and {Library.HomeSeries.Count} series ready to browse.",
            ActivityKind.Success);
    }
}
