using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DigitalCenter.Core.Configuration;
using DigitalCenter.Core.Logging;
using DigitalCenter.Core.Models;
using DigitalCenter.Core.Services;
using DigitalCenter.ViewModels.Services;

namespace DigitalCenter.ViewModels;

/// <summary>One row of the Management recent-activity list.</summary>
public sealed class ActivityItemViewModel
{
    public ActivityItemViewModel(MetadataImportLog log)
    {
        Id = log.Id;
        FileName = log.SourceFile;
        SourcePath = log.SourcePath ?? string.Empty;
        SizeText = log.FileSize is { } size ? UiFormat.FormatBytes(size) : "—";
        Status = log.Status;
        StatusText = ImportStatusLabels.For(log.Status);
        StatusKey = log.Status switch
        {
            ImportStatuses.Imported => "Ok",
            ImportStatuses.Failed => "Bad",
            ImportStatuses.Matched => "Active",
            ImportStatuses.Pending => "Pending",
            ImportStatuses.Approved => "Pending",
            _ => "Neutral"
        };
        DetectedType = log.IsSeries ? "Series" : "Movie";
        MatchedTitle = string.IsNullOrWhiteSpace(log.ParsedTitle) ? "—" : log.ParsedTitle!;
        MatchSummary = log.TmdbId is not null
            ? $"{log.ParsedTitle}{(log.ParsedYear is { } y ? $" ({y})" : "")} · TMDb {log.TmdbId}" +
              (log.Confidence is { } c ? $" · {c:P0}" : "")
            : log.Status;
        Detail = log.ErrorDetails ?? log.Note ?? string.Empty;
        HasDetail = Detail.Length > 0;
        MediaTitleId = log.MediaTitleId;
        TimestampText = log.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        CanConfirm = log.Status is ImportStatuses.Matched or ImportStatuses.Approved;
        CanSkip = log.Status is ImportStatuses.Matched or ImportStatuses.Pending or ImportStatuses.NoMatch;
        IsAwaitingDestination = log.Status == ImportStatuses.Matched;
    }

    public int Id { get; }
    public string FileName { get; }
    public string SourcePath { get; }
    public string SizeText { get; }
    public string Status { get; }
    public string StatusText { get; }
    public string StatusKey { get; }
    public string DetectedType { get; }
    public string MatchedTitle { get; }
    public string MatchSummary { get; }
    public string Detail { get; }
    public bool HasDetail { get; }
    public int? MediaTitleId { get; }
    public string TimestampText { get; }
    public bool CanConfirm { get; }
    public bool CanSkip { get; }
    public bool IsAwaitingDestination { get; }

    /// <summary>The backing log row (refreshed by the view model when the selection changes).</summary>
    public MetadataImportLog Log { get; internal set; } = null!;
}

/// <summary>
/// Management workspace: the ONE place where the single default Watch Folder is
/// configured and monitored — its path, the monitoring switches, start/stop,
/// rescan, import statistics, recent activity with the automatic match results,
/// a file preview and the destination-selection / confirmation workflow.
/// Settings no longer holds a Watch Folder path or any monitoring state, so
/// there is exactly one authoritative saved value
/// (<see cref="AppSettings.WatchFolderPath"/>) and one monitoring state.
/// There is deliberately no metadata editing here — that belongs to the details
/// screen. Changing the path never moves media and never erases records.
/// </summary>
public partial class ManagementViewModel : ObservableObject
{
    private readonly WatchFolderService _watchFolder;
    private readonly ImportService _imports;
    private readonly SettingsService _settingsService;
    private readonly IDialogService _dialogs;
    private readonly IAppLogger _logger;

    /// <summary>Raised after an import completed so the shell can refresh the library.</summary>
    public event Action? ImportCompleted;

    public ManagementViewModel(
        WatchFolderService watchFolder,
        ImportService imports,
        SettingsService settingsService,
        IDialogService dialogs,
        IAppLogger logger)
    {
        _watchFolder = watchFolder;
        _imports = imports;
        _settingsService = settingsService;
        _dialogs = dialogs;
        _logger = logger;

        WatchPathDisplay = string.Empty;
        MonitorStatusText = string.Empty;
        StatusText = string.Empty;
        DestinationPreviewText = string.Empty;
        ImportSummary = string.Empty;
        WatchFolderPath = string.Empty;
        StabilityDelaySeconds = 8;
        LoadWatchFolderSettings();
    }

    public ObservableCollection<ActivityItemViewModel> Activity { get; } = [];

    [ObservableProperty] public partial string WatchPathDisplay { get; set; }
    [ObservableProperty] public partial string MonitorStatusText { get; set; }
    [ObservableProperty] public partial string StatusText { get; set; }

    // ------------------------------------------------ Watch Folder (the only
    // place these values are edited; they are persisted through SettingsService)
    [ObservableProperty] public partial string WatchFolderPath { get; set; }
    [ObservableProperty] public partial bool WatchFolderEnabled { get; set; }
    [ObservableProperty] public partial bool ScanOnStartup { get; set; }
    [ObservableProperty] public partial bool IncludeSubfolders { get; set; }
    [ObservableProperty] public partial int StabilityDelaySeconds { get; set; }
    [ObservableProperty] public partial bool HasWatchFolderChanges { get; set; }

    /// <summary>The saved Watch Folder path, so unsaved edits can be detected.</summary>
    private string _savedWatchFolderPath = string.Empty;

    partial void OnWatchFolderPathChanged(string value) => UpdateWatchFolderDirtyState();
    partial void OnWatchFolderEnabledChanged(bool value) => UpdateWatchFolderDirtyState();
    partial void OnScanOnStartupChanged(bool value) => UpdateWatchFolderDirtyState();
    partial void OnIncludeSubfoldersChanged(bool value) => UpdateWatchFolderDirtyState();
    partial void OnStabilityDelaySecondsChanged(int value) => UpdateWatchFolderDirtyState();

    private void UpdateWatchFolderDirtyState() =>
        HasWatchFolderChanges =
            !string.Equals(WatchFolderPath.Trim(), _savedWatchFolderPath, StringComparison.OrdinalIgnoreCase);
    [ObservableProperty] public partial string DestinationPreviewText { get; set; }
    [ObservableProperty] public partial string ImportSummary { get; set; }
    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial ActivityItemViewModel? SelectedItem { get; set; }

    // Statistics
    [ObservableProperty] public partial int TotalCount { get; set; }
    [ObservableProperty] public partial int ImportedCount { get; set; }
    [ObservableProperty] public partial int SkippedCount { get; set; }
    [ObservableProperty] public partial int FailedCount { get; set; }
    [ObservableProperty] public partial int NoMatchCount { get; set; }
    [ObservableProperty] public partial int UnsupportedCount { get; set; }
    [ObservableProperty] public partial int AwaitingCount { get; set; }

    // Selected-file preview
    [ObservableProperty] public partial bool HasPreview { get; set; }
    [ObservableProperty] public partial string PreviewFileName { get; set; } = string.Empty;
    [ObservableProperty] public partial string PreviewSourcePath { get; set; } = string.Empty;
    [ObservableProperty] public partial string PreviewSizeText { get; set; } = string.Empty;
    [ObservableProperty] public partial string PreviewTypeText { get; set; } = string.Empty;
    [ObservableProperty] public partial string PreviewMatchText { get; set; } = string.Empty;
    [ObservableProperty] public partial string PreviewDetailText { get; set; } = string.Empty;
    [ObservableProperty] public partial string PreviewTargetText { get; set; } = string.Empty;
    [ObservableProperty] public partial bool PreviewHasTarget { get; set; }
    [ObservableProperty] public partial bool PreviewIsMedia { get; set; }
    [ObservableProperty] public partial bool CanConfirmSelected { get; set; }
    [ObservableProperty] public partial bool CanSkipSelected { get; set; }
    [ObservableProperty] public partial bool CanChooseDestination { get; set; }

    public bool IsMonitoring => _watchFolder.IsRunning;

    partial void OnSelectedItemChanged(ActivityItemViewModel? value) => _ = RefreshPreviewAsync();

    /// <summary>Navigation hook (also used after watch-folder events).</summary>
    public async Task OnNavigatedToAsync()
    {
        LoadWatchFolderSettings();
        await RefreshAsync();
    }

    /// <summary>Reads the single authoritative Watch Folder configuration into the editor fields.</summary>
    private void LoadWatchFolderSettings()
    {
        var settings = _settingsService.Load();
        _savedWatchFolderPath = (settings.WatchFolderPath ?? string.Empty).Trim();
        WatchFolderPath = _savedWatchFolderPath;
        WatchFolderEnabled = settings.WatchFolderEnabled;
        ScanOnStartup = settings.ScanOnStartup;
        IncludeSubfolders = settings.WatchFolderIncludeSubfolders;
        StabilityDelaySeconds = Math.Clamp(settings.StabilityDelaySeconds, 2, 600);
        HasWatchFolderChanges = false;
    }

    public async Task RefreshAsync()
    {
        var settings = _settingsService.Load();
        var configured = !string.IsNullOrWhiteSpace(settings.WatchFolderPath);
        WatchPathDisplay = configured
            ? settings.WatchFolderPath!
            : "No Watch Folder yet — choose the folder below and save it.";
        OnPropertyChanged(nameof(IsMonitoring));
        MonitorStatusText = IsMonitoring
            ? $"Monitoring active{(_watchFolder.CurrentPath is { } p ? $" · {p}" : string.Empty)}"
            : !configured
                ? "No Watch Folder configured"
                : settings.WatchFolderEnabled
                    ? "Monitoring stopped"
                    : "Automatic monitoring is off";

        try
        {
            var stats = await _imports.GetStatsAsync();
            TotalCount = stats.Total;
            ImportedCount = stats.Imported;
            SkippedCount = stats.Skipped;
            FailedCount = stats.Failed;
            NoMatchCount = stats.NoMatch;
            UnsupportedCount = stats.Unsupported;
            AwaitingCount = stats.AwaitingDestination;
            ImportSummary = stats.Total == 0
                ? string.Empty
                : $"{stats.Imported} imported · {stats.NoMatch} no match · {stats.Failed} failed";

            var logs = await _imports.GetActivityAsync();

            // Update or insert rows in place so the list does not flicker.
            var byId = Activity.ToDictionary(a => a.Id);
            foreach (var log in logs)
            {
                if (byId.TryGetValue(log.Id, out var existing))
                {
                    var index = Activity.IndexOf(existing);
                    var updated = new ActivityItemViewModel(log) { Log = log };
                    Activity[index] = updated;
                    byId[log.Id] = updated;
                    if (ReferenceEquals(SelectedItem, existing))
                    {
                        SelectedItem = updated;
                    }
                }
                else
                {
                    Activity.Insert(0, new ActivityItemViewModel(log) { Log = log });
                }
            }

            while (Activity.Count > logs.Count)
            {
                Activity.RemoveAt(Activity.Count - 1);
            }
        }
        catch (Exception ex)
        {
            _logger.Error("Refreshing the Management dashboard failed.", ex);
            StatusText = $"Refresh failed: {ex.Message}";
        }
    }

    private async Task RefreshPreviewAsync()
    {
        DestinationPreviewText = string.Empty;
        if (SelectedItem is null)
        {
            HasPreview = false;
            return;
        }

        try
        {
            var log = await _imports.GetLogAsync(SelectedItem.Id);
            if (log is null)
            {
                HasPreview = false;
                return;
            }

            var isMedia = LibraryScanner.MediaExtensions.Contains(Path.GetExtension(log.SourceFile));
            PreviewIsMedia = isMedia;
            PreviewFileName = log.SourceFile;
            PreviewSourcePath = log.SourcePath ?? "—";
            PreviewSizeText = log.FileSize is { } size ? UiFormat.FormatBytes(size) : "unknown";
            PreviewTypeText = isMedia ? (log.IsSeries ? "Series" : "Movie") : "Not a video file";
            PreviewMatchText = log.TmdbId is not null
                ? $"{log.ParsedTitle}{(log.ParsedYear is { } y ? $" ({y})" : "")} — TMDb {log.TmdbId}, confidence {log.Confidence:P0}"
                : ImportStatusLabels.For(log.Status);
            PreviewDetailText = log.ErrorDetails ?? log.Note ?? string.Empty;

            var settings = _settingsService.Load();
            var defaultFolder = _imports.GetDefaultDestinationFolder(log.IsSeries, settings);
            if (log.Status == ImportStatuses.Matched)
            {
                var preview = await _imports.PreviewImportAsync(log, destinationFolder: null);
                DestinationPreviewText = preview.DestinationFolder;
                PreviewTargetText = preview.PlannedTargetPath;
                PreviewHasTarget = true;
                CanChooseDestination = true;
            }
            else
            {
                PreviewTargetText = log.TargetPath ?? string.Empty;
                PreviewHasTarget = !string.IsNullOrEmpty(PreviewTargetText);
                DestinationPreviewText = log.ChosenFolder ?? defaultFolder ?? string.Empty;
                CanChooseDestination = log.Status == ImportStatuses.Matched;
            }

            CanConfirmSelected = log.Status is ImportStatuses.Matched or ImportStatuses.Approved;
            CanSkipSelected = log.Status is ImportStatuses.Matched or ImportStatuses.Pending or ImportStatuses.NoMatch;
            HasPreview = true;
        }
        catch (Exception ex)
        {
            _logger.Warn($"Preview refresh failed: {ex.Message}");
            HasPreview = true; // Show what we have; the detail explains the rest.
            PreviewDetailText = $"Preview incomplete: {ex.Message}";
        }
    }

    /// <summary>Browses for the default Watch Folder (the path is not saved until "Save Watch Folder").</summary>
    [RelayCommand]
    private void BrowseWatchFolder()
    {
        var picked = _dialogs.PickFolder(
            string.IsNullOrWhiteSpace(WatchFolderPath) ? null : WatchFolderPath,
            "Choose the default Watch Folder");
        if (picked is not null)
        {
            WatchFolderPath = picked;
            StatusText = "Save the Watch Folder to apply the new path. No file is moved by changing it.";
        }
    }

    /// <summary>
    /// Persists the Watch Folder path and the monitoring switches. This is the
    /// only writer of these values: it goes through
    /// <see cref="SettingsService.Update"/> so the settings owned by the
    /// Settings screen (library roots, artwork, copy options) are preserved.
    /// Changing the path never moves media and never erases catalog or import
    /// records — the folder is only watched from its new location.
    /// </summary>
    [RelayCommand]
    private void SaveWatchFolder()
    {
        var path = WatchFolderPath?.Trim() ?? string.Empty;
        var stability = Math.Clamp(StabilityDelaySeconds, 2, 600);

        var candidate = _settingsService.Load();
        candidate.WatchFolderPath = path.Length == 0 ? null : path;
        candidate.StabilityDelaySeconds = stability;

        var issues = SettingsService.ValidateWatchFolder(candidate);
        if (issues.Count > 0)
        {
            StatusText = "The Watch Folder was not saved — fix the problems first.";
            _dialogs.Error("Watch Folder", string.Join(Environment.NewLine, issues));
            return;
        }

        try
        {
            var settings = _settingsService.Update(current =>
            {
                current.WatchFolderPath = candidate.WatchFolderPath;
                current.WatchFolderEnabled = WatchFolderEnabled;
                current.ScanOnStartup = ScanOnStartup;
                current.WatchFolderIncludeSubfolders = IncludeSubfolders;
                current.StabilityDelaySeconds = stability;
            });

            _savedWatchFolderPath = (settings.WatchFolderPath ?? string.Empty).Trim();
            HasWatchFolderChanges = false;

            // Apply immediately: the single monitor follows the saved path.
            _watchFolder.StabilityDelay = TimeSpan.FromSeconds(stability);
            if (string.IsNullOrWhiteSpace(settings.WatchFolderPath))
            {
                _watchFolder.Stop();
            }
            else
            {
                _watchFolder.ChangePath(settings.WatchFolderPath);
            }

            StatusText = $"Watch Folder saved: {settings.WatchFolderPath ?? "(none)"}. " +
                         "Existing files, import history and catalog records were left untouched.";
            _logger.Info($"Watch Folder path saved: '{settings.WatchFolderPath ?? "(none)"}'.");
            _ = RefreshAsync();
        }
        catch (Exception ex)
        {
            _logger.Error("Saving the Watch Folder failed.", ex);
            StatusText = $"The Watch Folder could not be saved: {ex.Message}";
            _dialogs.Error("Watch Folder", StatusText);
        }
    }

    /// <summary>Restores the saved Watch Folder values, discarding unsaved edits.</summary>
    [RelayCommand]
    private void DiscardWatchFolderChanges()
    {
        LoadWatchFolderSettings();
        StatusText = "Changes discarded — the saved Watch Folder configuration is shown.";
    }

    [RelayCommand]
    private void ToggleMonitoring()
    {
        if (HasWatchFolderChanges)
        {
            StatusText = "Save the Watch Folder first — monitoring always uses the saved path.";
            return;
        }

        var settings = _settingsService.Load();
        if (IsMonitoring)
        {
            _watchFolder.Stop();
            StatusText = "Monitoring stopped.";
        }
        else
        {
            if (string.IsNullOrWhiteSpace(settings.WatchFolderPath))
            {
                _dialogs.Error("Watch Folder", "Choose and save a Watch Folder path first.");
                return;
            }

            _watchFolder.StabilityDelay = TimeSpan.FromSeconds(Math.Clamp(settings.StabilityDelaySeconds, 2, 600));
            _watchFolder.Start(settings.WatchFolderPath);
            StatusText = $"Monitoring started for '{settings.WatchFolderPath}'.";
        }

        OnPropertyChanged(nameof(IsMonitoring));
        MonitorStatusText = IsMonitoring ? "Monitoring active" : "Monitoring stopped";
    }

    [RelayCommand]
    private async Task RescanNowAsync()
    {
        var settings = _settingsService.Load();
        if (string.IsNullOrWhiteSpace(settings.WatchFolderPath))
        {
            _dialogs.Error("Watch Folder", "Choose and save a Watch Folder path first.");
            return;
        }

        IsBusy = true;
        try
        {
            _watchFolder.StabilityDelay = TimeSpan.FromSeconds(Math.Clamp(settings.StabilityDelaySeconds, 2, 600));
            var scan = await _watchFolder.RescanConfiguredAsync();
            StatusText = $"Rescan finished: {scan.NewPending} new file(s), {scan.AlreadyKnown} known, " +
                         $"{scan.UnsupportedLogged} unsupported, {scan.Unstable} still being written.";
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            _logger.Error("Rescan failed.", ex);
            StatusText = $"Rescan failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void OpenWatchFolder()
    {
        var settings = _settingsService.Load();
        if (string.IsNullOrWhiteSpace(settings.WatchFolderPath))
        {
            _dialogs.Info("Watch Folder", "No Watch Folder is saved yet. Choose a folder and save it first.");
            return;
        }

        _dialogs.OpenInExplorer(settings.WatchFolderPath);
    }

    [RelayCommand]
    private async Task ChooseDestinationAsync()
    {
        if (SelectedItem is null)
        {
            return;
        }

        var log = await _imports.GetLogAsync(SelectedItem.Id);
        if (log is null || log.Status != ImportStatuses.Matched)
        {
            return;
        }

        var settings = _settingsService.Load();
        var startFolder = log.ChosenFolder ?? _imports.GetDefaultDestinationFolder(log.IsSeries, settings) ?? log.SourcePath;
        var folder = _dialogs.PickFolder(startFolder, $"Choose the destination folder for '{log.ParsedTitle ?? log.SourceFile}'");
        if (folder is null)
        {
            return; // The file keeps waiting; nothing is moved without a choice.
        }

        try
        {
            await _imports.ChooseDestinationAsync(log.Id, folder);
            DestinationPreviewText = folder;
            StatusText = $"Destination chosen: {folder}. Confirm the move when the preview looks right.";
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            _dialogs.Error("Destination", ex.Message);
        }
    }

    [RelayCommand]
    private async Task ConfirmMoveAsync()
    {
        if (SelectedItem is null)
        {
            return;
        }

        var id = SelectedItem.Id;
        try
        {
            var outcome = await _imports.ConfirmImportAsync(id);
            if (outcome.Success)
            {
                StatusText = $"Imported: {outcome.Message}";
                ImportCompleted?.Invoke();
            }
            else
            {
                StatusText = $"Import failed: {outcome.Message}";
            }
        }
        catch (Exception ex)
        {
            _logger.Error("Confirming the import failed.", ex);
            StatusText = $"Import failed: {ex.Message}";
        }

        await RefreshAsync();
    }

    [RelayCommand]
    private async Task SkipFileAsync()
    {
        if (SelectedItem is null)
        {
            return;
        }

        try
        {
            await _imports.SkipAsync(SelectedItem.Id);
            StatusText = "File skipped — it stays in the Watch Folder.";
        }
        catch (Exception ex)
        {
            _dialogs.Error("Skip file", ex.Message);
        }

        await RefreshAsync();
    }
}
