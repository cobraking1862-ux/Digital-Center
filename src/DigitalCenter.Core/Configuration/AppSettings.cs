namespace DigitalCenter.Core.Configuration;

/// <summary>
/// Operator-editable application settings, persisted as JSON in the data root.
/// Note: the TMDb read access token ("Tmdb:ReadAccessToken") is intentionally
/// NOT stored here; it comes from .NET user secrets during development or an
/// external configuration file in deployment. Never commit real credentials.
/// </summary>
public sealed class AppSettings
{
    public int Version { get; set; } = 5;

    // ------------------------------------------------------------- library
    /// <summary>Absolute path of the folder that holds movies.</summary>
    public string? MoviesRoot { get; set; }

    /// <summary>Absolute path of the folder that holds series.</summary>
    public string? SeriesRoot { get; set; }

    /// <summary>Legacy single library root from DigitalCenter 1.x-4.x. Kept only
    /// so old settings files can be migrated (see SettingsService.Load).</summary>
    public string? LibraryRoot { get; set; }

    // -------------------------------------------------------- watch folder
    /// <summary>Absolute path of the single default Watch Folder.</summary>
    public string? WatchFolderPath { get; set; }

    /// <summary>Whether Watch Folder monitoring starts with the application.</summary>
    public bool WatchFolderEnabled { get; set; } = true;

    /// <summary>Whether a full watch-folder scan runs when the application starts.</summary>
    public bool ScanOnStartup { get; set; } = true;

    /// <summary>Seconds a file's size must stay unchanged before it counts as stable (5-600).</summary>
    public int StabilityDelaySeconds { get; set; } = 8;

    /// <summary>Also scan subfolders of the Watch Folder.</summary>
    public bool WatchFolderIncludeSubfolders { get; set; } = true;

    /// <summary>Minimum confidence (0.30-0.95) for an automatic TMDb match to be accepted.</summary>
    public double MinimumMatchConfidence { get; set; } = 0.60;

    // ------------------------------------------------------------ metadata
    /// <summary>Download poster artwork for matched titles.</summary>
    public bool DownloadPosters { get; set; } = true;

    /// <summary>Download backdrop artwork for matched titles.</summary>
    public bool DownloadBackdrops { get; set; } = true;

    /// <summary>Download cast profile images for matched titles.</summary>
    public bool DownloadCastProfiles { get; set; } = true;

    /// <summary>Store trailer links when TMDb provides them.</summary>
    public bool FetchTrailers { get; set; } = true;

    // ------------------------------------------------------ file operations
    /// <summary>
    /// Move a matched Watch Folder file into the library automatically, without
    /// waiting for the operator to confirm a destination. When this is off,
    /// matched rows stay "Awaiting destination" until they are confirmed or
    /// moved by hand with the per-file Browse / Move button.
    /// </summary>
    public bool AutoImportMatchedFiles { get; set; } = true;

    /// <summary>
    /// Organize the library by the detected original language: an import lands in
    /// "&lt;MoviesRoot|SeriesRoot&gt;\&lt;Language&gt;" and the language folder is
    /// created when it does not exist yet. Titles without a known language go
    /// straight into the root.
    /// </summary>
    public bool OrganizeLibraryByLanguage { get; set; } = true;

    /// <summary>Rename imported files to the clean "Title (Year)" pattern instead of keeping the release name.</summary>
    public bool CleanFilenames { get; set; } = true;

    /// <summary>Skip | Overwrite | Rename — applied when the destination file already exists.</summary>
    public string UsbConflictAction { get; set; } = "Skip";

    /// <summary>When true, a SHA-256 hash is verified in addition to the file size after copying.</summary>
    public bool VerifyHashes { get; set; }

    // ----------------------------------------------------------------- USB
    /// <summary>Subfolder created on the USB drive; the library layout is mirrored below it.</summary>
    public string UsbCopySubfolder { get; set; } = "DigitalCenter";

    /// <summary>Create one folder per title on the USB drive instead of copying flat.</summary>
    public bool CreatePerTitleFolders { get; set; } = true;

    // ---------------------------------------------------- naming (import)
    /// <summary>Template for imported movie file names. Supported: {Title}, {Year}.</summary>
    public string MovieFilenameTemplate { get; set; } = "{Title} ({Year})";

    /// <summary>Template for imported episode file names. Supported: {Title}, {Season}, {Episode}.</summary>
    public string SeriesFilenameTemplate { get; set; } = "{Title} - S{Season:00}E{Episode:00}";
}
