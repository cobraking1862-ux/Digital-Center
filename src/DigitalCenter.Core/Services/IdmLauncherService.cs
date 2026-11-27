using System.Diagnostics;
using System.IO;
using DigitalCenter.Core.Configuration;
using DigitalCenter.Core.Logging;

namespace DigitalCenter.Core.Services;

public sealed record IdmLaunchResult(
    bool Launched,
    string Message,
    string? ExecutablePath = null,
    string? ArgumentsUsed = null);

/// <summary>
/// Launches Internet Download Manager's normal Download File Info confirmation dialog
/// (/d "&lt;url&gt;" /p "&lt;folder&gt;" /f "&lt;filename&gt;" — never silent /n mode)
/// so the operator confirms each download in IDM and finished files land directly
/// in DigitalCenter's Movies Watch Folder.
/// </summary>
public sealed class IdmLauncherService
{
    private static readonly HashSet<string> KnownDownloadExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".mp4", ".avi", ".mov", ".m4v", ".webm", ".ts", ".wmv", ".zip", ".rar", ".7z"
    };

    private readonly IAppLogger? _logger;

    public IdmLauncherService(IAppLogger? logger = null)
    {
        _logger = logger;
    }

    public static string? DetectIdmExecutablePath()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Internet Download Manager", "IDMan.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Internet Download Manager", "IDMan.exe"),
            @"C:\Program Files (x86)\Internet Download Manager\IDMan.exe",
            @"C:\Program Files\Internet Download Manager\IDMan.exe"
        };

        foreach (var path in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }

    public static string? ResolveExecutablePath(string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            var trimmed = configuredPath.Trim().Trim('"');
            if (File.Exists(trimmed))
            {
                return trimmed;
            }
        }

        return DetectIdmExecutablePath();
    }

    public static string BuildSuggestedFileName(ResolvedDownload resolved)
    {
        if (!string.IsNullOrWhiteSpace(resolved.FileName))
        {
            var cleaned = PathPlanner.SanitizeName(Path.GetFileName(resolved.FileName.Trim()), 140);
            if (!string.IsNullOrWhiteSpace(cleaned))
            {
                return cleaned;
            }
        }

        var ext = ".mkv";
        if (resolved.DownloadUrl is not null)
        {
            var urlExt = Path.GetExtension(resolved.DownloadUrl.AbsolutePath);
            if (!string.IsNullOrWhiteSpace(urlExt) && KnownDownloadExtensions.Contains(urlExt))
            {
                ext = urlExt;
            }
        }

        var baseTitle = PathPlanner.SanitizeName(
            string.IsNullOrWhiteSpace(resolved.MovieTitle) ? "Movie" : resolved.MovieTitle,
            100);
        var quality = QualityPreferenceMatcher.NormalizeQualityLabel(resolved.Quality);
        return string.IsNullOrWhiteSpace(quality)
            ? $"{baseTitle}{ext}"
            : $"{baseTitle} [{quality}]{ext}";
    }

    /// <summary>
    /// Builds IDM CLI arguments without /n (silent mode), ensuring IDM always opens
    /// its normal Download File Info confirmation dialog.
    /// </summary>
    public static string BuildIdmArguments(Uri downloadUrl, string? destinationFolder, string? suggestedFileName)
    {
        if (downloadUrl is null ||
            (downloadUrl.Scheme != Uri.UriSchemeHttp && downloadUrl.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("Download URL must be an absolute http or https URI.", nameof(downloadUrl));
        }

        var parts = new List<string>
        {
            $"/d \"{EscapeArg(downloadUrl.AbsoluteUri)}\""
        };

        if (!string.IsNullOrWhiteSpace(destinationFolder))
        {
            parts.Add($"/p \"{EscapeArg(destinationFolder.Trim())}\"");
        }

        if (!string.IsNullOrWhiteSpace(suggestedFileName))
        {
            parts.Add($"/f \"{EscapeArg(suggestedFileName.Trim())}\"");
        }

        return string.Join(" ", parts);
    }

    public IdmLaunchResult LaunchDownload(
        ResolvedDownload resolved,
        DownloadSettings downloadSettings,
        AppSettings appSettings)
    {
        if (resolved?.DownloadUrl is null ||
            (resolved.DownloadUrl.Scheme != Uri.UriSchemeHttp && resolved.DownloadUrl.Scheme != Uri.UriSchemeHttps))
        {
            return new IdmLaunchResult(false, "Resolved URL is not a valid http/https address.");
        }

        var idmExe = ResolveExecutablePath(downloadSettings.IdmExecutablePath);
        if (string.IsNullOrWhiteSpace(idmExe))
        {
            return new IdmLaunchResult(
                false,
                "IDMan.exe was not found. Configure the IDM path in Maintenance → Lookup Sites → Download Manager, or use Open in Browser.");
        }

        string? destinationFolder = null;
        if (downloadSettings.RouteIdmToMoviesWatchFolder &&
            !string.IsNullOrWhiteSpace(appSettings.WatchFolderPath))
        {
            destinationFolder = appSettings.WatchFolderPath.Trim();
            try
            {
                Directory.CreateDirectory(destinationFolder);
            }
            catch (Exception ex)
            {
                _logger?.Warn($"Could not create Movies Watch Folder '{destinationFolder}' before IDM hand-off: {ex.Message}");
            }
        }

        var fileName = BuildSuggestedFileName(resolved);
        var args = BuildIdmArguments(resolved.DownloadUrl, destinationFolder, fileName);

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = idmExe,
                Arguments = args,
                UseShellExecute = true
            };
            Process.Start(psi);

            var destNote = !string.IsNullOrWhiteSpace(destinationFolder)
                ? $" → {destinationFolder}"
                : string.Empty;
            _logger?.Info($"Sent '{fileName}' ({resolved.Quality}) to IDM{destNote}.");
            return new IdmLaunchResult(
                true,
                $"Opened IDM confirmation dialog for '{fileName}' ({resolved.Quality}){destNote}.",
                idmExe,
                args);
        }
        catch (Exception ex)
        {
            _logger?.Error($"Launching IDM failed: {ex.Message}", ex);
            return new IdmLaunchResult(false, $"Could not launch IDM: {ex.Message}", idmExe, args);
        }
    }

    private static string EscapeArg(string value) => value.Replace("\"", "\\\"");
}
