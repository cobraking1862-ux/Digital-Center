using System.IO;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using DigitalCenter.Core.Configuration;
using DigitalCenter.Core.Logging;
using Microsoft.Playwright;

namespace DigitalCenter.Core.Services;

/// <summary>
/// BigOne browser workflow for BollyFlix. Uses a real, visible browser session so
/// site JavaScript, redirects, cookies and browser-side verification are handled
/// by the browser rather than by a headless HTTP crawler.
/// </summary>
public sealed class BigOneBrowserDownloadWorkflow : IDownloadWorkflowProvider, IDisposable
{
    private const string BollyFlixHost = "new1.bollyflix.tours";
    private const string FastDlHost = "dl.fastdlserver.site";
    private const string GdFlixHost = "new10.gdflix.io";
    private const int NavigationTimeoutMs = 45_000;
    private const int ElementTimeoutMs = 20_000;
    private const int HumanVerificationTimeoutMs = 180_000;

    private static readonly HashSet<string> AllowedPageHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        BollyFlixHost,
        FastDlHost,
        GdFlixHost
    };

    private static readonly HashSet<string> AllowedFinalHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "cloud-dl.px6xkyh05ea.workers.dev",
        "cloud-dl.bj1zcnlye7n.workers.dev",
        "quick.cloudpaglu.site"
    };

    private static readonly string[] HumanVerificationMarkers =
    [
        "just a moment",
        "verify you are human",
        "checking your browser",
        "complete the security check",
        "performing security verification",
        "enable javascript and cookies to continue",
        "please wait while we verify"
    ];

    private readonly AppPaths _paths;
    private readonly IAppLogger? _logger;
    private readonly SemaphoreSlim _runGate = new(1, 1);
    private readonly HashSet<IPage> _instrumentedPages = [];
    private IPlaywright? _playwright;
    private IBrowserContext? _context;
    private IPage? _page;
    private bool _disposed;

    public BigOneBrowserDownloadWorkflow(AppPaths paths, IAppLogger? logger = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _logger = logger;
    }

    public string ProviderId => DownloadWorkflowIds.BigOneBrowser;
    public string DisplayName => "BigOne — Visible Browser (BollyFlix)";
    public string Description =>
        "Opens a visible Edge/Chromium browser and automates the BollyFlix → FastDLServer → GDFlix route.";

    public async Task<DownloadResolveOutcome> ResolveAsync(
        DownloadRequest request,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        if (!Uri.TryCreate(request.SourcePageUrl, UriKind.Absolute, out var sourceUri) ||
            !IsBollyFlixUri(sourceUri))
        {
            return DownloadResolveOutcome.BrowserRequired(
                "BigOne Browser accepts only HTTPS pages on new1.bollyflix.tours.",
                Uri.TryCreate(request.SourcePageUrl, UriKind.Absolute, out var fallback) ? fallback : null);
        }

        await _runGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var page = await GetOrCreateVisiblePageAsync(progress).ConfigureAwait(false);
            var titlePageUri = sourceUri;

            if (IsSearchListingUri(sourceUri))
            {
                Report(progress, "Opening the BollyFlix IMDb search result in the visible browser.");
                await NavigateAsync(page, sourceUri, "BollyFlix search results", progress, ct).ConfigureAwait(false);
                if (!await WaitForStageElementAsync(
                        page,
                        ".post-cards article.latestPost.excerpt",
                        "BollyFlix search result card",
                        progress,
                        ct).ConfigureAwait(false))
                {
                    return BrowserFallback("The BollyFlix search result did not render a recognizable movie card.", titlePageUri, progress);
                }

                var listingHtml = await page.ContentAsync().ConfigureAwait(false);
                titlePageUri = ParseTitlePageUri(listingHtml, sourceUri) ??
                               throw new InvalidOperationException("No supported movie post link was found in the BollyFlix search results.");

                Report(progress, $"Found the movie post: {SafeUrl(titlePageUri)}");
            }
            else if (!IsBollyFlixTitlePageUri(sourceUri))
            {
                return BrowserFallback("The source URL is not a supported BollyFlix search or movie page.", sourceUri, progress);
            }

            Report(progress, "Opening the movie post and reading its quality sections.");
            await NavigateAsync(page, titlePageUri, "BollyFlix movie post", progress, ct).ConfigureAwait(false);
            if (!await WaitForStageElementAsync(
                    page,
                    ".post-single-content .thecontent h5",
                    "Movie quality headings",
                    progress,
                    ct).ConfigureAwait(false))
            {
                return BrowserFallback("The movie post did not render its quality sections.", titlePageUri, progress);
            }

            var titleHtml = await page.ContentAsync().ConfigureAwait(false);
            var options = ParseQualityOptions(titleHtml, titlePageUri);
            var selected = QualityPreferenceMatcher.SelectBestOption(options, request.PreferredQuality);
            if (selected is null)
            {
                return BrowserFallback(
                    "No Google Drive-labelled FastDLServer quality option was found on the movie post.",
                    titlePageUri,
                    progress);
            }

            Report(progress, $"Selected {selected.QualityLabel} (preferred: {request.PreferredQuality}).");
            Report(progress, $"Opening the FastDLServer route: {SafeUrl(selected.TargetUrl)}");
            await NavigateAsync(page, selected.TargetUrl, "FastDLServer file-host redirect", progress, ct).ConfigureAwait(false);

            if (!TryGetCurrentUri(page, out var fileHostUri) || !IsFileHostUri(fileHostUri))
            {
                return BrowserFallback(
                    $"FastDLServer redirected to an unsupported destination: {SafeUrl(page.Url)}",
                    titlePageUri,
                    progress);
            }

            if (!await WaitForStageElementAsync(
                    page,
                    "a[href*='/cloud/']",
                    "GDFlix FAST CLOUD link",
                    progress,
                    ct).ConfigureAwait(false))
            {
                return BrowserFallback(
                    "The FastDLServer destination did not render the expected FAST CLOUD link.",
                    titlePageUri,
                    progress);
            }

            var fileHostHtml = await page.ContentAsync().ConfigureAwait(false);
            var cloudUri = ParseFastCloudUri(fileHostHtml, fileHostUri);
            if (cloudUri is null)
            {
                return BrowserFallback(
                    "The file-host page did not contain a safe root-relative FAST CLOUD route.",
                    titlePageUri,
                    progress);
            }

            Report(progress, $"Opening the GDFlix cloud page: {SafeUrl(cloudUri)}");
            await NavigateAsync(page, cloudUri, "GDFlix cloud landing page", progress, ct).ConfigureAwait(false);

            if (!TryGetCurrentUri(page, out var cloudPageUri) || !IsGdFlixCloudPageUri(cloudPageUri))
            {
                return BrowserFallback(
                    $"The FAST CLOUD link ended on an unsupported page: {SafeUrl(page.Url)}",
                    titlePageUri,
                    progress);
            }

            if (!await WaitForStageElementAsync(
                    page,
                    "a:has-text('Cloud Resume Download'), a:has-text('Quick Download (Ready)')",
                    "Cloud Resume Download action",
                    progress,
                    ct).ConfigureAwait(false))
            {
                return BrowserFallback(
                    "The GDFlix cloud page did not expose a static Cloud Resume Download or Quick Download (Ready) link.",
                    titlePageUri,
                    progress);
            }

            var cloudHtml = await page.ContentAsync().ConfigureAwait(false);
            var finalUri = ParseCloudResumeUri(cloudHtml, cloudPageUri);
            if (finalUri is null || !IsApprovedFinalDestination(finalUri))
            {
                return BrowserFallback(
                    "The GDFlix cloud page did not expose a final download URL on an approved host.",
                    titlePageUri,
                    progress);
            }

            var fileName = GetFileName(finalUri);
            Report(progress, $"Found the {selected.QualityLabel} Cloud Resume Download URL.");
            Report(progress, $"Handing the verified-host URL to IDM: {SafeUrl(finalUri)}");

            return DownloadResolveOutcome.Success(
                new ResolvedDownload
                {
                    SourceId = request.SourceId,
                    MovieTitle = request.MovieTitle,
                    Quality = selected.QualityLabel,
                    DownloadUrl = finalUri,
                    FileName = fileName
                },
                $"Resolved {selected.QualityLabel} through the visible BigOne browser workflow.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Report(progress, "Browser workflow cancelled.");
            throw;
        }
        catch (PlaywrightException ex)
        {
            _logger?.Warn($"BigOne visible browser stopped: {ex.GetType().Name}.");
            return BrowserFallback(
                $"Visible browser automation stopped ({ex.GetType().Name}). The browser window has been left open for inspection.",
                sourceUri,
                progress);
        }
        catch (TimeoutException)
        {
            return BrowserFallback(
                "The browser timed out waiting for the expected page element. The visible browser has been left open for inspection.",
                sourceUri,
                progress);
        }
        catch (InvalidOperationException ex)
        {
            _logger?.Warn($"BigOne visible browser could not resolve the route: {ex.Message}");
            return BrowserFallback(ex.Message, sourceUri, progress);
        }
        catch (Exception ex)
        {
            _logger?.Warn($"BigOne visible browser failed: {ex.GetType().Name}.");
            return BrowserFallback(
                $"Browser workflow failed ({ex.GetType().Name}). Inspect the visible browser and application log.",
                sourceUri,
                progress);
        }
        finally
        {
            _runGate.Release();
        }
    }

    /// <summary>Finds the movie permalink from the supplied BollyFlix search-result DOM.</summary>
    public static Uri? ParseTitlePageUri(string html, Uri listingUri)
    {
        if (!IsBollyFlixUri(listingUri))
        {
            return null;
        }

        var document = new HtmlParser().ParseDocument(html ?? string.Empty);
        var article = document.QuerySelector(".post-cards article.latestPost.excerpt");
        if (article is null)
        {
            return null;
        }

        var anchor = article.QuerySelector("h2.title.front-view-title a[href]") ??
                     article.QuerySelector("a.post-image[href]");
        return anchor is null ? null : ResolveBollyFlixTitleUri(anchor.GetAttribute("href"), listingUri);
    }

    /// <summary>
    /// Extracts one Google Drive-labelled FastDLServer link from each quality heading's
    /// own following content block. Linksmod anchors are deliberately ignored.
    /// </summary>
    public static IReadOnlyList<DownloadOption> ParseQualityOptions(string html, Uri titlePageUri)
    {
        var options = new List<DownloadOption>();
        if (!IsBollyFlixUri(titlePageUri))
        {
            return options;
        }

        var document = new HtmlParser().ParseDocument(html ?? string.Empty);
        var headings = document.QuerySelectorAll(".post-single-content .thecontent h5");

        foreach (var heading in headings)
        {
            var rank = QualityPreferenceMatcher.ParseQualityRank(heading.TextContent);
            if (rank is not (480 or 720 or 1080 or 2160))
            {
                continue;
            }

            var quality = $"{rank}p";
            for (var sibling = heading.NextElementSibling;
                 sibling is not null && !sibling.LocalName.Equals("h5", StringComparison.OrdinalIgnoreCase);
                 sibling = sibling.NextElementSibling)
            {
                foreach (var anchor in sibling.QuerySelectorAll("a.dl[href]"))
                {
                    var label = NormalizeText(anchor.TextContent);
                    if (!label.Contains("Google Drive", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var target = ResolveFastDlUri(anchor.GetAttribute("href"), titlePageUri);
                    if (target is not null)
                    {
                        options.Add(new DownloadOption(quality, target, null, "Google Drive"));
                    }
                }
            }
        }

        return options
            .GroupBy(option => option.TargetUrl.AbsoluteUri, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
    }

    /// <summary>Extracts only GDFlix's root-relative /cloud/... action from the file-host page.</summary>
    public static Uri? ParseFastCloudUri(string html, Uri fileHostPageUri)
    {
        if (!IsFileHostUri(fileHostPageUri))
        {
            return null;
        }

        var document = new HtmlParser().ParseDocument(html ?? string.Empty);
        foreach (var anchor in document.QuerySelectorAll("a[href]"))
        {
            if (!NormalizeText(anchor.TextContent).Contains("FAST CLOUD", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var rawHref = anchor.GetAttribute("href");
            if (string.IsNullOrWhiteSpace(rawHref) ||
                rawHref.StartsWith("//", StringComparison.Ordinal) ||
                !rawHref.StartsWith("/cloud/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (Uri.TryCreate(fileHostPageUri, rawHref, out var cloudUri) && IsGdFlixCloudPageUri(cloudUri))
            {
                return cloudUri;
            }
        }

        return null;
    }

    /// <summary>
    /// Reads the static Cloud Resume Download href (preferred), or the legacy
    /// Quick Download (Ready) href when that static anchor is present.
    /// </summary>
    public static Uri? ParseCloudResumeUri(string html, Uri cloudPageUri)
    {
        if (!IsGdFlixCloudPageUri(cloudPageUri))
        {
            return null;
        }

        var document = new HtmlParser().ParseDocument(html ?? string.Empty);
        var anchors = document.QuerySelectorAll("a[href]");
        var selected = anchors.FirstOrDefault(anchor =>
            NormalizeText(anchor.TextContent).Contains("Cloud Resume Download", StringComparison.OrdinalIgnoreCase));

        selected ??= anchors.FirstOrDefault(anchor =>
            NormalizeText(anchor.TextContent).Contains("Quick Download (Ready)", StringComparison.OrdinalIgnoreCase));

        var rawHref = selected?.GetAttribute("href");
        if (string.IsNullOrWhiteSpace(rawHref) ||
            !Uri.TryCreate(cloudPageUri, rawHref, out var finalUri) ||
            !IsApprovedFinalDestination(finalUri))
        {
            return null;
        }

        return finalUri;
    }

    /// <summary>Returns true only for the explicit final download hosts used by this BigOne route.</summary>
    public static bool IsApprovedFinalDestination(Uri uri) =>
        uri.IsAbsoluteUri &&
        uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
        uri.IsDefaultPort &&
        string.IsNullOrEmpty(uri.UserInfo) &&
        AllowedFinalHosts.Contains(uri.IdnHost);

    /// <summary>Challenge detection is based on visible page text, not IDs or script names.</summary>
    public static bool IsHumanVerificationText(string? visibleText) =>
        !string.IsNullOrWhiteSpace(visibleText) &&
        HumanVerificationMarkers.Any(marker =>
            visibleText.Contains(marker, StringComparison.OrdinalIgnoreCase));

    private async Task<IPage> GetOrCreateVisiblePageAsync(IProgress<string>? progress)
    {
        if (_page is { IsClosed: false })
        {
            return _page;
        }

        _playwright ??= await Playwright.CreateAsync().ConfigureAwait(false);
        var profileDirectory = Path.Combine(_paths.DataRoot, "BrowserProfiles", "BigOne");
        Directory.CreateDirectory(profileDirectory);

        try
        {
            _context ??= await _playwright.Chromium.LaunchPersistentContextAsync(
                profileDirectory,
                new BrowserTypeLaunchPersistentContextOptions
                {
                    Headless = false,
                    Channel = "msedge",
                    SlowMo = 120,
                    AcceptDownloads = true,
                    Args = ["--start-maximized"]
                }).ConfigureAwait(false);

            Report(progress, "Opened a visible Microsoft Edge window with a persistent BigOne browser profile.");
        }
        catch (PlaywrightException edgeException)
        {
            _logger?.Warn($"Microsoft Edge could not be launched by Playwright: {edgeException.GetType().Name}.");
            Report(progress, "Microsoft Edge automation is unavailable. Trying the Playwright Chromium browser.");

            try
            {
                _context ??= await _playwright.Chromium.LaunchPersistentContextAsync(
                    profileDirectory,
                    new BrowserTypeLaunchPersistentContextOptions
                    {
                        Headless = false,
                        SlowMo = 120,
                        AcceptDownloads = true,
                        Args = ["--start-maximized"]
                    }).ConfigureAwait(false);

                Report(progress, "Opened a visible Playwright Chromium window.");
            }
            catch (PlaywrightException chromiumException)
            {
                _playwright.Dispose();
                _playwright = null;
                throw new InvalidOperationException(
                    "No supported visible browser could be started. Install Microsoft Edge or run scripts/Install-PlaywrightBrowsers.ps1 to install the Playwright Chromium browser.",
                    chromiumException);
            }
        }

        _page = _context.Pages.FirstOrDefault(candidate => !candidate.IsClosed) ??
                await _context.NewPageAsync().ConfigureAwait(false);
        await InstrumentPageAsync(_page).ConfigureAwait(false);
        return _page;
    }

    private async Task InstrumentPageAsync(IPage page)
    {
        if (!_instrumentedPages.Add(page))
        {
            return;
        }

        page.Request += (_, request) =>
        {
            if (request.IsNavigationRequest)
            {
                _logger?.Info($"[BigOne Browser] REQUEST {request.Method} {SafeUrl(request.Url)}");
            }
        };

        page.Response += (_, response) =>
        {
            if (response.Request.IsNavigationRequest)
            {
                _logger?.Info($"[BigOne Browser] RESPONSE HTTP {response.Status} {SafeUrl(response.Url)}");
            }
        };

        page.RequestFailed += (_, request) =>
        {
            _logger?.Warn($"[BigOne Browser] REQUEST FAILED {SafeUrl(request.Url)} · {request.Failure ?? "unknown network error"}");
        };

        await page.RouteAsync("**/*", async route =>
        {
            var request = route.Request;
            if (IsMainFrameNavigation(request, page) &&
                Uri.TryCreate(request.Url, UriKind.Absolute, out var destination) &&
                !IsAllowedPageHost(destination.IdnHost))
            {
                _logger?.Warn($"[BigOne Browser] BLOCKED unexpected top-level navigation to {SafeUrl(request.Url)}");
                await route.AbortAsync().ConfigureAwait(false);
                return;
            }

            await route.ContinueAsync().ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    private async Task NavigateAsync(
        IPage page,
        Uri target,
        string stage,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!IsAllowedPageHost(target.IdnHost))
        {
            throw new InvalidOperationException($"The {stage} URL is outside the approved browser-navigation hosts.");
        }

        Report(progress, $"{stage}: navigating to {SafeUrl(target)}");
        var response = await page.GotoAsync(
            target.AbsoluteUri,
            new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = NavigationTimeoutMs
            }).ConfigureAwait(false);

        ct.ThrowIfCancellationRequested();
        if (response is not null)
        {
            Report(progress, $"{stage}: browser received HTTP {response.Status}.");
            ValidateRedirectChain(response, stage);
        }

        if (!Uri.TryCreate(page.Url, UriKind.Absolute, out var currentUri) ||
            !IsAllowedPageHost(currentUri.IdnHost))
        {
            throw new InvalidOperationException(
                $"The {stage} navigation ended at an unapproved host: {SafeUrl(page.Url)}.");
        }

        Report(progress, $"{stage}: current page is {SafeUrl(currentUri)}");
    }

    private async Task<bool> WaitForStageElementAsync(
        IPage page,
        string selector,
        string description,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        var locator = page.Locator(selector).First;
        if (await WaitForLocatorAsync(locator, ElementTimeoutMs, ct).ConfigureAwait(false))
        {
            Report(progress, $"Page ready: {description}.");
            return true;
        }

        if (!await IsHumanVerificationVisibleAsync(page).ConfigureAwait(false))
        {
            var pageTitle = await page.TitleAsync().ConfigureAwait(false);
            Report(progress, $"Expected page element was not found: {description}. Page title: {NormalizeText(pageTitle)}");
            return false;
        }

        Report(progress, "A human verification page is visible. Complete it in the open browser; DigitalCenter will continue when the expected page element appears.");
        var completed = await WaitForLocatorAsync(locator, HumanVerificationTimeoutMs, ct).ConfigureAwait(false);
        if (completed)
        {
            Report(progress, "Human verification completed; the expected page element is now available.");
        }
        else
        {
            Report(progress, "Timed out waiting for manual browser verification.");
        }

        return completed;
    }

    private static async Task<bool> WaitForLocatorAsync(ILocator locator, int timeoutMs, CancellationToken ct)
    {
        var waitTask = locator.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = timeoutMs
        });
        var cancellationTask = Task.Delay(Timeout.Infinite, ct);
        if (await Task.WhenAny(waitTask, cancellationTask).ConfigureAwait(false) != waitTask)
        {
            _ = waitTask.ContinueWith(
                task => _ = task.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            ct.ThrowIfCancellationRequested();
        }

        try
        {
            await waitTask.ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    private static void ValidateRedirectChain(IResponse response, string stage)
    {
        for (var request = response.Request; request is not null; request = request.RedirectedFrom)
        {
            if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var requestedUri) ||
                !IsAllowedPageHost(requestedUri.IdnHost))
            {
                throw new InvalidOperationException(
                    $"The {stage} redirect chain included an unapproved host: {SafeUrl(request.Url)}.");
            }
        }
    }

    private static bool IsMainFrameNavigation(IRequest request, IPage page)
    {
        if (!request.IsNavigationRequest)
        {
            return false;
        }

        try
        {
            return request.Frame == page.MainFrame;
        }
        catch
        {
            // Playwright documents that a navigation request can be emitted before
            // its frame exists. The final page URL is validated again after navigation.
            return false;
        }
    }

    private static async Task<bool> IsHumanVerificationVisibleAsync(IPage page)
    {
        try
        {
            var visibleText = await page.Locator("body").InnerTextAsync().ConfigureAwait(false);
            if (IsHumanVerificationText(visibleText))
            {
                return true;
            }

            return await page.Locator("#challenge-form, #cf-challenge-running, #cf-please-wait, .cf-browser-verification")
                .CountAsync()
                .ConfigureAwait(false) > 0;
        }
        catch
        {
            return false;
        }
    }

    private static Uri? ResolveBollyFlixTitleUri(string? rawHref, Uri listingUri)
    {
        if (string.IsNullOrWhiteSpace(rawHref) ||
            !Uri.TryCreate(listingUri, rawHref, out var titleUri) ||
            !IsBollyFlixTitlePageUri(titleUri))
        {
            return null;
        }

        return titleUri;
    }

    private static Uri? ResolveFastDlUri(string? rawHref, Uri baseUri)
    {
        if (string.IsNullOrWhiteSpace(rawHref) ||
            !Uri.TryCreate(baseUri, rawHref, out var route) ||
            !IsFastDlRoute(route))
        {
            return null;
        }

        return route;
    }

    private static bool IsBollyFlixUri(Uri uri) =>
        uri.IsAbsoluteUri &&
        uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
        uri.IsDefaultPort &&
        string.IsNullOrEmpty(uri.UserInfo) &&
        uri.IdnHost.Equals(BollyFlixHost, StringComparison.OrdinalIgnoreCase);

    private static bool IsBollyFlixTitlePageUri(Uri uri) =>
        IsBollyFlixUri(uri) &&
        !uri.AbsolutePath.Equals("/", StringComparison.Ordinal) &&
        !uri.AbsolutePath.Contains("/category/", StringComparison.OrdinalIgnoreCase) &&
        !uri.AbsolutePath.Contains("/tag/", StringComparison.OrdinalIgnoreCase);

    private static bool IsSearchListingUri(Uri uri) =>
        IsBollyFlixUri(uri) &&
        uri.AbsolutePath.Equals("/", StringComparison.Ordinal) &&
        uri.Query.Contains("s=", StringComparison.OrdinalIgnoreCase);

    private static bool IsAllowedPageHost(string host) => AllowedPageHosts.Contains(host);

    private static bool IsFileHostUri(Uri uri) =>
        uri.IsAbsoluteUri &&
        uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
        uri.IsDefaultPort &&
        string.IsNullOrEmpty(uri.UserInfo) &&
        (uri.IdnHost.Equals(FastDlHost, StringComparison.OrdinalIgnoreCase) ||
         uri.IdnHost.Equals(GdFlixHost, StringComparison.OrdinalIgnoreCase));

    private static bool IsGdFlixCloudPageUri(Uri uri) =>
        IsFileHostUri(uri) &&
        uri.IdnHost.Equals(GdFlixHost, StringComparison.OrdinalIgnoreCase) &&
        uri.AbsolutePath.StartsWith("/cloud/", StringComparison.OrdinalIgnoreCase);

    private static bool IsFastDlRoute(Uri uri) =>
        uri.IsAbsoluteUri &&
        uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
        uri.IsDefaultPort &&
        string.IsNullOrEmpty(uri.UserInfo) &&
        uri.IdnHost.Equals(FastDlHost, StringComparison.OrdinalIgnoreCase) &&
        uri.AbsolutePath.Equals("/", StringComparison.Ordinal) &&
        QueryContainsKey(uri.Query, "id") &&
        QueryHasValue(uri.Query, "type", "file");

    private static bool QueryContainsKey(string query, string key) =>
        query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .Any(parts => Uri.UnescapeDataString(parts[0]).Equals(key, StringComparison.OrdinalIgnoreCase));

    private static bool QueryHasValue(string query, string key, string value) =>
        query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .Any(parts => parts.Length == 2 &&
                          Uri.UnescapeDataString(parts[0]).Equals(key, StringComparison.OrdinalIgnoreCase) &&
                          Uri.UnescapeDataString(parts[1]).Equals(value, StringComparison.OrdinalIgnoreCase));

    private static bool TryGetCurrentUri(IPage page, out Uri uri)
    {
        if (Uri.TryCreate(page.Url, UriKind.Absolute, out var parsed))
        {
            uri = parsed;
            return true;
        }

        uri = null!;
        return false;
    }

    private static string GetFileName(Uri finalUri)
    {
        var path = Uri.UnescapeDataString(finalUri.AbsolutePath);
        var fileName = Path.GetFileName(path);
        return string.IsNullOrWhiteSpace(fileName) ? "download" : fileName;
    }

    private static string NormalizeText(string? text) =>
        string.Join(" ", (text ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string SafeUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return "<invalid-url>";
        }

        var safeSegments = uri.AbsolutePath.Split('/')
            .Select(segment => segment.Length > 18 ? "{redacted}" : segment);
        var path = string.Join("/", safeSegments);
        return $"{uri.Scheme}://{uri.IdnHost}{path}{(string.IsNullOrEmpty(uri.Query) ? string.Empty : "?{query redacted}")}";
    }

    private void Report(IProgress<string>? progress, string message)
    {
        _logger?.Info($"[BigOne Browser] {message}");
        progress?.Report($"BigOne Browser · {message}");
    }

    private static DownloadResolveOutcome BrowserFallback(
        string reason,
        Uri fallback,
        IProgress<string>? progress)
    {
        progress?.Report($"BigOne Browser · Browser hand-off: {reason}");
        return DownloadResolveOutcome.BrowserRequired(reason, fallback);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            // Closing the persistent context also interrupts any in-flight browser
            // operation so application shutdown does not wait on a manual challenge.
            _context?.CloseAsync().GetAwaiter().GetResult();
        }
        catch
        {
            // Application shutdown must not be blocked by an already-closed browser.
        }

        _context = null;
        _page = null;
        _playwright?.Dispose();
        _playwright = null;
    }
}
