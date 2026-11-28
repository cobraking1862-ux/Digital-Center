using DigitalCenter.Core.Configuration;
using DigitalCenter.Core.Logging;

namespace DigitalCenter.Core.Services;

/// <summary>
/// Central registry of independent, pluggable movie download workflow providers.
/// Each workflow is isolated by its stable ProviderId; failures in one provider
/// never affect Lookup, IDM, or other providers. The BigOne visible browser
/// workflow is the only built-in provider.
/// </summary>
public sealed class DownloadProviderRegistry : IDisposable
{
    private readonly Dictionary<string, IDownloadWorkflowProvider> _providers = new(StringComparer.OrdinalIgnoreCase);
    private readonly IAppLogger? _logger;

    public DownloadProviderRegistry(IEnumerable<IDownloadWorkflowProvider> providers, IAppLogger? logger = null)
    {
        _logger = logger;
        foreach (var provider in providers)
        {
            if (!string.IsNullOrWhiteSpace(provider.ProviderId))
            {
                _providers[provider.ProviderId.Trim()] = provider;
            }
        }
    }

    public static DownloadProviderRegistry CreateDefault(
        IAppLogger? logger = null,
        AppPaths? paths = null)
    {
        var appPaths = paths ?? AppPaths.CreateDefault();
        return new DownloadProviderRegistry(
        [
            new BigOneBrowserDownloadWorkflow(appPaths, logger)
        ], logger);
    }

    public void Dispose()
    {
        foreach (var provider in _providers.Values.OfType<IDisposable>().Distinct())
        {
            provider.Dispose();
        }
    }

    public bool HasProvider(string? providerId) =>
        !string.IsNullOrWhiteSpace(providerId) && _providers.ContainsKey(providerId.Trim());

    public bool TryGetProvider(string? providerId, out IDownloadWorkflowProvider? provider)
    {
        if (string.IsNullOrWhiteSpace(providerId))
        {
            provider = null;
            return false;
        }

        return _providers.TryGetValue(providerId.Trim(), out provider);
    }

    public IReadOnlyList<IDownloadWorkflowProvider> GetAllProviders() =>
        _providers.Values.OrderBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();

    public async Task<DownloadResolveOutcome> ResolveForSiteAsync(
        string? providerId,
        DownloadRequest request,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        if (!TryGetProvider(providerId, out var provider) || provider is null)
        {
            return DownloadResolveOutcome.NotFound("This site is Lookup-only (no download workflow assigned).");
        }

        try
        {
            return await provider.ResolveAsync(request, progress, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.Warn($"Download workflow '{provider.ProviderId}' failed for '{request.MovieTitle}': {ex.Message}");
            return DownloadResolveOutcome.Error($"Workflow error: {ex.Message}");
        }
    }
}
