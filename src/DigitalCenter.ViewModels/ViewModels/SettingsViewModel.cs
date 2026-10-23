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
/// Settings workspace: library roots, metadata/artwork, file-operation, USB and
/// naming preferences. Save Settings validates and applies the changes;
/// Discard Changes restores the last saved values. Changing paths never moves
/// files or rewrites library records.
///
/// The Watch Folder is deliberately NOT configured here: Management owns the
/// single default Watch Folder (path + monitoring), so this screen never
/// maintains a second path or a conflicting monitoring state. Saving works on
/// the loaded settings (see <see cref="SettingsService.Update"/>) and therefore
/// cannot clobber the values Management owns.
/// </summary>
public partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsService _settingsService;
    private readonly ITmdbMetadataProvider _tmdb;
    private readonly ActivityFeed _activity;
    private readonly IDialogService _dialogs;
    private readonly AppPaths _paths;

    public SettingsViewModel(
        SettingsService settingsService,
        ITmdbMetadataProvider tmdb,
        ActivityFeed activity,
        IDialogService dialogs,
        AppPaths paths,
        MaintenanceViewModel maintenance)
    {
        _settingsService = settingsService;
        _tmdb = tmdb;
        _activity = activity;
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

    // ---------------------------------------------------------- metadata
    // MinimumMatchConfidence is deliberately not editable here any more: the
    // threshold is a fixed matching safeguard (validated 0.30-0.95 by
    // SettingsService and applied by MetadataMatcher). It is read from and
    // written back to settings.json untouched, so a value configured by an
    // earlier version keeps working.

    [ObservableProperty] public partial bool DownloadPosters { get; set; }
    [ObservableProperty] public partial bool DownloadBackdrops { get; set; }
    [ObservableProperty] public partial bool DownloadCastProfiles { get; set; }
    [ObservableProperty] public partial bool FetchTrailers { get; set; }
    [ObservableProperty] public partial string StatusText { get; set; }

    public bool TmdbConfigured { get; }

    // ----------------------------------------------------- file operations
    /// <summary>Move matched Watch Folder files into the library without asking.</summary>
    [ObservableProperty] public partial bool AutoImportMatchedFiles { get; set; }

    /// <summary>File imports into "&lt;root&gt;\&lt;detected language&gt;", creating the folder when missing.</summary>
    [ObservableProperty] public partial bool OrganizeLibraryByLanguage { get; set; }

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
        DownloadPosters = settings.DownloadPosters;
        DownloadBackdrops = settings.DownloadBackdrops;
        DownloadCastProfiles = settings.DownloadCastProfiles;
        FetchTrailers = settings.FetchTrailers;
        AutoImportMatchedFiles = settings.AutoImportMatchedFiles;
        OrganizeLibraryByLanguage = settings.OrganizeLibraryByLanguage;
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

    /// <summary>
    /// Applies the values this screen owns onto the loaded settings. Everything
    /// owned elsewhere — above all the Watch Folder path and its monitoring
    /// switches, which Management owns — is left exactly as it is.
    /// </summary>
    private void ApplyTo(AppSettings settings)
    {
        settings.MoviesRoot = NullIfEmpty(MoviesRoot);
        settings.SeriesRoot = NullIfEmpty(SeriesRoot);
        settings.DownloadPosters = DownloadPosters;
        settings.DownloadBackdrops = DownloadBackdrops;
        settings.DownloadCastProfiles = DownloadCastProfiles;
        settings.FetchTrailers = FetchTrailers;
        settings.AutoImportMatchedFiles = AutoImportMatchedFiles;
        settings.OrganizeLibraryByLanguage = OrganizeLibraryByLanguage;
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
    }

    /// <summary>The settings as they would be saved (used for validation before writing).</summary>
    private AppSettings BuildSettings()
    {
        var settings = _settingsService.Load();
        ApplyTo(settings);
        return settings;
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
        _activity.Publish("Testing the TMDb connection…", ActivityKind.Background);
        var reachable = await _tmdb.TestConnectionAsync();
        StatusText = reachable
            ? "TMDb connection OK."
            : "TMDb could not be reached (offline, or the token is invalid). The rest of the app keeps working.";
        _activity.Publish(StatusText, reachable ? ActivityKind.Success : ActivityKind.Warning);
    }

    [RelayCommand]
    private void Save()
    {
        var settings = BuildSettings();
        var issues = SettingsService.Validate(settings);
        if (issues.Count > 0)
        {
            StatusText = "Settings were not saved — fix the highlighted problems first.";
            _activity.Publish($"Settings not saved: {string.Join(" ", issues)}", ActivityKind.Warning);
            _dialogs.Error("Invalid settings", string.Join(Environment.NewLine, issues));
            return;
        }

        try
        {
            // Update (load → apply → save) keeps the Watch Folder configuration
            // that Management owns intact.
            _settingsService.Update(ApplyTo);
            StatusText = "Settings saved. Changing paths never moves files or modifies library records. " +
                         "The Watch Folder is managed in the Management workspace.";
            _activity.Publish("Settings saved successfully.", ActivityKind.Success);
        }
        catch (Exception ex)
        {
            StatusText = $"Settings could not be saved: {ex.Message}";
            _activity.Publish(StatusText, ActivityKind.Error);
            _dialogs.Error("Settings", StatusText);
        }
    }

    [RelayCommand]
    private void DiscardChanges()
    {
        LoadFromService();
        StatusText = "Changes discarded — the last saved settings are shown.";
        _activity.Publish(StatusText, ActivityKind.System);
    }

    private static string? NullIfEmpty(string? text) =>
        string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
