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
/// ManagementViewModel, part 3 of 3: the per-file actions in the activity list —
/// browse and move one file, confirm the selected move, or skip a file.
/// </summary>
public partial class ManagementViewModel
{
    /// <summary>
    /// The per-row "Browse / Move" action: opens the standard folder picker for
    /// THIS file and, once a destination is chosen, relocates that single file
    /// into it through the normal confirmed-import pipeline (metadata, artwork,
    /// catalog row and the move itself). Nothing happens when the picker is
    /// cancelled, and a file without a reliable match is reported instead of
    /// being moved somewhere it cannot be catalogued.
    /// </summary>
    [RelayCommand]
    private async Task MoveFileAsync(ActivityItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        if (!item.CanConfirm)
        {
            StatusText = $"'{item.FileName}' cannot be moved yet — status “{item.StatusText}”. " +
                         "A file needs a metadata match before it can be relocated into the library.";
            _activity.Publish(StatusText, ActivityKind.Warning);
            _dialogs.Info("Browse / Move", StatusText);
            return;
        }

        var log = await _imports.GetLogAsync(item.Id);
        if (log is null)
        {
            StatusText = $"'{item.FileName}' is no longer in the import list.";
            _activity.Publish(StatusText, ActivityKind.Warning);
            return;
        }

        var settings = _settingsService.Load();
        var startFolder = log.ChosenFolder
                          ?? _imports.GetDefaultDestinationFolder(log.IsSeries, settings)
                          ?? (string.IsNullOrEmpty(log.SourcePath) ? null : Path.GetDirectoryName(log.SourcePath));

        var folder = _dialogs.PickFolder(startFolder, $"Choose the destination folder for '{log.ParsedTitle ?? log.SourceFile}'");
        if (string.IsNullOrWhiteSpace(folder))
        {
            StatusText = "No folder was chosen — the file stays where it is.";
            return; // Cancelled: nothing is moved without an explicit choice.
        }

        SelectedItem = item;
        IsBusy = true;
        StatusText = $"Moving '{item.FileName}' to {folder} …";
        _activity.Publish($"Moving file to Library: '{item.FileName}' → {folder}", ActivityKind.FileOperation);

        try
        {
            var outcome = await Task.Run(() => _imports.ConfirmImportAsync(item.Id, folder));
            if (outcome.Success)
            {
                StatusText = $"Moved into the library: {outcome.Message}";
                _activity.Publish($"Moved '{item.FileName}' to {folder} successfully.", ActivityKind.Success);
                ImportCompleted?.Invoke();
            }
            else
            {
                StatusText = $"Move failed: {outcome.Message}";
                _activity.Publish($"Moving '{item.FileName}' failed: {outcome.Message}", ActivityKind.Error);
            }
        }
        catch (Exception ex)
        {
            _logger.Error("Moving a file to a chosen folder failed.", ex);
            StatusText = $"Move failed: {ex.Message}";
            _activity.Publish($"Moving '{item.FileName}' failed: {ex.Message}", ActivityKind.Error);
            _dialogs.Error("Browse / Move", StatusText);
        }
        finally
        {
            IsBusy = false;
        }

        await RefreshAsync();
    }

    [RelayCommand]
    private async Task ConfirmMoveAsync()
    {
        if (SelectedItem is null)
        {
            return;
        }

        var id = SelectedItem.Id;
        IsBusy = true;
        StatusText = "Importing the selected file …";
        _activity.Publish($"Moving file to Library: '{SelectedItem.FileName}' …", ActivityKind.FileOperation);
        try
        {
            // The move is real file I/O (copy, hash, delete): keep it off the UI
            // thread so the dashboard stays responsive during an import.
            var outcome = await Task.Run(() => _imports.ConfirmImportAsync(id));
            if (outcome.Success)
            {
                StatusText = $"Imported: {outcome.Message}";
                _activity.Publish($"Moved into the library: {outcome.Message}", ActivityKind.Success);
                ImportCompleted?.Invoke();
            }
            else
            {
                StatusText = $"Import failed: {outcome.Message}";
                _activity.Publish(StatusText, ActivityKind.Error);
            }
        }
        catch (Exception ex)
        {
            _logger.Error("Confirming the import failed.", ex);
            StatusText = $"Import failed: {ex.Message}";
            _activity.Publish(StatusText, ActivityKind.Error);
        }
        finally
        {
            IsBusy = false;
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
            var skipped = SelectedItem.FileName;
            await _imports.SkipAsync(SelectedItem.Id);
            StatusText = "File skipped — it stays in the Watch Folder.";
            _activity.Publish($"Skipped '{skipped}' — the file stays in the Watch Folder.", ActivityKind.System);
        }
        catch (Exception ex)
        {
            _dialogs.Error("Skip file", ex.Message);
        }

        await RefreshAsync();
    }
}
