using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DigitalCenter.Core.Configuration;
using DigitalCenter.Core.Services;
using DigitalCenter.Core.Tmdb;
using DigitalCenter.ViewModels.Services;

namespace DigitalCenter.ViewModels;

/// <summary>
/// Settings workspace: the only place where the default Watch Folder and the
/// application preferences are configured. Save Settings validates and applies
/// the changes; Discard Changes restores the last saved values. Changing
/// paths never moves files or rewrites library records.
/// </summary>
public partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsService _settingsService;
    private readonly ITmdbMetadataProvider _tmdb;
    private readonly IDialogService _dialogs;
    private readonly AppPaths _paths;

    public SettingsViewModel(
        SettingsService settingsService,
        ITmdbMetadataProvider tmdb,
        IDialogService dialogs,
        AppPaths paths,
        MaintenanceViewModel maintenance)
    {
        _settingsService = settingsService;
        _tmdb = tmdb;
        _dialogs = dialogs;
        _paths = paths;
        Maintenance = maintenance;

        ConflictOptions = ["Skip", "Overwrite", "Rename"];
        LoadFromService();
        TmdbConfigured = tmdb.IsConfigured;
    }

    /// <summary>Existing maintenance tooling (scan, backup/restore, reports), embedded as the Application section.</summary>
    public MaintenanceViewModel Maintenance { get; }

    public IReadOnlyList<string> ConflictOptions { get; }

    // ----------------------------------------------------------- library
    [ObservableProperty] public partial string MoviesRoot { get; set; }
    [ObservableProperty] public partial string SeriesRoot { get; set; }

    // ------------------------------------------------------ watch folder
    [ObservableProperty] public partial string WatchFolderPath { get; set; }
    [ObservableProperty] public partial bool WatchFolderEnabled { get; set; }
    [ObservableProperty] public partial bool ScanOnStartup { get; set; }
    [ObservableProperty] public partial int StabilityDelaySeconds { get; set; }
    [ObservableProperty] public partial bool WatchFolderIncludeSubfolders { get; set; }
    [ObservableProperty] public partial double MinimumMatchConfidence { get; set; }

    // ---------------------------------------------------------- metadata
    [ObservableProperty] public partial bool DownloadPosters { get; set; }
    [ObservableProperty] public partial bool DownloadBackdrops { get; set; }
    [ObservableProperty] public partial bool DownloadCastProfiles { get; set; }
    [ObservableProperty] public partial bool FetchTrailers { get; set; }
    [ObservableProperty] public partial string StatusText { get; set; }

    public bool TmdbConfigured { get; }

    // ----------------------------------------------------- file operations
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

    public string AppVersion => $"DigitalCenter 5.0.0";
    public string DatabaseLocation => _paths.DatabaseFile;
    public string SettingsLocation => _paths.SettingsFile;

    private void LoadFromService()
    {
        var settings = _settingsService.Load();
        MoviesRoot = settings.MoviesRoot ?? string.Empty;
        SeriesRoot = settings.SeriesRoot ?? string.Empty;
        WatchFolderPath = settings.WatchFolderPath ?? string.Empty;
        WatchFolderEnabled = settings.WatchFolderEnabled;
        ScanOnStartup = settings.ScanOnStartup;
        StabilityDelaySeconds = settings.StabilityDelaySeconds;
        WatchFolderIncludeSubfolders = settings.WatchFolderIncludeSubfolders;
        MinimumMatchConfidence = settings.MinimumMatchConfidence;
        DownloadPosters = settings.DownloadPosters;
        DownloadBackdrops = settings.DownloadBackdrops;
        DownloadCastProfiles = settings.DownloadCastProfiles;
        FetchTrailers = settings.FetchTrailers;
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
        return new AppSettings
        {
            MoviesRoot = NullIfEmpty(MoviesRoot),
            SeriesRoot = NullIfEmpty(SeriesRoot),
            WatchFolderPath = NullIfEmpty(WatchFolderPath),
            WatchFolderEnabled = WatchFolderEnabled,
            ScanOnStartup = ScanOnStartup,
            StabilityDelaySeconds = StabilityDelaySeconds,
            WatchFolderIncludeSubfolders = WatchFolderIncludeSubfolders,
            MinimumMatchConfidence = MinimumMatchConfidence,
            DownloadPosters = DownloadPosters,
            DownloadBackdrops = DownloadBackdrops,
            DownloadCastProfiles = DownloadCastProfiles,
            FetchTrailers = FetchTrailers,
            AskForDestination = AskForDestination,
            CleanFilenames = CleanFilenames,
            UsbConflictAction = ConflictIndex switch
            {
                1 => "Overwrite",
                2 => "Rename",
                _ => "Skip"
            },
            VerifyHashes = VerifyHashes,
            UsbCopySubfolder = string.IsNullOrWhiteSpace(UsbCopySubfolder) ? "DigitalCenter" : UsbCopySubfolder.Trim(),
            CreatePerTitleFolders = CreatePerTitleFolders,
            MovieFilenameTemplate = string.IsNullOrWhiteSpace(MovieFilenameTemplate)
                ? "{Title} ({Year})"
                : MovieFilenameTemplate,
            SeriesFilenameTemplate = string.IsNullOrWhiteSpace(SeriesFilenameTemplate)
                ? "{Title} - S{Season:00}E{Episode:00}"
                : SeriesFilenameTemplate
        };
    }

    [RelayCommand]
    private void BrowseMoviesRoot()
    {
        var picked = _dialogs.PickFolder(MoviesRoot, "Choose the movies library root");
        if (picked is not null)
        {
            MoviesRoot = picked;
        }
    }

    [RelayCommand]
    private void BrowseSeriesRoot()
    {
        var picked = _dialogs.PickFolder(SeriesRoot, "Choose the series library root");
        if (picked is not null)
        {
            SeriesRoot = picked;
        }
    }

    [RelayCommand]
    private void BrowseWatchFolder()
    {
        var picked = _dialogs.PickFolder(WatchFolderPath, "Choose the default Watch Folder");
        if (picked is not null)
        {
            WatchFolderPath = picked;
        }
    }

    [RelayCommand]
    private void OpenMoviesRoot()
    {
        if (Directory.Exists(MoviesRoot))
        {
            _dialogs.OpenInExplorer(MoviesRoot);
        }
    }

    [RelayCommand]
    private void OpenSeriesRoot()
    {
        if (Directory.Exists(SeriesRoot))
        {
            _dialogs.OpenInExplorer(SeriesRoot);
        }
    }

    [RelayCommand]
    private void OpenDataFolder() => _dialogs.OpenInExplorer(_paths.DataRoot);

    [RelayCommand]
    private async Task TestTmdbAsync()
    {
        if (!TmdbConfigured)
        {
            StatusText = "TMDb is not configured. Set the Tmdb:ReadAccessToken value in appsettings.json, " +
                         "as an environment variable (Tmdb__ReadAccessToken), or via .NET user secrets.";
            _dialogs.Error("TMDb", StatusText);
            return;
        }

        StatusText = "Testing the TMDb connection…";
        var reachable = await _tmdb.TestConnectionAsync();
        StatusText = reachable
            ? "TMDb connection OK."
            : "TMDb could not be reached (offline, or the token is invalid). The rest of the app keeps working.";
    }

    [RelayCommand]
    private void Save()
    {
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
            StatusText = "Settings saved. Changing paths never moves files or modifies library records; " +
                         "restart the application so the Watch Folder monitor picks up a new folder.";
        }
        catch (Exception ex)
        {
            StatusText = $"Settings could not be saved: {ex.Message}";
            _dialogs.Error("Settings", StatusText);
        }
    }

    [RelayCommand]
    private void DiscardChanges()
    {
        LoadFromService();
        StatusText = "Changes discarded — the last saved settings are shown.";
    }

    private static string? NullIfEmpty(string? text) =>
        string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
