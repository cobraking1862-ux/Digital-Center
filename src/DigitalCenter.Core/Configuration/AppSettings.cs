namespace DigitalCenter.Core.Configuration;

/// <summary>One user-configured or built-in lookup site entry managed in Maintenance.</summary>
public sealed class LookupSiteEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Search URL template. Supports {imdb_id}, {title}, {year}, {query} (defaults to IMDb ID),
    /// and {title_plus}. If no placeholder is present, the resolved IMDb ID (or title) is appended.
    /// </summary>
    public string UrlTemplate { get; set; } = string.Empty;

    /// <summary>Optional site-specific text that indicates "no results" when found in the response body.</summary>
    public string? NoResultsMarker { get; set; }

    public bool IsEnabled { get; set; } = true;
    public bool IsBuiltIn { get; set; } = false;
}

/// <summary>
/// Operator-editable application settings, persisted as JSON in the data root.
/// </summary>
public sealed class AppSettings
{
    public int Version { get; set; } = 11;

    // ------------------------------------------------------------- library
    /// <summary>Absolute path of the folder that holds movies.</summary>
    public string? MoviesRoot { get; set; }

    /// <summary>Absolute path of the folder that holds multi-part Movie Collections (e.g. Harry Potter).</summary>
    public string? CollectionsRoot { get; set; }

    /// <summary>Legacy alias for CollectionsRoot / SeriesRoot retained for backwards compatibility.</summary>
    public string? SeriesRoot { get; set; }

    /// <summary>Unified library root (or legacy root migrated to Movies/Collections).</summary>
    public string? LibraryRoot { get; set; }

    // -------------------------------------------------------------- dramas
    /// <summary>Root folder where organized dramas live (one folder per drama).</summary>
    public string? DramaRoot { get; set; }

    /// <summary>Watch folder monitored for incoming drama subfolders and episode files.</summary>
    public string? DramaWatchFolderPath { get; set; }

    /// <summary>Whether Drama Watch Folder monitoring starts automatically with the application.</summary>
    public bool DramaWatchFolderEnabled { get; set; } = true;

    /// <summary>Subfolder created on USB/Android destinations for copied drama episodes.</summary>
    public string DramaCopySubfolder { get; set; } = "Dramas";

    // -------------------------------------------------------------- lookup
    /// <summary>User-managed websites for the Lookup panel (managed in Maintenance).</summary>
    public List<LookupSiteEntry> LookupSites { get; set; } = [];

    // -------------------------------------------------------- watch folder
    /// <summary>Absolute path of the single default Watch Folder.</summary>
    public string? WatchFolderPath { get; set; }

    /// <summary>Whether Watch Folder monitoring starts with the application.</summary>
    public bool WatchFolderEnabled { get; set; } = true;

    /// <summary>Whether a full watch-folder scan runs when the application starts.</summary>
    public bool ScanOnStartup { get; set; } = true;

    /// <summary>Seconds a file's size must stay unchanged before it counts as stable (2-600).</summary>
    public int StabilityDelaySeconds { get; set; } = 8;

    /// <summary>Also scan subfolders of the Watch Folder.</summary>
    public bool WatchFolderIncludeSubfolders { get; set; } = true;

    /// <summary>Minimum confidence (0.30-0.95) for an automatic TMDb match to be accepted.</summary>
    public double MinimumMatchConfidence { get; set; } = 0.50;

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
    /// Automatically move matched Watch Folder files into language sub-folders
    /// under the library root (creating the language sub-folder if none exists).
    /// </summary>
    public bool AutoOrganizeByLanguage { get; set; } = true;

    /// <summary>
    /// When true, wait for manual operator confirmation before moving matched
    /// files. Defaults to false so Watch Folder files are auto-organized by language.
    /// </summary>
    public bool AskForDestination { get; set; } = false;

    /// <summary>Rename imported files to the clean "Title (Year)" pattern instead of keeping the release name.</summary>
    public bool CleanFilenames { get; set; } = true;

    /// <summary>Skip | Overwrite | Rename — applied when the destination file already exists.</summary>
    public string UsbConflictAction { get; set; } = "Skip";

    /// <summary>When true, a SHA-256 hash is verified in addition to the file size after copying.</summary>
    public bool VerifyHashes { get; set; }

    // ----------------------------------------------------------------- USB
    /// <summary>Single folder created on the USB drive where all queued files are copied.</summary>
    public string UsbCopySubfolder { get; set; } = "DigitalCenter";

    /// <summary>Kept false by default so USB copies go directly into the single UsbCopySubfolder.</summary>
    public bool CreatePerTitleFolders { get; set; } = false;

    // ---------------------------------------------------- naming (import)
    /// <summary>Template for imported movie file names. Supported: {Title}, {Year}.</summary>
    public string MovieFilenameTemplate { get; set; } = "{Title} ({Year})";

    /// <summary>Template for imported episode file names. Supported: {Title}, {Season}, {Episode}.</summary>
    public string SeriesFilenameTemplate { get; set; } = "{Title} - S{Season:00}E{Episode:00}";

    /// <summary>Resolves the effective library root for standalone movies.</summary>
    public string? GetEffectiveMoviesRoot()
    {
        if (!string.IsNullOrWhiteSpace(MoviesRoot))
        {
            return MoviesRoot;
        }

        if (!string.IsNullOrWhiteSpace(LibraryRoot))
        {
            return LibraryRoot;
        }

        return CollectionsRoot ?? SeriesRoot;
    }

    /// <summary>
    /// Resolves the effective root folder for Movie Collections (separate from regional language folders).
    /// Uses CollectionsRoot if configured; otherwise &lt;MoviesRoot&gt;/Collections or &lt;LibraryRoot&gt;/Collections.
    /// </summary>
    public string? GetEffectiveCollectionsRoot()
    {
        if (!string.IsNullOrWhiteSpace(CollectionsRoot))
        {
            return CollectionsRoot;
        }

        if (!string.IsNullOrWhiteSpace(MoviesRoot))
        {
            return Path.Combine(MoviesRoot, "Collections");
        }

        if (!string.IsNullOrWhiteSpace(LibraryRoot))
        {
            return Path.Combine(LibraryRoot, "Collections");
        }

        return SeriesRoot;
    }

    /// <summary>Resolves the effective library root for standalone movies vs collections.</summary>
    public string? GetEffectiveRoot(bool isCollection = false) =>
        isCollection ? GetEffectiveCollectionsRoot() : GetEffectiveMoviesRoot();

    /// <summary>Returns all distinct configured library root directories that exist on disk.</summary>
    public IReadOnlyList<string> GetConfiguredLibraryRoots()
    {
        var candidates = new[] { LibraryRoot, MoviesRoot, CollectionsRoot, SeriesRoot }
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => Path.TrimEndingDirectorySeparator(Path.GetFullPath(p!)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return candidates
            .Where(c => !candidates.Any(other =>
                !string.Equals(other, c, StringComparison.OrdinalIgnoreCase) &&
                c.StartsWith(other + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }
}
