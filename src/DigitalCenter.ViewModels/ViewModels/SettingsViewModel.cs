using System.IO;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DigitalCenter.Core.Configuration;
using DigitalCenter.Core.Services;
using DigitalCenter.Core.Tmdb;
using DigitalCenter.ViewModels.Services;

namespace DigitalCenter.ViewModels;

/// <summary>
/// Settings workspace: library roots &amp; watch folder locations for both Movies and Dramas,
/// automatic language subfolder organization, TMDb metadata preferences, and USB options.
/// </summary>
public partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsService _settingsService;
    private readonly WatchFolderService _watchFolder;
    private readonly DramaWatchFolderService _dramaWatchFolder;
    private readonly ITmdbMetadataProvider _tmdb;
    private readonly IDialogService _dialogs;
    private readonly AppPaths _paths;
    private readonly ActivityFeedService _activityFeed;

    public SettingsViewModel(
        SettingsService settingsService,
        WatchFolderService watchFolder,
        DramaWatchFolderService dramaWatchFolder,
        ITmdbMetadataProvider tmdb,
        IDialogService dialogs,
        AppPaths paths,
        ActivityFeedService activityFeed)
    {
        _settingsService = settingsService;
        _watchFolder = watchFolder;
        _dramaWatchFolder = dramaWatchFolder;
        _tmdb = tmdb;
        _dialogs = dialogs;
        _paths = paths;
        _activityFeed = activityFeed;

        ConflictOptions = ["Skip", "Overwrite", "Rename"];
        StatusText = string.Empty;
        LoadFromService();
        TmdbConfigured = tmdb.IsConfigured;
    }

    public IReadOnlyList<string> ConflictOptions { get; }

    // ----------------------------------------------------------- movies & watch folder
    [ObservableProperty] public partial string MoviesRoot { get; set; }
    [ObservableProperty] public partial string CollectionsRoot { get; set; }
    [ObservableProperty] public partial string SeriesRoot { get; set; }
    [ObservableProperty] public partial string WatchFolderPath { get; set; }
    [ObservableProperty] public partial bool WatchFolderEnabled { get; set; }

    // ------------------------------------------------------------ dramas & watch folder
    [ObservableProperty] public partial string DramaRoot { get; set; }
    [ObservableProperty] public partial string DramaWatchFolderPath { get; set; }
    [ObservableProperty] public partial bool DramaWatchFolderEnabled { get; set; }
    [ObservableProperty] public partial string DramaCopySubfolder { get; set; }

    // ---------------------------------------------------------- metadata
    [ObservableProperty] public partial int MinimumMatchConfidencePercent { get; set; }
    [ObservableProperty] public partial bool DownloadPosters { get; set; }
    [ObservableProperty] public partial bool DownloadBackdrops { get; set; }
    [ObservableProperty] public partial bool DownloadCastProfiles { get; set; }
    [ObservableProperty] public partial bool FetchTrailers { get; set; }
    [ObservableProperty] public partial string StatusText { get; set; }

    public bool TmdbConfigured { get; }

    // ----------------------------------------------------- file operations
    [ObservableProperty] public partial bool AutoOrganizeByLanguage { get; set; }
    [ObservableProperty] public partial bool AskForDestination { get; set; }
    [ObservableProperty] public partial bool CleanFilenames { get; set; }
    [ObservableProperty] public partial int ConflictIndex { get; set; }
    [ObservableProperty] public partial bool VerifyHashes { get; set; }

    // ----------------------------------------------------------------- USB
    [ObservableProperty] public partial string UsbCopySubfolder { get; set; }
    [ObservableProperty] public partial bool CreatePerTitleFolders { get; set; }

    // ------------------------------------------------------------- naming
    [ObservableProperty] public partial string MovieFilenameTemplate { get; set; }
    [ObservableProperty] public partial string SeriesFilenameTemplate { get; set; }

    public string AppVersion
    {
        get
        {
            var version = Assembly.GetEntryAssembly()?.GetName().Version;
            return version is not null
                ? $"DigitalCenter {version.Major}.{version.Minor}.{version.Build}"
                : "DigitalCenter";
        }
    }

    public string DatabaseLocation => _paths.DatabaseFile;
    public string SettingsLocation => _paths.SettingsFile;

    private void LoadFromService()
    {
        var settings = _settingsService.Load();
        MoviesRoot = settings.MoviesRoot ?? string.Empty;
        CollectionsRoot = settings.CollectionsRoot ?? settings.GetEffectiveCollectionsRoot() ?? string.Empty;
        SeriesRoot = settings.SeriesRoot ?? string.Empty;
        WatchFolderPath = settings.WatchFolderPath ?? string.Empty;
        WatchFolderEnabled = settings.WatchFolderEnabled;
        DramaRoot = settings.DramaRoot ?? string.Empty;
        DramaWatchFolderPath = settings.DramaWatchFolderPath ?? string.Empty;
        DramaWatchFolderEnabled = settings.DramaWatchFolderEnabled;
        DramaCopySubfolder = string.IsNullOrWhiteSpace(settings.DramaCopySubfolder) ? "Dramas" : settings.DramaCopySubfolder;
        MinimumMatchConfidencePercent = (int)Math.Round(Math.Clamp(settings.MinimumMatchConfidence, 0.30, 0.95) * 100.0);
        DownloadPosters = settings.DownloadPosters;
        DownloadBackdrops = settings.DownloadBackdrops;
        DownloadCastProfiles = settings.DownloadCastProfiles;
        FetchTrailers = settings.FetchTrailers;
        AutoOrganizeByLanguage = settings.AutoOrganizeByLanguage;
        AskForDestination = settings.AskForDestination;
        CleanFilenames = settings.CleanFilenames;
        VerifyHashes = settings.VerifyHashes;
        UsbCopySubfolder = settings.UsbCopySubfolder;
        CreatePerTitleFolders = settings.CreatePerTitleFolders;
        MovieFilenameTemplate = settings.MovieFilenameTemplate;
        SeriesFilenameTemplate = settings.SeriesFilenameTemplate;
        ConflictIndex = settings.UsbConflictAction switch
        {
            "Overwrite" => 1,
            "Rename" => 2,
            _ => 0
        };
    }

    private AppSettings BuildSettings()
    {
        var settings = _settingsService.Load();
        settings.MoviesRoot = NullIfEmpty(MoviesRoot);
        settings.CollectionsRoot = NullIfEmpty(CollectionsRoot);
        settings.SeriesRoot = NullIfEmpty(SeriesRoot);
        settings.WatchFolderPath = NullIfEmpty(WatchFolderPath);
        settings.WatchFolderEnabled = WatchFolderEnabled && !string.IsNullOrWhiteSpace(settings.WatchFolderPath);
        settings.DramaRoot = NullIfEmpty(DramaRoot);
        settings.DramaWatchFolderPath = NullIfEmpty(DramaWatchFolderPath);
        settings.DramaWatchFolderEnabled = DramaWatchFolderEnabled && !string.IsNullOrWhiteSpace(settings.DramaWatchFolderPath);
        settings.DramaCopySubfolder = string.IsNullOrWhiteSpace(DramaCopySubfolder) ? "Dramas" : DramaCopySubfolder.Trim();
        settings.MinimumMatchConfidence = Math.Round(Math.Clamp(MinimumMatchConfidencePercent, 30, 95) / 100.0, 2);
        settings.DownloadPosters = DownloadPosters;
        settings.DownloadBackdrops = DownloadBackdrops;
        settings.DownloadCastProfiles = DownloadCastProfiles;
        settings.FetchTrailers = FetchTrailers;
        settings.AutoOrganizeByLanguage = AutoOrganizeByLanguage;
        settings.AskForDestination = AskForDestination;
        settings.CleanFilenames = CleanFilenames;
        settings.UsbConflictAction = ConflictIndex switch
        {
            1 => "Overwrite",
            2 => "Rename",
            _ => "Skip"
        };
        settings.VerifyHashes = VerifyHashes;
        settings.UsbCopySubfolder = string.IsNullOrWhiteSpace(UsbCopySubfolder) ? "DigitalCenter" : UsbCopySubfolder.Trim();
        settings.CreatePerTitleFolders = CreatePerTitleFolders;
        settings.MovieFilenameTemplate = string.IsNullOrWhiteSpace(MovieFilenameTemplate)
            ? "{Title} ({Year})"
            : MovieFilenameTemplate;
        settings.SeriesFilenameTemplate = string.IsNullOrWhiteSpace(SeriesFilenameTemplate)
            ? "{Title} - S{Season:00}E{Episode:00}"
            : SeriesFilenameTemplate;
        return settings;
    }

    [RelayCommand]
    private void BrowseMoviesRoot()
    {
        var picked = _dialogs.PickFolder(MoviesRoot, "Choose the movies library root");
        if (picked is not null) MoviesRoot = picked;
    }

    [RelayCommand]
    private void BrowseCollectionsRoot()
    {
        var picked = _dialogs.PickFolder(CollectionsRoot, "Choose the movie collections root folder");
        if (picked is not null) CollectionsRoot = picked;
    }

    [RelayCommand]
    private void BrowseWatchFolder()
    {
        var picked = _dialogs.PickFolder(WatchFolderPath, "Choose the Movies Watch Folder");
        if (picked is not null) WatchFolderPath = picked;
    }

    [RelayCommand]
    private void BrowseDramaRoot()
    {
        var picked = _dialogs.PickFolder(DramaRoot, "Choose the Drama library root folder");
        if (picked is not null) DramaRoot = picked;
    }

    [RelayCommand]
    private void BrowseDramaWatchFolder()
    {
        var picked = _dialogs.PickFolder(DramaWatchFolderPath, "Choose the Drama Watch Folder");
        if (picked is not null) DramaWatchFolderPath = picked;
    }

    [RelayCommand]
    private void BrowseSeriesRoot() => BrowseCollectionsRoot();

    [RelayCommand]
    private void OpenMoviesRoot()
    {
        if (string.IsNullOrWhiteSpace(MoviesRoot))
        {
            _dialogs.Info("Movies Library Root", "Choose a Movies library root folder first.");
            return;
        }
        Directory.CreateDirectory(MoviesRoot);
        _dialogs.OpenInExplorer(MoviesRoot);
    }

    [RelayCommand]
    private void OpenCollectionsRoot()
    {
        if (string.IsNullOrWhiteSpace(CollectionsRoot))
        {
            _dialogs.Info("Collections Library Root", "Choose a Movie Collections root folder first.");
            return;
        }
        Directory.CreateDirectory(CollectionsRoot);
        _dialogs.OpenInExplorer(CollectionsRoot);
    }

    [RelayCommand]
    private void OpenWatchFolder()
    {
        if (string.IsNullOrWhiteSpace(WatchFolderPath))
        {
            _dialogs.Info("Movies Watch Folder", "Choose a Movies Watch Folder first.");
            return;
        }
        Directory.CreateDirectory(WatchFolderPath);
        _dialogs.OpenInExplorer(WatchFolderPath);
    }

    [RelayCommand]
    private void OpenDramaRoot()
    {
        if (string.IsNullOrWhiteSpace(DramaRoot))
        {
            _dialogs.Info("Drama Root", "Choose a Drama library root folder first.");
            return;
        }
        Directory.CreateDirectory(DramaRoot);
        _dialogs.OpenInExplorer(DramaRoot);
    }

    [RelayCommand]
    private void OpenDramaWatchFolder()
    {
        if (string.IsNullOrWhiteSpace(DramaWatchFolderPath))
        {
            _dialogs.Info("Drama Watch Folder", "Choose a Drama Watch Folder first.");
            return;
        }
        Directory.CreateDirectory(DramaWatchFolderPath);
        _dialogs.OpenInExplorer(DramaWatchFolderPath);
    }

    [RelayCommand]
    private void OpenSeriesRoot() => OpenCollectionsRoot();

    [RelayCommand]
    private void OpenDataFolder() => _dialogs.OpenInExplorer(_paths.DataRoot);

    [RelayCommand]
    private async Task TestTmdbAsync()
    {
        if (!TmdbConfigured)
        {
            StatusText = "TMDb is not configured. Set Tmdb:ReadAccessToken in appsettings.json, environment variables, or user secrets.";
            _dialogs.Error("TMDb", StatusText);
            return;
        }

        StatusText = "Testing TMDb connection…";
        var reachable = await _tmdb.TestConnectionAsync();
        StatusText = reachable
            ? "TMDb connection OK."
            : "TMDb could not be reached. Offline features remain available.";
    }

    [RelayCommand]
    private void Save()
    {
        var previous = _settingsService.Load();
        var settings = BuildSettings();
        var issues = SettingsService.Validate(settings);
        if (issues.Count > 0)
        {
            StatusText = "Settings were not saved — fix the highlighted problems first.";
            _dialogs.Error("Invalid settings", string.Join(Environment.NewLine, issues));
            return;
        }

        try
        {
            _settingsService.Save(settings);
            ApplyWatchers(previous, settings);
            StatusText = "Settings saved.";
            _activityFeed.Publish("Settings saved");
        }
        catch (Exception ex)
        {
            StatusText = $"Settings could not be saved: {ex.Message}";
            _dialogs.Error("Settings", StatusText);
        }
    }

    private void ApplyWatchers(AppSettings previous, AppSettings current)
    {
        var stability = TimeSpan.FromSeconds(Math.Clamp(current.StabilityDelaySeconds, 2, 600));
        _watchFolder.StabilityDelay = stability;
        _dramaWatchFolder.StabilityDelay = stability;

        var moviePathChanged = !string.Equals(previous.WatchFolderPath, current.WatchFolderPath, StringComparison.OrdinalIgnoreCase);
        if (!current.WatchFolderEnabled || string.IsNullOrWhiteSpace(current.WatchFolderPath) || moviePathChanged)
        {
            if (_watchFolder.IsRunning) _watchFolder.Stop();
        }
        if (current.WatchFolderEnabled && !string.IsNullOrWhiteSpace(current.WatchFolderPath) && Directory.Exists(current.WatchFolderPath))
        {
            if (!_watchFolder.IsRunning) _watchFolder.Start(current.WatchFolderPath);
        }

        var dramaPathChanged = !string.Equals(previous.DramaWatchFolderPath, current.DramaWatchFolderPath, StringComparison.OrdinalIgnoreCase);
        if (!current.DramaWatchFolderEnabled || string.IsNullOrWhiteSpace(current.DramaWatchFolderPath) || dramaPathChanged)
        {
            if (_dramaWatchFolder.IsRunning) _dramaWatchFolder.Stop();
        }
        if (current.DramaWatchFolderEnabled &&
            !string.IsNullOrWhiteSpace(current.DramaWatchFolderPath) &&
            Directory.Exists(current.DramaWatchFolderPath) &&
            !string.IsNullOrWhiteSpace(current.DramaRoot))
        {
            if (!_dramaWatchFolder.IsRunning) _dramaWatchFolder.Start(current.DramaWatchFolderPath);
        }
    }

    private static string? NullIfEmpty(string? text) =>
        string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
