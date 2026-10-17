using System.Text.RegularExpressions;
using DigitalCenter.Core.Models;

namespace DigitalCenter.Core.Services;

/// <summary>
/// Plans file system paths for the import workflow and the USB copy queue.
/// Library layout: Movies/{Category}/{Title (Year)}/ and
/// Series/{Category}/{Title (Year)}/Season NN/. USB destinations mirror the
/// library-relative path below the chosen drive root.
/// </summary>
public static partial class PathPlanner
{
    [GeneratedRegex(@"[<>:""/\\|?*\x00-\x1F]")]
    private static partial Regex InvalidCharacters();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    /// <summary>Turns an arbitrary title into a safe folder name.</summary>
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
    /// Full target path for an approved import. The file name is kept from the
    /// source; if the folder already contains a file with that name a numeric
    /// suffix is appended so nothing is ever overwritten silently.
    /// </summary>
    public static string PlanImportTargetPath(
        string libraryRoot,
        MediaKind kind,
        string categoryName,
        string title,
        int? year,
        int? season,
        string sourceFileName)
    {
        var domain = kind == MediaKind.Series ? "Series" : "Movies";
        var category = SanitizeName(categoryName, 60);
        var titleFolder = GetTitleFolderName(title, year);

        var directory = kind == MediaKind.Series && season.HasValue
            ? Path.Combine(libraryRoot, domain, category, titleFolder, $"Season {season.Value:00}")
            : Path.Combine(libraryRoot, domain, category, titleFolder);

        var fileName = SanitizeFileName(sourceFileName);
        return Path.Combine(directory, fileName);
    }

    /// <summary>
    /// Target path for the 5.0 workflow: the operator chooses the destination
    /// folder, so only the directory plus a clean file name are planned.
    /// There is no imposed category/year/genre hierarchy anywhere in this path.
    /// </summary>
    public static string PlanImportTargetPath(
        string destinationFolder,
        string cleanFileName)
    {
        return Path.Combine(destinationFolder, SanitizeFileName(cleanFileName));
    }

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
