using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using DigitalCenter.Core.Configuration;
using DigitalCenter.Core.Logging;

namespace DigitalCenter.Core.Services;

/// <summary>
/// Central registry of independent, pluggable movie download workflow providers.
/// Each workflow is isolated by its stable ProviderId; failures in one provider
/// never affect Lookup, IDM, or other providers.
/// </summary>
public sealed class DownloadProviderRegistry
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

    public static DownloadProviderRegistry CreateDefault(HttpClient? httpClient = null, IAppLogger? logger = null)
    {
        var client = httpClient ?? CreateDefaultHttpClient();
        return new DownloadProviderRegistry(
        [
            new DirectLinkDownloadWorkflow(client),
            new MultiStepLinkDownloadWorkflow(client)
        ], logger);
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

    internal static HttpClient CreateDefaultHttpClient()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5
        };

        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("DigitalCenter/14.0 (+https://github.com/CBAJoker/CBA-Movie-Explorer)");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json,text/html,application/xhtml+xml;q=0.9,*/*;q=0.8");
        return client;
    }
}

/// <summary>Shared HTML/JSON parsing and safety checks used by authorized workflow providers.</summary>
internal static class WorkflowHttpHelpers
{
    private static readonly Regex ChromeStripRegex = new(
        @"<(nav|header|footer|aside|script|style|noscript)\b[^>]*>.*?</\1>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private static readonly Regex AnchorRegex = new(
        @"<a\b[^>]*\bhref\s*=\s*(?:""(?<href>[^""]+)""|'(?<href>[^']+)')[^>]*>(?<text>.*?)</a>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private static readonly Regex TagStripRegex = new(@"<[^>]+>", RegexOptions.Compiled);

    // Headings like <h5>Download … {Hindi-English} 720p [1.3GB]</h5> carry the quality tier
    // for the download anchors that follow them on multi-quality movie pages.
    private static readonly Regex HeadingRegex = new(
        @"<h[1-6]\b[^>]*>(?<text>.*?)</h[1-6]>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private static readonly HashSet<string> DirectMediaExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".mp4", ".avi", ".mov", ".m4v", ".webm", ".ts", ".wmv", ".zip", ".rar", ".7z"
    };

    private static readonly string[] IgnoredNavigationSegments =
    [
        "/category/",
        "/categories/",
        "/tag/",
        "/tags/",
        "/genre/",
        "/genres/",
        "/author/",
        "/page/",
        "/movies-by-size/",
        "/year/",
        "/how-to-download",
        "/contact",
        "/dmca",
        "/privacy",
        "/login",
        "/signin",
        "/register",
        "/signup"
    ];

    private static readonly string[] BrowserRequiredMarkers =
    [
        "just a moment...",
        "cf-browser-verification",
        "challenge-platform",
        "verify you are human",
        "g-recaptcha",
        "hcaptcha",
        "enable javascript and cookies to continue",
        "sign in to continue",
        "login required",
        "authentication required"
    ];

    // Download-host endpoints that must be preferred over generic "Download Links"
    // redirect pages (e.g. linksmod.top) when both appear on a movie page.
    private static readonly string[] PreferredDownloadHostMarkers =
    [
        "dl.fastdlserver.site",
        "fastdlserver",
        "gdflix",
        "drive.google.com",
        "drive.usercontent.google.com"
    ];

    // Hosts that serve gdflix "cloud" pages; their relative /cloud/<id>/<token> links
    // must be resolved against the current gdflix file host (e.g. new10.gdflix.io).
    private static readonly string[] CloudPageHostMarkers = ["gdflix"];

    private static readonly string CloudHostBase = "https://new10.gdflix.io";

    public static bool IsPreferredDownloadHost(Uri uri) =>
        PreferredDownloadHostMarkers.Any(m => uri.Host.Contains(m, StringComparison.OrdinalIgnoreCase));

    public static bool IsCloudPageHost(Uri uri) =>
        CloudPageHostMarkers.Any(m => uri.Host.Contains(m, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Rewrites relative gdflix cloud links (e.g. /cloud/1795759148/iif5zjN7hPXqDaXdAxaq)
    /// onto the canonical gdflix file host (new10.gdflix.io) instead of the host that
    /// happened to serve the intermediate page.
    /// </summary>
    public static Uri? TryRewriteCloudLink(Uri uri)
    {
        if (uri is null || !uri.IsAbsoluteUri)
        {
            return null;
        }

        var path = uri.AbsolutePath;
        if (!path.StartsWith("/cloud/", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (!Uri.TryCreate($"{CloudHostBase}{path}{uri.Query}", UriKind.Absolute, out var rewritten) ||
            !IsSafeHttpUri(rewritten))
        {
            return null;
        }

        return rewritten;
    }

    public static bool IsSafeHttpUri(Uri? uri) =>
        uri is not null &&
        uri.IsAbsoluteUri &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    public static bool IsDirectFileUri(Uri uri)
    {
        if (!IsSafeHttpUri(uri)) return false;
        var ext = Path.GetExtension(uri.AbsolutePath);
        return !string.IsNullOrWhiteSpace(ext) && DirectMediaExtensions.Contains(ext);
    }

    public static bool IsNavigationTaxonomyUri(Uri uri)
    {
        var path = uri.AbsolutePath;
        if (string.IsNullOrWhiteSpace(path) || path == "/")
        {
            // A root path WITH a query (e.g. dl.fastdlserver.site/?id=...&type=file) is a
            // parameterized download endpoint, not navigation. Only a bare root is taxonomy.
            return string.IsNullOrWhiteSpace(uri.Query);
        }

        foreach (var seg in IgnoredNavigationSegments)
        {
            if (path.Contains(seg, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static bool RequiresBrowserInteraction(int statusCode, string? body)
    {
        if (statusCode is 401 or 403 or 429 or 503)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(body))
        {
            return false;
        }

        foreach (var marker in BrowserRequiredMarkers)
        {
            if (body.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static bool TryParseJsonOptions(string body, Uri baseUri, out List<DownloadOption> options)
    {
        options = [];
        var trimmed = (body ?? string.Empty).TrimStart();
        if (!trimmed.StartsWith('{') && !trimmed.StartsWith('['))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            var root = doc.RootElement;

            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("options", out var optsEl) &&
                optsEl.ValueKind == JsonValueKind.Array)
            {
                ReadOptionArray(optsEl, baseUri, options);
            }
            else if (root.ValueKind == JsonValueKind.Array)
            {
                ReadOptionArray(root, baseUri, options);
            }
            else if (root.ValueKind == JsonValueKind.Object &&
                     TryGetString(root, "downloadUrl", out var singleUrl) &&
                     Uri.TryCreate(baseUri, singleUrl, out var singleUri) &&
                     IsSafeHttpUri(singleUri))
            {
                TryGetString(root, "quality", out var q);
                TryGetString(root, "fileName", out var fn);
                options.Add(new DownloadOption(q ?? string.Empty, singleUri, fn));
            }

            return options.Count > 0;
        }
        catch
        {
            return false;
        }
    }

    public static List<DownloadOption> ExtractHtmlAnchorOptions(string html, Uri baseUri, bool directFilesOnly)
    {
        var list = new List<DownloadOption>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Strip site-wide navigation chrome so menu links (e.g. "Category: 1080p") are never mistaken for downloads.
        var contentHtml = ChromeStripRegex.Replace(html ?? string.Empty, " ");

        // Last quality tier seen in a heading before each anchor, so unlabeled
        // host links inherit their section's quality (e.g. 720p Google Drive link).
        var lastHeadingQuality = string.Empty;
        var lastIndex = 0;

        foreach (Match match in AnchorRegex.Matches(contentHtml))
        {
            if (match.Index > lastIndex)
            {
                var preceding = contentHtml.Substring(lastIndex, match.Index - lastIndex);
                var lastHeading = LastHeadingText(preceding);
                if (!string.IsNullOrWhiteSpace(lastHeading))
                {
                    var headingQuality = QualityPreferenceMatcher.ParseQualityRank(lastHeading);
                    lastHeadingQuality = headingQuality > 0 ? $"{headingQuality}p" : string.Empty;
                }
            }

            lastIndex = Math.Max(lastIndex, match.Index + match.Length);
            var rawHref = WebUtility.HtmlDecode(match.Groups["href"].Value).Trim();
            if (string.IsNullOrWhiteSpace(rawHref) ||
                rawHref.StartsWith('#') ||
                rawHref.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!Uri.TryCreate(baseUri, rawHref, out var resolved) || !IsSafeHttpUri(resolved))
            {
                continue;
            }

            if (!directFilesOnly && IsNavigationTaxonomyUri(resolved))
            {
                continue;
            }

            if (directFilesOnly && !IsDirectFileUri(resolved))
            {
                continue;
            }

            if (!seen.Add(resolved.AbsoluteUri))
            {
                continue;
            }

            var text = WebUtility.HtmlDecode(TagStripRegex.Replace(match.Groups["text"].Value, " ")).Trim();
            var combinedForQuality = $"{lastHeadingQuality} {text} {resolved.AbsolutePath}";
            var quality = QualityPreferenceMatcher.NormalizeQualityLabel(combinedForQuality);
            quality = quality.Length > 0 && QualityPreferenceMatcher.ParseQualityRank(quality) > 0
                ? quality
                : lastHeadingQuality;
            var fileName = IsDirectFileUri(resolved)
                ? Uri.UnescapeDataString(Path.GetFileName(resolved.AbsolutePath))
                : null;

            list.Add(new DownloadOption(quality, resolved, fileName, text));
        }

        return list;
    }

    public static async Task<(int StatusCode, Uri FinalUri, string ContentType, string Body, string? ContentDispositionFileName)> FetchAsync(
        HttpClient http,
        Uri uri,
        CancellationToken ct)
    {
        const int maxRedirects = 5;
        var currentUri = uri;
        var redirects = 0;

        while (true)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, currentUri);
            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            var statusCode = (int)resp.StatusCode;
            var finalUri = resp.RequestMessage?.RequestUri ?? currentUri;

            // Follow redirects manually so the workflow also works with clients whose
            // handler has AllowAutoRedirect=false. Handlers that auto-redirect never
            // surface a 3xx here, so this loop is a no-op for them.
            if (statusCode is 301 or 302 or 303 or 307 or 308 &&
                redirects < maxRedirects &&
                resp.Headers.Location is { } location &&
                TryResolveRedirect(finalUri, location, out var nextUri))
            {
                redirects++;
                currentUri = nextUri;
                continue;
            }

            var contentType = resp.Content.Headers.ContentType?.MediaType ?? string.Empty;
            var cdFile = resp.Content.Headers.ContentDisposition?.FileNameStar
                         ?? resp.Content.Headers.ContentDisposition?.FileName?.Trim('"');

            // If the endpoint itself is already a direct binary/video download, do not buffer the media body.
            if (statusCode is >= 200 and < 300 &&
                (contentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase) ||
                 contentType.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase) ||
                 !string.IsNullOrWhiteSpace(cdFile)))
            {
                return (statusCode, finalUri, contentType, string.Empty, cdFile);
            }

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream);
            var buffer = new char[8192];
            var sb = new System.Text.StringBuilder();
            const int maxChars = 512_000;
            int read;
            while (sb.Length < maxChars &&
                   (read = await reader.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, maxChars - sb.Length)), ct)) > 0)
            {
                sb.Append(buffer, 0, read);
            }

            return (statusCode, finalUri, contentType, sb.ToString(), cdFile);
        }
    }

    private static bool TryResolveRedirect(Uri baseUri, Uri location, out Uri resolved)
    {
        if (location.IsAbsoluteUri)
        {
            resolved = location;
            return IsSafeHttpUri(resolved);
        }

        if (Uri.TryCreate(baseUri, location, out var combined) && IsSafeHttpUri(combined))
        {
            resolved = combined;
            return true;
        }

        resolved = baseUri;
        return false;
    }

    private static string LastHeadingText(string htmlFragment)
    {
        var last = string.Empty;
        foreach (Match m in HeadingRegex.Matches(htmlFragment))
        {
            var text = WebUtility.HtmlDecode(TagStripRegex.Replace(m.Groups["text"].Value, " ")).Trim();
            if (text.Length > 0)
            {
                last = text;
            }
        }

        return last;
    }

    private static void ReadOptionArray(JsonElement array, Uri baseUri, List<DownloadOption> target)
    {
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            if (!TryGetString(item, "url", out var urlText) && !TryGetString(item, "downloadUrl", out urlText))
            {
                continue;
            }

            if (!Uri.TryCreate(baseUri, urlText, out var uri) || !IsSafeHttpUri(uri))
            {
                continue;
            }

            TryGetString(item, "quality", out var q);
            TryGetString(item, "fileName", out var fn);
            TryGetString(item, "host", out var host);
            target.Add(new DownloadOption(q ?? string.Empty, uri, fn, host));
        }
    }

    private static bool TryGetString(JsonElement obj, string name, out string? value)
    {
        foreach (var prop in obj.EnumerateObject())
        {
            if (prop.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && prop.Value.ValueKind == JsonValueKind.String)
            {
                value = prop.Value.GetString();
                return !string.IsNullOrWhiteSpace(value);
            }
        }

        value = null;
        return false;
    }
}

/// <summary>
/// Workflow 1 (Direct Link Workflow): resolves an authorized page or JSON catalog
/// that exposes direct downloadable file URLs organized by quality.
/// </summary>
public sealed class DirectLinkDownloadWorkflow : IDownloadWorkflowProvider
{
    private readonly HttpClient _http;

    public DirectLinkDownloadWorkflow(HttpClient http)
    {
        _http = http;
    }

    public string ProviderId => DownloadWorkflowIds.DirectLink;
    public string DisplayName => "Direct Link Workflow";
    public string Description => "Resolves authorized direct file links or JSON download catalogs by preferred quality.";

    public async Task<DownloadResolveOutcome> ResolveAsync(
        DownloadRequest request,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        if (!Uri.TryCreate(request.SourcePageUrl, UriKind.Absolute, out var startUri) ||
            !WorkflowHttpHelpers.IsSafeHttpUri(startUri))
        {
            return DownloadResolveOutcome.Error("Invalid source URL.");
        }

        progress?.Report($"Inspecting {request.SourceName}…");
        var (status, finalUri, contentType, body, cdFile) = await WorkflowHttpHelpers.FetchAsync(_http, startUri, ct);

        if (WorkflowHttpHelpers.RequiresBrowserInteraction(status, body))
        {
            return DownloadResolveOutcome.BrowserRequired("Requires browser interaction — use Open.", startUri);
        }

        if (status < 200 || status >= 400)
        {
            return DownloadResolveOutcome.Error($"Source returned HTTP {status}.");
        }

        if (contentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase) ||
            contentType.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrWhiteSpace(cdFile) ||
            WorkflowHttpHelpers.IsDirectFileUri(finalUri))
        {
            var resolved = new ResolvedDownload
            {
                SourceId = request.SourceId,
                MovieTitle = request.MovieTitle,
                Quality = QualityPreferenceMatcher.NormalizeQualityLabel(cdFile ?? finalUri.AbsolutePath),
                DownloadUrl = finalUri,
                FileName = cdFile
            };
            return DownloadResolveOutcome.Success(resolved);
        }

        List<DownloadOption> options;
        if (!WorkflowHttpHelpers.TryParseJsonOptions(body, finalUri, out options))
        {
            options = WorkflowHttpHelpers.ExtractHtmlAnchorOptions(body, finalUri, directFilesOnly: true);
        }

        var best = QualityPreferenceMatcher.SelectBestOption(options, request.PreferredQuality);
        if (best is null)
        {
            return DownloadResolveOutcome.BrowserRequired(
                "No direct file link found on page — requires browser interaction (use Open).",
                startUri);
        }

        var quality = string.IsNullOrWhiteSpace(best.QualityLabel)
            ? request.PreferredQuality
            : QualityPreferenceMatcher.NormalizeQualityLabel(best.QualityLabel);

        return DownloadResolveOutcome.Success(new ResolvedDownload
        {
            SourceId = request.SourceId,
            MovieTitle = request.MovieTitle,
            Quality = quality,
            DownloadUrl = best.TargetUrl,
            FileName = best.FileName
        });
    }
}

/// <summary>
/// Workflow 2 (Multi-Step Host Workflow): follows up to 5 validated http/https hops
/// across authorized quality/host selection pages to reach a direct file URL,
/// cleanly bailing out to NeedsBrowser on any login, challenge, or interactive page.
/// Handles the real-world chain: search page → movie page → dl.fastdlserver.site
/// redirect → gdflix /file/ page → /cloud/ page (rebuilt on new10.gdflix.io) → direct file.
/// </summary>
public sealed class MultiStepLinkDownloadWorkflow : IDownloadWorkflowProvider
{
    private const int MaxHops = 5;
    private readonly HttpClient _http;

    public MultiStepLinkDownloadWorkflow(HttpClient http)
    {
        _http = http;
    }

    public string ProviderId => DownloadWorkflowIds.MultiStepLink;
    public string DisplayName => "Multi-Step Host Workflow";
    public string Description => "Follows up to 3 validated authorized quality/host pages and hands off the final file URL to IDM.";

    public async Task<DownloadResolveOutcome> ResolveAsync(
        DownloadRequest request,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        if (!Uri.TryCreate(request.SourcePageUrl, UriKind.Absolute, out var startUri) ||
            !WorkflowHttpHelpers.IsSafeHttpUri(startUri))
        {
            return DownloadResolveOutcome.Error("Invalid source URL.");
        }

        var currentUri = startUri;
        var selectedQuality = request.PreferredQuality;
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var hop = 1; hop <= MaxHops; hop++)
        {
            ct.ThrowIfCancellationRequested();
            if (!visited.Add(currentUri.AbsoluteUri))
            {
                return DownloadResolveOutcome.BrowserRequired("Requires browser interaction — use Open.", startUri);
            }

            progress?.Report($"Resolving step {hop}/{MaxHops} on {currentUri.Host}…");
            var (status, finalUri, contentType, body, cdFile) = await WorkflowHttpHelpers.FetchAsync(_http, currentUri, ct);

            if (WorkflowHttpHelpers.RequiresBrowserInteraction(status, body))
            {
                return DownloadResolveOutcome.BrowserRequired("Requires browser interaction — use Open.", startUri);
            }

            if (status < 200 || status >= 400)
            {
                return DownloadResolveOutcome.Error($"Step {hop} returned HTTP {status}.");
            }

            if (contentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase) ||
                contentType.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase) ||
                !string.IsNullOrWhiteSpace(cdFile) ||
                WorkflowHttpHelpers.IsDirectFileUri(finalUri))
            {
                return DownloadResolveOutcome.Success(new ResolvedDownload
                {
                    SourceId = request.SourceId,
                    MovieTitle = request.MovieTitle,
                    Quality = QualityPreferenceMatcher.NormalizeQualityLabel(selectedQuality),
                    DownloadUrl = finalUri,
                    FileName = cdFile
                });
            }

            // JSON catalog support at any hop
            if (WorkflowHttpHelpers.TryParseJsonOptions(body, finalUri, out var jsonOpts))
            {
                var bestJson = QualityPreferenceMatcher.SelectBestOption(jsonOpts, selectedQuality);
                if (bestJson is not null)
                {
                    if (!string.IsNullOrWhiteSpace(bestJson.QualityLabel))
                    {
                        selectedQuality = QualityPreferenceMatcher.NormalizeQualityLabel(bestJson.QualityLabel);
                    }

                    if (WorkflowHttpHelpers.IsDirectFileUri(bestJson.TargetUrl))
                    {
                        return DownloadResolveOutcome.Success(new ResolvedDownload
                        {
                            SourceId = request.SourceId,
                            MovieTitle = request.MovieTitle,
                            Quality = selectedQuality,
                            DownloadUrl = bestJson.TargetUrl,
                            FileName = bestJson.FileName
                        });
                    }

                    currentUri = bestJson.TargetUrl;
                    continue;
                }
            }

            // Check direct media anchors first
            var directAnchors = WorkflowHttpHelpers.ExtractHtmlAnchorOptions(body, finalUri, directFilesOnly: true);
            if (directAnchors.Count > 0)
            {
                var bestDirect = QualityPreferenceMatcher.SelectBestOption(directAnchors, selectedQuality)!;
                if (!string.IsNullOrWhiteSpace(bestDirect.QualityLabel))
                {
                    selectedQuality = QualityPreferenceMatcher.NormalizeQualityLabel(bestDirect.QualityLabel);
                }

                return DownloadResolveOutcome.Success(new ResolvedDownload
                {
                    SourceId = request.SourceId,
                    MovieTitle = request.MovieTitle,
                    Quality = selectedQuality,
                    DownloadUrl = bestDirect.TargetUrl,
                    FileName = bestDirect.FileName
                });
            }

            // Otherwise advance to the best unvisited intermediate link (if hops remain)
            if (hop < MaxHops)
            {
                var allAnchors = WorkflowHttpHelpers.ExtractHtmlAnchorOptions(body, finalUri, directFilesOnly: false)
                    .Where(a => !visited.Contains(a.TargetUrl.AbsoluteUri))
                    .Select(a =>
                    {
                        // On gdflix pages, relative /cloud/<id>/<token> links must be pinned
                        // to new10.gdflix.io regardless of which mirror served this page.
                        if (WorkflowHttpHelpers.IsCloudPageHost(finalUri) &&
                            WorkflowHttpHelpers.TryRewriteCloudLink(a.TargetUrl) is { } rewritten &&
                            !visited.Contains(rewritten.AbsoluteUri))
                        {
                            return new DownloadOption(a.QualityLabel, rewritten, a.FileName, a.HostLabel);
                        }

                        return a;
                    })
                    .ToList();

                // On hop 1 from a search query page (?s=...), prefer a result link matching the movie title
                if (hop == 1 && !string.IsNullOrWhiteSpace(finalUri.Query))
                {
                    var titleMatched = allAnchors
                        .Select(a => new
                        {
                            Option = a,
                            Score = TitleSimilarity.Score(request.MovieTitle, $"{a.HostLabel} {a.TargetUrl.AbsolutePath}")
                        })
                        .Where(x => x.Score >= 0.35)
                        .OrderByDescending(x => x.Score)
                        .Select(x => x.Option)
                        .FirstOrDefault();

                    if (titleMatched is not null)
                    {
                        currentUri = titleMatched.TargetUrl;
                        continue;
                    }
                }

                DownloadOption? next = null;

                // 1. On gdflix file pages, follow the /cloud/ download entry (rebuilt on new10.gdflix.io).
                if (WorkflowHttpHelpers.IsCloudPageHost(finalUri))
                {
                    next = allAnchors.FirstOrDefault(a =>
                        a.TargetUrl.AbsolutePath.StartsWith("/cloud/", StringComparison.OrdinalIgnoreCase));
                }

                // 2. Prefer known download-host endpoints (dl.fastdlserver.site, gdflix, Google Drive)
                //    over generic "Download Links" redirect pages such as linksmod.top.
                if (next is null)
                {
                    var hostAnchors = allAnchors
                        .Where(a => WorkflowHttpHelpers.IsPreferredDownloadHost(a.TargetUrl))
                        .ToList();

                    if (hostAnchors.Count > 0)
                    {
                        next = QualityPreferenceMatcher.SelectBestOption(hostAnchors, selectedQuality);
                    }
                }

                // 3. Fall back to the best quality-labeled or "Download" link.
                if (next is null)
                {
                    next = QualityPreferenceMatcher.SelectBestOption(
                        allAnchors
                            .Where(a => QualityPreferenceMatcher.ParseQualityRank(a.QualityLabel) > 0 ||
                                        (a.HostLabel?.Contains("download", StringComparison.OrdinalIgnoreCase) == true))
                            .ToList(),
                        selectedQuality);
                }

                if (next is not null)
                {
                    if (!string.IsNullOrWhiteSpace(next.QualityLabel))
                    {
                        selectedQuality = QualityPreferenceMatcher.NormalizeQualityLabel(next.QualityLabel);
                    }

                    currentUri = next.TargetUrl;
                    continue;
                }
            }

            return DownloadResolveOutcome.BrowserRequired(
                "Requires browser interaction — use Open.",
                startUri);
        }

        return DownloadResolveOutcome.BrowserRequired("Requires browser interaction — use Open.", startUri);
    }
}
