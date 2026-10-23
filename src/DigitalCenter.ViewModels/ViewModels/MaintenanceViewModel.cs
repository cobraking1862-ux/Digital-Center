using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DigitalCenter.Core.Configuration;
using DigitalCenter.Core.Logging;
using DigitalCenter.Core.Services;
using DigitalCenter.ViewModels.Services;

namespace DigitalCenter.ViewModels;

/// <summary>Display wrapper around a backup file for the maintenance list.</summary>
public sealed class BackupItemViewModel
{
    public BackupItemViewModel(BackupFileInfo source)
    {
        Source = source;
        CreatedAtText = source.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        SizeText = UiFormat.FormatBytes(source.Size);
        FilePathText = source.FilePath;
    }

    public BackupFileInfo Source { get; }
    public string CreatedAtText { get; }
    public string SizeText { get; }
    public string FilePathText { get; }
}

/// <summary>Display row for one missing file report entry.</summary>
public sealed class MissingFileRow
{
    public MissingFileRow(MissingFileInfo info)
    {
        TitleDisplay = info.TitleDisplay;
        FilePath = info.FilePath;
        SizeText = UiFormat.FormatBytes(info.FileSize);
        AddedText = info.AddedAt.ToLocalTime().ToString("yyyy-MM-dd");
    }

    public string TitleDisplay { get; }
    public string FilePath { get; }
    public string SizeText { get; }
    public string AddedText { get; }
}

/// <summary>Display row for one duplicate-candidate group.</summary>
public sealed class DuplicateGroupRow
{
    public DuplicateGroupRow(DuplicateGroupInfo info)
    {
        Reason = info.Reason;
        TitleDisplay = info.TitleDisplay;
        SizeText = UiFormat.FormatBytes(info.FileSize);
        Paths = string.Join(Environment.NewLine, info.FilePaths);
        Count = info.FilePaths.Count;
    }

    public string Reason { get; }
    public string TitleDisplay { get; }
    public string SizeText { get; }
    public string Paths { get; }
    public int Count { get; }
}

public partial class MaintenanceViewModel : ObservableObject
{
    private readonly ActivityFeed _activity;
    private readonly AppPaths _paths;
    private readonly SettingsService _settingsService;
    private readonly LibraryScanner _scanner;
    private readonly BackupService _backupService;
    private readonly MaintenanceService _maintenance;
    private readonly IDialogService _dialogs;
    private readonly IAppLogger _logger;
    private readonly IAppControl _appControl;

    public MaintenanceViewModel(
        AppPaths paths,
        SettingsService settingsService,
        LibraryScanner scanner,
        BackupService backupService,
        MaintenanceService maintenance,
        ActivityFeed activity,
        IDialogService dialogs,
        IAppLogger logger,
        IAppControl appControl)
    {
        _activity = activity;
        _paths = paths;
        _settingsService = settingsService;
        _scanner = scanner;
        _backupService = backupService;
        _maintenance = maintenance;
        _dialogs = dialogs;
        _logger = logger;
        _appControl = appControl;

        ProgressText = string.Empty;
        ScanSummary = string.Empty;
        ReportSummary = string.Empty;

        ReloadBackups();
    }

    public ObservableCollection<BackupItemViewModel> Backups { get; } = [];
    public ObservableCollection<MissingFileRow> MissingFiles { get; } = [];
    public ObservableCollection<DuplicateGroupRow> Duplicates { get; } = [];

    [ObservableProperty]
    public partial string ProgressText { get; set; }

    [ObservableProperty]
    public partial string ScanSummary { get; set; }

    [ObservableProperty]
    public partial string ReportSummary { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial BackupItemViewModel? SelectedBackup { get; set; }

    public bool HasScanSummary => !string.IsNullOrEmpty(ScanSummary);

    public bool HasReportSummary => !string.IsNullOrEmpty(ReportSummary);

    public bool HasMissingFiles => MissingFiles.Count > 0;

    public bool HasDuplicates => Duplicates.Count > 0;

    partial void OnScanSummaryChanged(string value)
    {
        OnPropertyChanged(nameof(HasScanSummary));
        Publish(value, ActivityKind.Success);
    }

    partial void OnReportSummaryChanged(string value)
    {
        OnPropertyChanged(nameof(HasReportSummary));
        Publish(value, ActivityKind.Success);
    }

    /// <summary>
    /// Reports a result in the consolidated activity feed (first line only — the
    /// full text stays on the screen where it belongs).
    /// </summary>
    private void Publish(string value, ActivityKind kind)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var firstLine = value.Split('\n')[0].Trim().TrimEnd('·', '—', ':', ' ');
        _activity.Publish(firstLine.Length == 0 ? value.Trim() : firstLine, kind);
    }

    partial void OnIsBusyChanged(bool value)
    {
        ScanCommand.NotifyCanExecuteChanged();
        BackupCommand.NotifyCanExecuteChanged();
        RestoreCommand.NotifyCanExecuteChanged();
        FindMissingFilesCommand.NotifyCanExecuteChanged();
        FindDuplicatesCommand.NotifyCanExecuteChanged();
        RebuildSearchIndexCommand.NotifyCanExecuteChanged();
        ExportCsvCommand.NotifyCanExecuteChanged();
        ImportCsvCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanScan))]
    private async Task ScanAsync()
    {
        var settings = _settingsService.Load();
        if (string.IsNullOrWhiteSpace(settings.LibraryRoot))
        {
            _dialogs.Error("Scan", "Configure the library root in Settings first.");
            return;
        }

        if (!Directory.Exists(settings.LibraryRoot))
        {
            _dialogs.Error("Scan", $"The library root does not exist:\n{settings.LibraryRoot}");
            return;
        }

        IsBusy = true;
        ScanSummary = string.Empty;
        var progress = new Progress<ScanProgress>(p =>
            ProgressText = $"Scanning… {p.FilesSeen} file(s) seen — {Path.GetFileName(p.CurrentFile)}");

        try
        {
            var result = await _scanner.ScanAsync(settings.LibraryRoot, progress);
            ProgressText = $"Scan finished in {result.Duration.TotalSeconds:0.0} s.";

            var summary = new StringBuilder();
            summary.AppendLine($"Files seen: {result.FilesSeen}");
            summary.AppendLine($"Movie files registered: {result.MovieFilesRegistered} ({result.NewTitlesCreated} new title(s) total)");
            summary.AppendLine($"Episode files registered: {result.EpisodeFilesRegistered} ({result.NewSeasonsCreated} new season(s), {result.NewEpisodesCreated} new episode(s))");
            summary.AppendLine($"Duplicate paths skipped: {result.DuplicatePathsSkipped}");
            summary.AppendLine($"Unsupported files: {result.UnsupportedFiles}");
            if (result.Skipped.Count > 0)
            {
                summary.AppendLine();
                summary.AppendLine($"Skipped / unparsed ({result.Skipped.Count}):");
                foreach (var skipped in result.Skipped.Take(10))
                {
                    summary.AppendLine($"  • {Path.GetFileName(skipped.Path)} — {skipped.Reason}");
                }

                if (result.Skipped.Count > 10)
                {
                    summary.AppendLine($"  … and {result.Skipped.Count - 10} more (see log file).");
                }
            }

            ScanSummary = summary.ToString();
        }
        catch (OperationCanceledException)
        {
            ProgressText = "Scan cancelled.";
        }
        catch (Exception ex)
        {
            _logger.Error("Library scan failed.", ex);
            _dialogs.Error("Scan", $"The scan failed: {ex.Message}");
            _activity.Publish($"Library scan failed. ({ex.Message})", ActivityKind.Error);
            ProgressText = string.Empty;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanScan() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanBackup))]
    private async Task BackupAsync()
    {
        try
        {
            var file = await _backupService.BackupAsync();
            ReloadBackups();
            _dialogs.Info("Backup", $"Backup created:\n{file}");
        }
        catch (Exception ex)
        {
            _logger.Error("Backup failed.", ex);
            _dialogs.Error("Backup", $"The backup failed: {ex.Message}");
            _activity.Publish($"Backup failed. ({ex.Message})", ActivityKind.Error);
        }
    }

    private bool CanBackup() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanRestore))]
    private async Task RestoreAsync()
    {
        var selected = SelectedBackup?.Source.FilePath
                       ?? _dialogs.PickFile("Select a DigitalCenter backup", "DigitalCenter backup (*.db)|*.db|All files (*.*)|*.*");

        if (selected is null)
        {
            return;
        }

        if (!_dialogs.Confirm(
                "Restore database",
                "The current catalog will be replaced by the selected backup.\n" +
                "A safety copy of the current database is kept before restoring.\n\n" +
                "Continue?"))
        {
            return;
        }

        try
        {
            await _backupService.RestoreAsync(selected);
            _dialogs.Info(
                "Restore",
                "The database was restored. DigitalCenter will now close; please start it again to reload the restored catalog.");
            _appControl.Shutdown();
        }
        catch (Exception ex)
        {
            _logger.Error("Restore failed.", ex);
            _dialogs.Error("Restore", $"The restore failed: {ex.Message}");
            _activity.Publish($"Restore failed. ({ex.Message})", ActivityKind.Error);
        }
    }

    private bool CanRestore() => !IsBusy;

    [RelayCommand]
    private void OpenBackupsFolder() => _dialogs.OpenInExplorer(_paths.BackupsDirectory);

    private void ReloadBackups()
    {
        Backups.Clear();
        foreach (var backup in _backupService.ListBackups())
        {
            Backups.Add(new BackupItemViewModel(backup));
        }
    }

    // ------------------------------------------------------------ reports

    [RelayCommand(CanExecute = nameof(CanScan))]
    private async Task FindMissingFilesAsync()
    {
        IsBusy = true;
        try
        {
            var missing = await _maintenance.GetMissingFilesAsync();
            MissingFiles.Clear();
            foreach (var row in missing)
            {
                MissingFiles.Add(new MissingFileRow(row));
            }

            OnPropertyChanged(nameof(HasMissingFiles));
            ReportSummary = missing.Count == 0
                ? "No missing files: every catalog entry exists on disk."
                : $"{missing.Count} catalog file(s) no longer exist on disk. Review the list; DigitalCenter never deletes records or files automatically.";
        }
        catch (Exception ex)
        {
            _logger.Error("Missing-file report failed.", ex);
            _dialogs.Error("Reports", $"The missing-file report failed: {ex.Message}");
            _activity.Publish($"Missing-file report failed. ({ex.Message})", ActivityKind.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanScan))]
    private async Task FindDuplicatesAsync()
    {
        IsBusy = true;
        try
        {
            var groups = await _maintenance.GetDuplicateCandidatesAsync();
            Duplicates.Clear();
            foreach (var row in groups)
            {
                Duplicates.Add(new DuplicateGroupRow(row));
            }

            OnPropertyChanged(nameof(HasDuplicates));
            ReportSummary = groups.Count == 0
                ? "No duplicate candidates found."
                : $"{groups.Count} duplicate candidate group(s). DigitalCenter never deletes duplicates automatically — review the list and decide.";
        }
        catch (Exception ex)
        {
            _logger.Error("Duplicate report failed.", ex);
            _dialogs.Error("Reports", $"The duplicate report failed: {ex.Message}");
            _activity.Publish($"Duplicate report failed. ({ex.Message})", ActivityKind.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanScan))]
    private async Task RebuildSearchIndexAsync()
    {
        IsBusy = true;
        try
        {
            var count = await _maintenance.RebuildSearchIndexAsync();
            ReportSummary = $"Search index rebuilt for {count} title(s).";
        }
        catch (NotSupportedException ex)
        {
            _dialogs.Info("Search index", ex.Message);
        }
        catch (Exception ex)
        {
            _logger.Error("Search index rebuild failed.", ex);
            _dialogs.Error("Search index", $"The index could not be rebuilt: {ex.Message}");
            _activity.Publish($"Search index rebuild failed. ({ex.Message})", ActivityKind.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ------------------------------------------------------------ CSV

    [RelayCommand(CanExecute = nameof(CanScan))]
    private async Task ExportCsvAsync()
    {
        // The shared dialog service only offers an open dialog; ask for the
        // folder and derive the file name so the operator still controls the
        // location without a SaveFileDialog dependency in the VM layer.
        var folder = _dialogs.PickFolder(_paths.DataRoot);
        if (string.IsNullOrEmpty(folder))
        {
            return;
        }

        var target = Path.Combine(folder, $"digitalcenter-catalog-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
        if (File.Exists(target) && !_dialogs.Confirm("Overwrite file", $"Overwrite the existing file?\n{target}"))
        {
            return;
        }

        IsBusy = true;
        try
        {
            var rows = await _maintenance.ExportCsvAsync(target);
            ReportSummary = $"Exported {rows} row(s) to:\n{target}";
            _dialogs.Info("Export", $"The catalog was exported to:\n{target}");
        }
        catch (Exception ex)
        {
            _logger.Error("CSV export failed.", ex);
            _dialogs.Error("Export", $"The export failed: {ex.Message}");
            _activity.Publish($"CSV export failed. ({ex.Message})", ActivityKind.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanScan))]
    private async Task ImportCsvAsync()
    {
        var source = _dialogs.PickFile("Select a catalog CSV file", "CSV file (*.csv)|*.csv|All files (*.*)|*.*");
        if (string.IsNullOrEmpty(source))
        {
            return;
        }

        if (!_dialogs.Confirm(
                "Import catalog",
                "The import is additive: titles are created or merged, existing data is never deleted, and files are only registered (never moved).\n\nContinue?"))
        {
            return;
        }

        IsBusy = true;
        try
        {
            var summary = await _maintenance.ImportCsvAsync(source);
            var report = new StringBuilder();
            report.AppendLine($"Rows read: {summary.RowsRead}");
            report.AppendLine($"Titles created: {summary.TitlesCreated}");
            report.AppendLine($"Titles merged: {summary.TitlesMerged}");
            report.AppendLine($"Files registered: {summary.FilesRegistered}");
            report.AppendLine($"Files missing on disk: {summary.FilesMissing}");
            report.AppendLine($"Rows skipped: {summary.RowsSkipped}");
            foreach (var error in summary.Errors.Take(10))
            {
                report.AppendLine($"  • {error}");
            }

            ReportSummary = report.ToString();
        }
        catch (Exception ex)
        {
            _logger.Error("CSV import failed.", ex);
            _dialogs.Error("Import", $"The import failed: {ex.Message}");
            _activity.Publish($"CSV import failed. ({ex.Message})", ActivityKind.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }
}
