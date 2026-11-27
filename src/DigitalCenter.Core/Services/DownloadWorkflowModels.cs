namespace DigitalCenter.Core.Services;

/// <summary>Input passed from one Lookup availability row to its assigned workflow.</summary>
public sealed record DownloadRequest(
    string SourceId,
    string SourceName,
    string SourcePageUrl,
    string ImdbId,
    string MovieTitle,
    int? Year,
    string PreferredQuality);

/// <summary>A candidate route discovered by a site-specific workflow before quality ranking.</summary>
public sealed record DownloadOption(
    string QualityLabel,
    Uri TargetUrl,
    string? FileName = null,
    string? HostLabel = null);

/// <summary>Verified destination handed to the existing IDM launcher.</summary>
public sealed class ResolvedDownload
{
    public string SourceId { get; init; } = string.Empty;
    public string MovieTitle { get; init; } = string.Empty;
    public string Quality { get; init; } = string.Empty;
    public Uri DownloadUrl { get; init; } = null!;
    public string? FileName { get; init; }
}

public enum DownloadResolveStatus
{
    Resolved,
    NeedsBrowser,
    NotFound,
    Failed
}

/// <summary>Outcome returned by the explicit site workflow dispatcher.</summary>
public sealed record DownloadResolveOutcome(
    DownloadResolveStatus Status,
    ResolvedDownload? Download,
    string Message,
    Uri? FallbackBrowserUrl = null)
{
    public bool IsSuccess => Status == DownloadResolveStatus.Resolved && Download is not null;

    public static DownloadResolveOutcome Success(ResolvedDownload download, string? message = null) =>
        new(DownloadResolveStatus.Resolved, download, message ?? $"Resolved {download.Quality} download link.");

    public static DownloadResolveOutcome BrowserRequired(string reason, Uri? openUrl = null) =>
        new(DownloadResolveStatus.NeedsBrowser, null, reason, openUrl);

    public static DownloadResolveOutcome NotFound(string reason) =>
        new(DownloadResolveStatus.NotFound, null, reason);

    public static DownloadResolveOutcome Error(string reason) =>
        new(DownloadResolveStatus.Failed, null, reason);
}
