using System.Text.Json;
using System.Text.Json.Serialization;
using DigitalCenter.Core.Services;

namespace DigitalCenter.Core.Configuration;

/// <summary>
/// Loads and saves <see cref="AppSettings"/> as JSON with an atomic write,
/// migrates legacy settings files and validates operator input.
/// </summary>
public sealed class SettingsService
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _settingsFile;

    public SettingsService(AppPaths paths)
    {
        _settingsFile = paths.SettingsFile;
    }

    public AppSettings Load()
    {
        var settings = LoadRaw();
        var previousVersion = settings.Version;
        MigrateLegacy(settings);
        if (settings.Version != previousVersion && File.Exists(_settingsFile))
        {
            Save(settings);
        }

        return settings;
    }

    private AppSettings LoadRaw()
    {
        if (!File.Exists(_settingsFile))
        {
            return new AppSettings();
        }

        try
        {
            using var stream = File.OpenRead(_settingsFile);
            return JsonSerializer.Deserialize<AppSettings>(stream, SerializerOptions) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // A corrupt settings file must not prevent startup; keep defaults.
            return new AppSettings();
        }
    }

    /// <summary>
    /// DigitalCenter 1.x-4.x used one LibraryRoot plus category subfolders.
    /// The layout maps onto the new model additively; nothing on disk changes.
    /// </summary>
    public static void MigrateLegacy(AppSettings settings)
    {
        settings.LookupSites ??= [];

        if (settings.Version < 11)
        {
            if (settings.LookupSites.Count > 0 &&
                !settings.LookupSites.Any(site =>
                    string.Equals(site.Id, "builtin-bollyflix", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(site.Name, "BollyFlix", StringComparison.OrdinalIgnoreCase) ||
                    (!string.IsNullOrWhiteSpace(site.UrlTemplate) &&
                     site.UrlTemplate.Contains("new1.bollyflix.tours", StringComparison.OrdinalIgnoreCase))))
            {
                var bollyFlix = ImdbLookupService.GetDefaultSites().First(site =>
                    site.Id.Equals("builtin-bollyflix", StringComparison.OrdinalIgnoreCase));
                settings.LookupSites.Add(bollyFlix);
            }

            settings.Version = 11;
        }

        if (string.IsNullOrWhiteSpace(settings.MoviesRoot) &&
            string.IsNullOrWhiteSpace(settings.SeriesRoot) &&
            !string.IsNullOrWhiteSpace(settings.LibraryRoot))
        {
            settings.MoviesRoot = Path.Combine(settings.LibraryRoot, "Movies");
            settings.SeriesRoot = Path.Combine(settings.LibraryRoot, "Series");
        }

        if (string.IsNullOrWhiteSpace(settings.CollectionsRoot) &&
            !string.IsNullOrWhiteSpace(settings.MoviesRoot))
        {
            settings.CollectionsRoot = Path.Combine(settings.MoviesRoot, "Collections");
        }
    }

    public void Save(AppSettings settings)
    {
        var directory = Path.GetDirectoryName(_settingsFile);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempFile = _settingsFile + ".tmp";
        using (var stream = File.Create(tempFile))
        {
            JsonSerializer.Serialize(stream, settings, SerializerOptions);
        }

        File.Move(tempFile, _settingsFile, overwrite: true);
    }

    /// <summary>Returns human-readable problems with the current settings (empty when valid).</summary>
    public static IReadOnlyList<string> Validate(AppSettings settings)
    {
        var issues = new List<string>();

        ValidateRoot(settings.MoviesRoot, "Movies library root", issues);
        ValidateRoot(settings.CollectionsRoot, "Collections library root", issues, allowNonExistentIfParentExists: true);
        ValidateRoot(settings.SeriesRoot, "Series library root", issues, allowNonExistentIfParentExists: true);

        issues.AddRange(ValidateWatchFolderPath(settings));

        if (settings.StabilityDelaySeconds is < 2 or > 600)
        {
            issues.Add("The file-stability delay must be between 2 and 600 seconds.");
        }

        if (settings.MinimumMatchConfidence is < 0.30 or > 0.95)
        {
            issues.Add("The minimum match confidence must be between 0.30 and 0.95.");
        }

        if (!settings.MovieFilenameTemplate.Contains("{Title}", StringComparison.Ordinal))
        {
            issues.Add("The movie file-name template must contain {Title}.");
        }

        if (!settings.SeriesFilenameTemplate.Contains("{Title}", StringComparison.Ordinal))
        {
            issues.Add("The series file-name template must contain {Title}.");
        }

        return issues;
    }

    /// <summary>Validates the Watch Folder path and prevents recursive library rediscovery.</summary>
    public static IReadOnlyList<string> ValidateWatchFolderPath(AppSettings settings)
    {
        var issues = new List<string>();
        var watchFolder = settings.WatchFolderPath;
        if (string.IsNullOrWhiteSpace(watchFolder))
        {
            return issues;
        }

        if (!Path.IsPathRooted(watchFolder))
        {
            issues.Add("The Watch Folder must be an absolute path.");
            return issues;
        }

        if (!Directory.Exists(watchFolder))
        {
            issues.Add($"The Watch Folder does not exist: '{watchFolder}'.");
        }

        foreach (var (root, label) in new[]
                 {
                     (settings.MoviesRoot, "movies library root"),
                     (settings.SeriesRoot, "series library root")
                 })
        {
            // Reject nesting in either direction. A Watch Folder inside a
            // library is confusing; a library inside the Watch Folder can
            // cause imported files to be rediscovered recursively.
            if (IsInside(root, watchFolder) || IsInside(watchFolder, root))
            {
                issues.Add($"The Watch Folder must be outside and must not overlap with the {label}; choose a separate folder tree.");
            }
        }

        return issues;
    }

    private static void ValidateRoot(string? root, string label, List<string> issues, bool allowNonExistentIfParentExists = false)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return; // Roots are optional; features that need them explain that in place.
        }

        if (!Path.IsPathRooted(root))
        {
            issues.Add($"{label} must be an absolute path.");
        }
        else if (!Directory.Exists(root))
        {
            var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)));
            if (allowNonExistentIfParentExists && !string.IsNullOrWhiteSpace(parent) && Directory.Exists(parent))
            {
                return;
            }

            issues.Add($"{label} does not exist: '{root}'.");
        }
    }

    private static bool IsInside(string? parent, string? child)
    {
        if (string.IsNullOrWhiteSpace(parent) || string.IsNullOrWhiteSpace(child))
        {
            return false;
        }

        try
        {
            var p = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));
            var c = Path.TrimEndingDirectorySeparator(Path.GetFullPath(child));
            if (string.Equals(p, c, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var parentPrefix = Path.EndsInDirectorySeparator(p) ? p : p + Path.DirectorySeparatorChar;
            var childPath = Path.EndsInDirectorySeparator(c) ? c : c + Path.DirectorySeparatorChar;
            return childPath.StartsWith(parentPrefix, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }
}
