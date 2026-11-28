using System.IO;
using System.Text.Json;

namespace DigitalCenter.Core.Configuration;

/// <summary>Supported default movie download quality tiers (highest to lowest).</summary>
public static class DownloadQualityTiers
{
    public const string Q2160p = "2160p";
    public const string Q1080p = "1080p";
    public const string Q720p = "720p";
    public const string Q480p = "480p";

    public static readonly IReadOnlyList<string> All =
    [
        Q2160p,
        Q1080p,
        Q720p,
        Q480p
    ];
}

/// <summary>Stable provider IDs for built-in authorized download workflows.</summary>
public static class DownloadWorkflowIds
{
    public const string None = "";
    public const string DirectLink = "direct-link";
    public const string MultiStepLink = "multi-step-link";
    public const string BigOne = "big-one";
    public const string BigOneBrowser = "big-one-browser";
    public const string BigOneSite = "builtin-bollyflix";
    public const string BigOneSiteHost = "new1.bollyflix.tours";
    public const string BigOneLookupUrlTemplate = "https://new1.bollyflix.tours/?s={imdb_id}";
}

/// <summary>
/// Persisted Download Manager &amp; Workflow Provider settings (v14.0.0),
/// stored separately in download-settings.json so existing settings.json files
/// remain 100% backward-compatible with zero schema migrations.
/// </summary>
public sealed class DownloadSettings
{
    /// <summary>Optional custom path to IDMan.exe; leave blank to auto-detect.</summary>
    public string? IdmExecutablePath { get; set; }

    /// <summary>Default preferred movie quality (automatic downward fallback when unavailable).</summary>
    public string PreferredQuality { get; set; } = DownloadQualityTiers.Q1080p;

    /// <summary>When true, IDM's confirmation dialog defaults its save path to Movies Watch Folder.</summary>
    public bool RouteIdmToMoviesWatchFolder { get; set; } = true;

    /// <summary>When true, importing a movie via Watch Folder automatically marks matching Open tickets Completed.</summary>
    public bool AutoCompleteTicketsOnImport { get; set; } = true;

    /// <summary>Maps LookupSiteEntry.Id -&gt; registered workflow ProviderId.</summary>
    public Dictionary<string, string> SiteWorkflowBindings { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class DownloadSettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _filePath;

    public DownloadSettingsService(AppPaths paths)
    {
        _filePath = Path.Combine(paths.DataRoot, "download-settings.json");
    }

    public string SettingsFilePath => _filePath;

    /// <summary>
    /// Moves an existing BollyFlix assignment from the old HTTP-only workflow to
    /// the visible browser workflow. Other sites and their assignments are untouched.
    /// </summary>
    public bool MigrateBollyFlixBrowserBinding(IEnumerable<LookupSiteEntry> sites)
    {
        var settings = Load();
        var changed = false;
        foreach (var site in sites)
        {
            var isBollyFlix = string.Equals(site.Id, DownloadWorkflowIds.BigOneSite, StringComparison.OrdinalIgnoreCase) ||
                              (!string.IsNullOrWhiteSpace(site.UrlTemplate) &&
                               site.UrlTemplate.Contains(DownloadWorkflowIds.BigOneSiteHost, StringComparison.OrdinalIgnoreCase));
            if (!isBollyFlix ||
                !settings.SiteWorkflowBindings.TryGetValue(site.Id, out var providerId) ||
                string.IsNullOrWhiteSpace(providerId))
            {
                continue;
            }

            if (providerId.Equals(DownloadWorkflowIds.MultiStepLink, StringComparison.OrdinalIgnoreCase) ||
                providerId.Equals(DownloadWorkflowIds.BigOne, StringComparison.OrdinalIgnoreCase))
            {
                settings.SiteWorkflowBindings[site.Id] = DownloadWorkflowIds.BigOneBrowser;
                changed = true;
            }
        }

        if (changed)
        {
            Save(settings);
        }

        return changed;
    }

    public DownloadSettings Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return new DownloadSettings();
            }

            var json = File.ReadAllText(_filePath);
            var loaded = JsonSerializer.Deserialize<DownloadSettings>(json, JsonOptions) ?? new DownloadSettings();
            loaded.SiteWorkflowBindings ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (loaded.SiteWorkflowBindings.Comparer != StringComparer.OrdinalIgnoreCase)
            {
                loaded.SiteWorkflowBindings = new Dictionary<string, string>(loaded.SiteWorkflowBindings, StringComparer.OrdinalIgnoreCase);
            }

            if (string.IsNullOrWhiteSpace(loaded.PreferredQuality))
            {
                loaded.PreferredQuality = DownloadQualityTiers.Q1080p;
            }

            return loaded;
        }
        catch
        {
            return new DownloadSettings();
        }
    }

    public void Save(DownloadSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        var temp = _filePath + ".tmp";
        File.WriteAllText(temp, json);
        File.Move(temp, _filePath, overwrite: true);
    }

    public string? GetProviderIdForSite(string? siteId)
    {
        if (string.IsNullOrWhiteSpace(siteId)) return null;
        var settings = Load();
        return settings.SiteWorkflowBindings.TryGetValue(siteId.Trim(), out var pid) && !string.IsNullOrWhiteSpace(pid)
            ? pid.Trim()
            : null;
    }

    public void SetProviderIdForSite(string siteId, string? providerId)
    {
        if (string.IsNullOrWhiteSpace(siteId)) return;
        var settings = Load();
        var key = siteId.Trim();
        if (string.IsNullOrWhiteSpace(providerId))
        {
            settings.SiteWorkflowBindings.Remove(key);
        }
        else
        {
            settings.SiteWorkflowBindings[key] = providerId.Trim();
        }

        Save(settings);
    }
}
