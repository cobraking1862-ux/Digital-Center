using System.Diagnostics;
using System.IO;
using System.Windows;
using DigitalCenter.ViewModels.Services;
using Microsoft.Win32;

namespace DigitalCenter.App.Services;

/// <summary>Standard WPF dialogs (MessageBox, OpenFolderDialog, OpenFileDialog).</summary>
public sealed class DialogService : IDialogService
{
    private static Window? Owner => Application.Current?.MainWindow;

    public void Info(string title, string message) =>
        MessageBox.Show(Owner, message, title, MessageBoxButton.OK, MessageBoxImage.Information);

    public void Error(string title, string message) =>
        MessageBox.Show(Owner, message, title, MessageBoxButton.OK, MessageBoxImage.Warning);

    public bool Confirm(string title, string message) =>
        MessageBox.Show(Owner, message, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    public string? PickFolder(string? initialPath = null, string? title = null)
    {
        var dialog = new OpenFolderDialog
        {
            Title = string.IsNullOrWhiteSpace(title) ? "Select a folder" : title
        };

        if (Directory.Exists(initialPath))
        {
            dialog.InitialDirectory = initialPath;
        }

        return dialog.ShowDialog(Owner) == true ? dialog.FolderName : null;
    }

    public string? PickFile(string title, string filter)
    {
        var dialog = new OpenFileDialog
        {
            Title = title,
            Filter = filter,
            CheckFileExists = true
        };

        return dialog.ShowDialog(Owner) == true ? dialog.FileName : null;
    }

    public void OpenInExplorer(string path)
    {
        if (File.Exists(path))
        {
            // Select the actual media file inside its containing folder.
            Process.Start("explorer.exe", $"/select,\"{path}\"");
            return;
        }

        if (Directory.Exists(path))
        {
            Process.Start("explorer.exe", path);
            return;
        }

        Error("Not found", $"The path does not exist:\n{path}");
    }

    public void OpenUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Error("Browser", $"The link could not be opened: {ex.Message}");
        }
    }
}

/// <summary>Application shell actions for view models.</summary>
public sealed class AppControl : IAppControl
{
    public void Shutdown() => Application.Current.Shutdown();
}
