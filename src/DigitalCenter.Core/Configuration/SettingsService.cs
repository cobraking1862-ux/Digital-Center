using System.Text.Json;
using System.Text.Json.Serialization;

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
        MigrateLegacy(settings);
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
        if (string.IsNullOrWhiteSpace(settings.MoviesRoot) &&
            string.IsNullOrWhiteSpace(settings.SeriesRoot) &&
            !string.IsNullOrWhiteSpace(settings.LibraryRoot))
        {
            settings.MoviesRoot = Path.Combine(settings.LibraryRoot, "Movies");
            settings.SeriesRoot = Path.Combine(settings.LibraryRoot, "Series");
        }
    }

    /// <summary>
    /// Loads the current settings, applies <paramref name="change"/> and saves
    /// them again in one step. Every screen that owns only part of the
    /// configuration (Settings owns the library roots and preferences,
    /// Management owns the Watch Folder) must go through this method: building
    /// a fresh <see cref="AppSettings"/> and saving it would silently drop the
    /// values owned by the other screen.
    /// </summary>
    public AppSettings Update(Action<AppSettings> change)
    {
        ArgumentNullException.ThrowIfNull(change);

        var settings = Load();
        change(settings);
        Save(settings);
        return settings;
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
        ValidateRoot(settings.SeriesRoot, "Series library root", issues);

        issues.AddRange(ValidateWatchFolder(settings));

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

    /// <summary>
    /// Validates only the Watch Folder rules. The Watch Folder is configured in
    /// the Management workspace, so that screen validates its own input without
    /// being blocked by unrelated settings (an unset library root, for example).
    /// </summary>
    public static IReadOnlyList<string> ValidateWatchFolder(AppSettings settings)
    {
        var issues = new List<string>();

        if (!string.IsNullOrWhiteSpace(settings.WatchFolderPath))
        {
            if (!Path.IsPathRooted(settings.WatchFolderPath))
            {
                issues.Add("The Watch Folder must be an absolute path.");
            }
            else if (!Directory.Exists(settings.WatchFolderPath))
            {
                issues.Add($"The Watch Folder does not exist: '{settings.WatchFolderPath}'.");
            }

            foreach (var (root, label) in new[]
                     {
                         (settings.MoviesRoot, "movies library root"),
                         (settings.SeriesRoot, "series library root")
                     })
            {
                if (IsInside(root, settings.WatchFolderPath))
                {
                    issues.Add($"The Watch Folder must be outside the {label}.");
                }
            }
        }

        if (settings.StabilityDelaySeconds is < 2 or > 600)
        {
            issues.Add("The file-stability delay must be between 2 and 600 seconds.");
        }

        return issues;
    }

    private static void ValidateRoot(string? root, string label, List<string> issues)
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
            var p = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent)) + Path.DirectorySeparatorChar;
            var c = Path.TrimEndingDirectorySeparator(Path.GetFullPath(child)) + Path.DirectorySeparatorChar;
            return c.StartsWith(p, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }
}
