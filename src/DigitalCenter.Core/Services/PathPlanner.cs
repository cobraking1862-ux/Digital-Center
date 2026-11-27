using System.Text.RegularExpressions;
using DigitalCenter.Core.Models;

namespace DigitalCenter.Core.Services;

/// <summary>
/// Plans file system paths for the language-based Watch Folder auto-organization
/// workflow and the USB copy queue.
/// </summary>
public static partial class PathPlanner
{
    [GeneratedRegex(@"[<>:""/\\|?*\x00-\x1F]")]
    private static partial Regex InvalidCharacters();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    /// <summary>Turns an arbitrary title or language into a safe folder name.</summary>
    public static string SanitizeName(string? name, int maxLength = 90)
    {
        var cleaned = InvalidCharacters().Replace(name ?? string.Empty, " ");
        cleaned = Whitespace().Replace(cleaned, " ").Trim(' ', '.', '_');

        if (cleaned.Length > maxLength)
        {
            cleaned = cleaned[..maxLength].TrimEnd();
        }

        if (cleaned.Length == 0)
        {
            cleaned = "Unknown";
        }

        foreach (var reserved in new[] { "CON", "PRN", "AUX", "NUL" })
        {
            if (string.Equals(cleaned, reserved, StringComparison.OrdinalIgnoreCase))
            {
                cleaned = reserved + "_";
            }
        }

        return cleaned;
    }

    public static string GetTitleFolderName(string title, int? year) =>
        year.HasValue ? $"{SanitizeName(title)} ({year.Value})" : SanitizeName(title);

    /// <summary>
    /// Resolves the language subfolder path inside the given library root
    /// (e.g. &lt;LibraryRoot&gt;/English). Avoids double-nesting if the base
    /// path already points at that language subfolder.
    /// </summary>
    public static string PlanLanguageFolderPath(string libraryRoot, string? language)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(libraryRoot));
        var safeLanguage = SanitizeName(
            string.IsNullOrWhiteSpace(language) ? LanguageResolver.DefaultLanguageFolder : language,
            60);

        var trailingName = Path.GetFileName(fullRoot);
        if (string.Equals(trailingName, safeLanguage, StringComparison.OrdinalIgnoreCase))
        {
            return fullRoot;
        }

        return Path.Combine(fullRoot, safeLanguage);
    }

    /// <summary>
    /// Resolves the movie collection subfolder path (e.g. &lt;CollectionsRoot&gt;/Harry Potter
    /// or &lt;MoviesRoot&gt;/Collections/Harry Potter). Avoids double-nesting if the base path
    /// already points at the Collections folder or the specific collection folder.
    /// </summary>
    public static string PlanCollectionFolderPath(string baseRoot, string collectionName)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(baseRoot));
        var safeCollection = SanitizeName(collectionName, 70);

        var trailingName = Path.GetFileName(fullRoot);
        if (string.Equals(trailingName, safeCollection, StringComparison.OrdinalIgnoreCase))
        {
            return fullRoot;
        }

        if (string.Equals(trailingName, CollectionResolver.DefaultCollectionsFolder, StringComparison.OrdinalIgnoreCase))
        {
            return Path.Combine(fullRoot, safeCollection);
        }

        return Path.Combine(fullRoot, CollectionResolver.DefaultCollectionsFolder, safeCollection);
    }

    /// <summary>
    /// Plans the full destination file path inside the target folder.
    /// </summary>
    public static string PlanImportTargetPath(string destinationFolder, string cleanFileName) =>
        Path.Combine(destinationFolder, SanitizeFileName(cleanFileName));

    /// <summary>
    /// Builds the clean import file name from the configured templates:
    /// movies use "{Title} ({Year})", episodes use "{Title} - S01E02".
    /// When the year or season/episode is unknown, those parts are dropped.
    /// </summary>
    public static string BuildImportFileName(
        string movieTemplate,
        string seriesTemplate,
        bool isSeries,
        string title,
        int? year,
        int? season,
        int? episode,
        string sourceFileName)
    {
        var template = isSeries ? seriesTemplate : movieTemplate;
        var stem = template
            .Replace("{Title}", SanitizeName(title))
            .Replace("{Year}", year?.ToString() ?? string.Empty)
            .Replace("{Season:00}", season?.ToString("00") ?? string.Empty)
            .Replace("{Episode:00}", episode?.ToString("00") ?? string.Empty)
            .Replace("{Season}", season?.ToString() ?? string.Empty)
            .Replace("{Episode}", episode?.ToString() ?? string.Empty);

        // Clean up empty parentheses "()" if year was missing.
        stem = stem.Replace("()", string.Empty);
        stem = Whitespace().Replace(stem, " ").Trim(' ', '-', '_');
        if (stem.Length == 0)
        {
            stem = SanitizeName(title, 120);
        }

        var extension = Path.GetExtension(sourceFileName);
        return string.IsNullOrEmpty(extension)
            ? stem
            : stem + extension.ToLowerInvariant();
    }

    /// <summary>Sanitizes a file name while preserving its extension.</summary>
    public static string SanitizeFileName(string fileName)
    {
        var name = Path.GetFileName(fileName);
        var extension = Path.GetExtension(name);
        var stem = SanitizeName(name[..^extension.Length], 120);
        return stem + extension.ToLowerInvariant();
    }

    /// <summary>Returns "{name} (2).ext" style variants until one does not exist in the directory.</summary>
    public static string ResolveFileNameConflict(string directory, string fileName)
    {
        if (!File.Exists(Path.Combine(directory, fileName)))
        {
            return fileName;
        }

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (var counter = 2; counter < 1000; counter++)
        {
            var candidate = $"{stem} ({counter}){extension}";
            if (!File.Exists(Path.Combine(directory, candidate)))
            {
                return candidate;
            }
        }

        return $"{Guid.NewGuid():N}{extension}";
    }

    /// <summary>
    /// Path of a library file relative to the library root (the shape mirrored
    /// onto USB drives), or null when the file lives outside the library.
    /// </summary>
    public static string? GetLibraryRelativePath(string? libraryRoot, string filePath)
    {
        if (string.IsNullOrWhiteSpace(libraryRoot))
        {
            return null;
        }

        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(libraryRoot)) + Path.DirectorySeparatorChar;
            var full = Path.GetFullPath(filePath);
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                ? full[root.Length..]
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Fallback relative target for files that are not under the library root.</summary>
    public static string BuildFallbackRelativePath(MediaKind kind, string categoryName, string title, int? year, string fileName)
    {
        var domain = kind == MediaKind.Series ? "Series" : "Movies";
        return Path.Combine(domain, SanitizeName(categoryName, 60), GetTitleFolderName(title, year), SanitizeFileName(fileName));
    }
}
