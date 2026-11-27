using DigitalCenter.Core.Configuration;
using DigitalCenter.Core.Logging;

namespace DigitalCenter.Core.Services;

/// <summary>
/// Small explicit dispatcher for implemented site workflows. BigOne is scoped to
/// the stable built-in BollyFlix site ID and validates the actual source URL again.
/// </summary>
public sealed class DownloadWorkflowDispatcher
{
    private readonly BigOneDownloadWorkflow _bigOne;
    private readonly IAppLogger? _logger;

    public DownloadWorkflowDispatcher(BigOneDownloadWorkflow bigOne, IAppLogger? logger = null)
    {
        _bigOne = bigOne;
        _logger = logger;
    }

    public async Task<DownloadResolveOutcome> ResolveForSiteAsync(
        string? siteId,
        string? workflowId,
        DownloadRequest request,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(workflowId) ||
            workflowId.Equals(DownloadWorkflowIds.None, StringComparison.OrdinalIgnoreCase))
            return DownloadResolveOutcome.NotFound("This Lookup Site is configured for Lookup-only.");

        if (!workflowId.Equals(DownloadWorkflowIds.BigOne, StringComparison.OrdinalIgnoreCase))
            return DownloadResolveOutcome.NotFound("No implemented workflow is assigned to this Lookup Site.");

        if (!string.Equals(siteId, DownloadWorkflowIds.BigOneSite, StringComparison.OrdinalIgnoreCase))
            return DownloadResolveOutcome.NotFound("BigOne is assigned only to the designated BollyFlix Lookup Site.");

        if (!Uri.TryCreate(request.SourcePageUrl, UriKind.Absolute, out var sourceUri) ||
            !BigOneDownloadWorkflow.IsBollyFlixUri(sourceUri))
            return DownloadResolveOutcome.BrowserRequired(
                "BigOne rejected a source URL outside the designated BollyFlix host.",
                Uri.TryCreate(request.SourcePageUrl, UriKind.Absolute, out sourceUri) ? sourceUri : null);

        try
        {
            return await _bigOne.ResolveAsync(request, progress, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Avoid logging exception messages because HTTP exceptions can include signed URLs.
            _logger?.Warn($"BigOne workflow failed for '{request.MovieTitle}': {ex.GetType().Name}");
            return DownloadResolveOutcome.BrowserRequired(
                "BigOne could not safely verify this route. Open the original BollyFlix page in your browser.",
                sourceUri);
        }
    }
}
