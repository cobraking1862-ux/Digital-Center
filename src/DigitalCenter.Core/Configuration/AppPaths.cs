namespace DigitalCenter.Core.Configuration;

/// <summary>
/// Central definition of where DigitalCenter keeps its local data. Everything
/// lives under one data root (default: %APPDATA%\DigitalCenter) so the app
/// never writes next to its executable and never inside the media library.
/// </summary>
public sealed class AppPaths
{
    public required string DataRoot { get; init; }

    public string DatabaseFile => Path.Combine(DataRoot, "digitalcenter.db");
    public string PostersDirectory => Path.Combine(DataRoot, "Posters");
    public string LogsDirectory => Path.Combine(DataRoot, "Logs");
    public string BackupsDirectory => Path.Combine(DataRoot, "Backups");
    public string SettingsFile => Path.Combine(DataRoot, "settings.json");

    public static AppPaths CreateDefault()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return new AppPaths { DataRoot = Path.Combine(appData, "DigitalCenter") };
    }

    public void EnsureDataDirectories()
    {
        Directory.CreateDirectory(DataRoot);
        Directory.CreateDirectory(PostersDirectory);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(BackupsDirectory);
    }
}
