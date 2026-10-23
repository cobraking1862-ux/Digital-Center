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

/// <summary>
/// ManagementViewModel, part 2 of 3: the Watch Folder itself — browsing for a
/// folder, saving the configuration, starting and stopping the monitor,
/// rescanning on demand and opening the folder in Explorer.
/// </summary>
public partial class ManagementViewModel
{
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
            _savedStabilityDelaySeconds = stability;
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
            _activity.Publish(StatusText, ActivityKind.Success);
            _logger.Info($"Watch Folder path saved: '{settings.WatchFolderPath ?? "(none)"}'.");
            _ = RefreshAsync();
        }
        catch (Exception ex)
        {
            _logger.Error("Saving the Watch Folder failed.", ex);
            StatusText = $"The Watch Folder could not be saved: {ex.Message}";
            _activity.Publish(StatusText, ActivityKind.Error);
            _dialogs.Error("Watch Folder", StatusText);
        }
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
            _activity.Publish("Watch Folder monitoring stopped.", ActivityKind.System);
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
            _activity.Publish(StatusText, ActivityKind.Background);
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
            StatusText = $"Scanning '{settings.WatchFolderPath}' …";
            _activity.Publish($"Fetching files from the Watch Folder: '{settings.WatchFolderPath}' …", ActivityKind.Background);

            // The whole pass (enumeration, stability window, TMDb matching) runs
            // on the thread pool so the window keeps repainting while it works.
            var scan = await Task.Run(() => _watchFolder.RescanConfiguredAsync());

            StatusText = scan.NewPending == 0 && scan.Unstable == 0
                ? $"Rescan finished: nothing new in '{settings.WatchFolderPath}' ({scan.AlreadyKnown} already known)."
                : $"Rescan finished: {scan.NewPending} new file(s), {scan.AlreadyKnown} known, " +
                  $"{scan.UnsupportedLogged} unsupported, {scan.Unstable} still being written.";
            _activity.Publish(StatusText, scan.NewPending > 0 ? ActivityKind.Success : ActivityKind.Background);
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            _logger.Error("Rescan failed.", ex);
            StatusText = $"Rescan failed: {ex.Message}";
            _activity.Publish(StatusText, ActivityKind.Error);
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
}
