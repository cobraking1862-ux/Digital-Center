namespace DigitalCenter.ViewModels.Services;

/// <summary>Dialogs and shell actions the view models may request from the WPF host.</summary>
public interface IDialogService
{
    void Info(string title, string message);
    void Error(string title, string message);
    bool Confirm(string title, string message);
    string? PickFolder(string? initialPath = null, string? title = null);
    string? PickFile(string title, string filter);
    /// <summary>Opens Explorer with the file (or folder) selected/visible.</summary>
    void OpenInExplorer(string path);
    /// <summary>Opens a URL (e.g. a trailer) in the default browser.</summary>
    void OpenUrl(string url);
}

/// <summary>Application-level actions (shutdown, etc.).</summary>
public interface IAppControl
{
    void Shutdown();
}
