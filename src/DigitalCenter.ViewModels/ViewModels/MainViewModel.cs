using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DigitalCenter.Core.Logging;
using DigitalCenter.Core.Services;
using DigitalCenter.ViewModels.Services;

namespace DigitalCenter.ViewModels;

/// <summary>One entry in the navigation sidebar, with an optional badge.</summary>
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

    [ObservableProperty]
    public partial int BadgeCount { get; set; }

    public string DisplayTitle => Badge.Length == 0 ? Title : $"{Title}{Badge}";

    partial void OnBadgeChanged(string value) => OnPropertyChanged(nameof(DisplayTitle));

    public override string ToString() => Title;
}

/// <summary>
/// Application shell: sidebar with Library, Drama, Lookup, Tickets, USB Cart,
/// Watch Folder, Maintenance, and Settings, plus in-application navigation to
/// Movie Details and Drama Details.
/// </summary>
public partial class MainViewModel : ObservableObject
{
    private readonly WatchFolderService _watchFolder;
    private readonly DramaWatchFolderService _dramaWatchFolder;
    private readonly IUiDispatcher _uiDispatcher;
    private readonly IAppLogger _logger;
    private readonly ActivityFeedService _activityFeed;
    private readonly NavItem _maintenanceNav;

    public MainViewModel(
        LibraryViewModel library,
        DramaViewModel drama,
        LookupViewModel lookup,
        TicketsViewModel tickets,
        UsbCartViewModel usbCart,
        ManagementViewModel management,
        MaintenanceViewModel maintenance,
        SettingsViewModel settings,
        WatchFolderService watchFolder,
        DramaWatchFolderService dramaWatchFolder,
        WatchFolderIssueService watchFolderIssues,
        ImportService imports,
        IUiDispatcher uiDispatcher,
        IAppLogger logger,
        ActivityFeedService activityFeed)
    {
        Library = library;
        Drama = drama;
        Lookup = lookup;
        Tickets = tickets;
        UsbCart = usbCart;
        Management = management;
        Maintenance = maintenance;
        Settings = settings;
        _watchFolder = watchFolder;
        _dramaWatchFolder = dramaWatchFolder;
        _uiDispatcher = uiDispatcher;
        _logger = logger;
        _activityFeed = activityFeed;

        watchFolder.StatusChanged += message => _activityFeed.Publish(message);
        dramaWatchFolder.StatusChanged += message => _activityFeed.Publish(message);
        imports.StatusChanged += message => _activityFeed.Publish(message);
        imports.ActivityChanged += () => _uiDispatcher.Post(() =>
        {
            _ = Management.RefreshAsync();
            _ = Maintenance.RefreshWatchFolderIssuesAsync();
        });
        watchFolderIssues.IssuesChanged += () => _uiDispatcher.Post(() =>
        {
            _ = Management.RefreshIssueCountAsync();
            _ = Maintenance.RefreshWatchFolderIssuesAsync();
        });

        var ticketsNav = new NavItem("Tickets", "\uE73A", tickets);
        var usbCartNav = new NavItem("USB Cart", "\uE7BF", usbCart);
        _maintenanceNav = new NavItem("Maintenance", "\uE90F", maintenance);

        NavItems =
        [
            new NavItem("Library", "\uE80F", library),
            new NavItem("Drama", "\uE714", drama),
            new NavItem("Lookup", "\uE721", lookup),
            ticketsNav,
            usbCartNav,
            new NavItem("Watch Folder", "\uE8B7", management),
            _maintenanceNav,
            new NavItem("Settings", "\uE713", settings)
        ];

        _isInitializing = true;
        SelectedNavItem = NavItems[0];
        CurrentPage = library;
        IsSidebarOpen = true;
        _isInitializing = false;

        tickets.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TicketsViewModel.OpenTicketCount))
            {
                ticketsNav.Badge = tickets.OpenTicketCount > 0 ? $" ({tickets.OpenTicketCount})" : string.Empty;
                ticketsNav.BadgeCount = tickets.OpenTicketCount;
            }
        };

        usbCart.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(UsbCartViewModel.CartCount))
            {
                usbCartNav.Badge = usbCart.CartCount > 0 ? $" ({usbCart.CartCount})" : string.Empty;
                usbCartNav.BadgeCount = usbCart.CartCount;
            }
        };

        watchFolder.PendingImportsChanged += () => _uiDispatcher.Post(() =>
        {
            try
            {
                _ = Management.RefreshAsync();
                _ = Library.RefreshSafeAsync();
                _ = Maintenance.RefreshWatchFolderIssuesAsync();
            }
            catch (Exception ex)
            {
                _logger.Warn($"Refresh after watch-folder event failed: {ex.Message}");
            }
        });

        dramaWatchFolder.DramaIngested += () => _uiDispatcher.Post(() =>
        {
            _ = Drama.RefreshSafeAsync();
            _ = Management.RefreshAsync();
            _ = Maintenance.RefreshWatchFolderIssuesAsync();
        });

        management.ImportCompleted += () => _uiDispatcher.Post(() => _ = Library.RefreshSafeAsync());
        management.DramaImportCompleted += () => _uiDispatcher.Post(() => _ = Drama.RefreshSafeAsync());
        management.OpenMaintenanceIssuesRequested += () => _uiDispatcher.Post(OpenMaintenanceWatchFolderIssues);

        maintenance.CatalogChanged += () => _uiDispatcher.Post(() =>
        {
            _ = Library.RefreshSafeAsync();
            _ = Drama.RefreshSafeAsync();
        });
        maintenance.LookupSitesChanged += () => _uiDispatcher.Post(Lookup.ReloadSites);
        maintenance.WatchFolderIssuesChanged += () => _uiDispatcher.Post(() => _ = Management.RefreshAsync());

        library.DetailsRequested += titleId => ShowDetails(titleId);
        library.CartChanged += () => _uiDispatcher.Post(() => _ = UsbCart.RefreshCartAsync());
        drama.DetailsRequested += dramaId => ShowDramaDetails(dramaId);
        lookup.OpenLocalTitleRequested += titleId => ShowDetails(titleId);
    }

    public IReadOnlyList<NavItem> NavItems { get; }

    public LibraryViewModel Library { get; }

    public DramaViewModel Drama { get; }

    public LookupViewModel Lookup { get; }

    public TicketsViewModel Tickets { get; }

    public UsbCartViewModel UsbCart { get; }

    public ManagementViewModel Management { get; }

    public MaintenanceViewModel Maintenance { get; }

    public SettingsViewModel Settings { get; }

    public ObservableCollection<ActivityFeedItem> ActivityFeed => _activityFeed.Items;

    [ObservableProperty]
    public partial bool IsSidebarOpen { get; set; }

    private bool _isInitializing;

    [ObservableProperty]
    public partial NavItem SelectedNavItem { get; set; }

    [ObservableProperty]
    public partial ObservableObject CurrentPage { get; set; }

    public bool IsShowingDetails => ReferenceEquals(CurrentPage, Details) || ReferenceEquals(CurrentPage, DramaDetails);

    public DetailsViewModel? Details { get; private set; }

    public DramaDetailsViewModel? DramaDetails { get; private set; }

    partial void OnSelectedNavItemChanged(NavItem value)
    {
        Details = null;
        DramaDetails = null;
        CurrentPage = value.Page;
        OnPropertyChanged(nameof(IsShowingDetails));

        if (_isInitializing)
        {
            return;
        }

        switch (CurrentPage)
        {
            case LibraryViewModel:
                _ = Library.RefreshSafeAsync();
                break;
            case DramaViewModel:
                _ = Drama.OnNavigatedToAsync();
                break;
            case LookupViewModel:
                Lookup.ReloadSites();
                break;
            case TicketsViewModel:
                _ = Tickets.OnNavigatedToAsync();
                break;
            case UsbCartViewModel:
                _ = UsbCart.OnNavigatedToAsync();
                break;
            case ManagementViewModel:
                _ = Management.OnNavigatedToAsync();
                break;
            case MaintenanceViewModel:
                Maintenance.OnNavigatedTo();
                break;
        }
    }

    private void OpenMaintenanceWatchFolderIssues()
    {
        Maintenance.SelectedTabIndex = 1;
        SelectedNavItem = _maintenanceNav;
    }

    public Func<int, DetailsViewModel>? DetailsFactory { get; set; }

    public Func<int, DramaDetailsViewModel>? DramaDetailsFactory { get; set; }

    public void ShowDetails(int mediaTitleId)
    {
        if (DetailsFactory is null) return;

        DramaDetails = null;
        Details = DetailsFactory(mediaTitleId);
        Details.CloseRequested += (_, _) => _uiDispatcher.Post(CloseDetails);
        Details.SavedChanges += () => _uiDispatcher.Post(() => _ = Library.RefreshSafeAsync());
        CurrentPage = Details;
        OnPropertyChanged(nameof(Details));
        OnPropertyChanged(nameof(IsShowingDetails));
        _ = Details.InitializeAsync();
    }

    public void ShowDramaDetails(int dramaId)
    {
        if (DramaDetailsFactory is null) return;

        Details = null;
        DramaDetails = DramaDetailsFactory(dramaId);
        DramaDetails.CloseRequested += (_, _) => _uiDispatcher.Post(CloseDetails);
        DramaDetails.SavedChanges += () => _uiDispatcher.Post(() => _ = Drama.RefreshSafeAsync());
        CurrentPage = DramaDetails;
        OnPropertyChanged(nameof(DramaDetails));
        OnPropertyChanged(nameof(IsShowingDetails));
        _ = DramaDetails.InitializeAsync();
    }

    public void CloseDetails()
    {
        Details = null;
        DramaDetails = null;
        CurrentPage = SelectedNavItem.Page;
        OnPropertyChanged(nameof(Details));
        OnPropertyChanged(nameof(DramaDetails));
        OnPropertyChanged(nameof(IsShowingDetails));

        if (CurrentPage is LibraryViewModel)
        {
            _ = Library.RefreshSafeAsync();
        }
        else if (CurrentPage is DramaViewModel)
        {
            _ = Drama.RefreshSafeAsync();
        }
    }

    public void NotifyNavReactivated(NavItem item)
    {
        if (item is null || !ReferenceEquals(item, SelectedNavItem))
        {
            return;
        }

        if (IsShowingDetails)
        {
            CloseDetails();
        }
        else if (item.Page is LibraryViewModel)
        {
            _ = Library.RefreshSafeAsync();
        }
        else if (item.Page is DramaViewModel)
        {
            _ = Drama.OnNavigatedToAsync();
        }
        else if (item.Page is TicketsViewModel)
        {
            _ = Tickets.OnNavigatedToAsync();
        }
    }

    public void ToggleSidebar() => IsSidebarOpen = !IsSidebarOpen;

    public async Task InitializeAsync(Core.Configuration.AppSettings settings)
    {
        _ = Library.RefreshSafeAsync();
        _ = Drama.RefreshSafeAsync();
        _ = Tickets.RefreshAsync();
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
                    try { await _watchFolder.RescanOnceAsync(path); }
                    catch (Exception ex) { _logger.Warn($"Startup watch-folder scan failed: {ex.Message}"); }
                });
            }
        }
    }
}
