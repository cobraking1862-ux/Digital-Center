namespace DigitalCenter.Core.Services;

/// <summary>Input passed from a Lookup availability row to its bound download workflow provider.</summary>
public sealed record DownloadRequest(
    string SourceId,
    string SourceName,
    string SourcePageUrl,
    string ImdbId,
    string MovieTitle,
    int? Year,
    string PreferredQuality);

/// <summary>One candidate download link discovered by a workflow provider before quality ranking.</summary>
public sealed record DownloadOption(
    string QualityLabel,
    Uri TargetUrl,
    string? FileName = null,
    string? HostLabel = null);

/// <summary>Standardized resolved download hand-off consumed by IdmLauncherService.</summary>
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

/// <summary>Structured outcome returned by any IDownloadWorkflowProvider.</summary>
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

/// <summary>
/// Pluggable workflow contract for an authorized movie download source.
/// Each provider owns its own resolution steps and returns either a standardized
/// ResolvedDownload or a clean NeedsBrowser / NotFound / Failed outcome.
/// </summary>
public interface IDownloadWorkflowProvider
{
    string ProviderId { get; }
    string DisplayName { get; }
    string Description { get; }

    Task<DownloadResolveOutcome> ResolveAsync(
        DownloadRequest request,
        IProgress<string>? progress = null,
        CancellationToken ct = default);
}
