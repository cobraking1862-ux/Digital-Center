using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using DigitalCenter.Core.Configuration;

namespace DigitalCenter.Core.Services;

public enum BigOneStage
{
    TitlePage,
    QualityRouteSelected,
    FastDlServer,
    FileHostPage,
    CloudLandingPage,
    FinalDestinationVerified,
    NeedsBrowser,
    Failed
}

/// <summary>Explicit, validated options for the BigOne cloud-origin hand-off.</summary>
public sealed record BigOneOptions(string CloudOrigin = "https://new10.gdflix.io/");

/// <summary>
/// Dedicated resolver for the designated BollyFlix page shape and its supported
/// FastDLServer → GDFlix FAST CLOUD route. This is not a general-purpose crawler.
/// </summary>
public sealed class BigOneDownloadWorkflow
{
    private static readonly Regex AnchorRegex = new(
        "<a\\b(?<attrs>[^>]*)>(?<body>.*?)</a>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex ArticleRegex = new(
        "<article\\b(?<attrs>[^>]*)>(?<body>.*?)</article>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex HeadingRegex = new(
        "<h5\\b[^>]*>(?<body>.*?)</h5>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex HrefRegex = new(
        "(?:^|\\s)href\\s*=\\s*['\\\"](?<url>[^'\\\"]+)['\\\"]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ClassRegex = new(
        "(?:^|\\s)class\\s*=\\s*['\\\"](?<value>[^'\\\"]*)['\\\"]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex TagRegex = new("<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);

    private static readonly HashSet<string> CloudHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "new10.gdflix.io"
    };

    private static readonly HashSet<string> FileHostHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "dl.fastdlserver.site",
        "new10.gdflix.io"
    };

    private static readonly HashSet<string> FinalDestinationHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "cloud-dl.bj1zcnlye7n.workers.dev",
        "quick.cloudpaglu.site"
    };

    private readonly HttpClient _http;
    private readonly Uri _cloudOrigin;

    public BigOneDownloadWorkflow(HttpClient http, BigOneOptions? options = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        options ??= new BigOneOptions();
        if (!Uri.TryCreate(options.CloudOrigin, UriKind.Absolute, out var origin) ||
            !IsSafeUri(origin) ||
            !origin.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !origin.AbsolutePath.Equals("/", StringComparison.Ordinal) ||
            !string.IsNullOrEmpty(origin.Query) || !string.IsNullOrEmpty(origin.Fragment) ||
            !CloudHosts.Contains(origin.IdnHost))
        {
            throw new ArgumentException("BigOne requires the approved HTTPS cloud origin.", nameof(options));
        }

        _cloudOrigin = origin;
    }

    public async Task<DownloadResolveOutcome> ResolveAsync(
        DownloadRequest request,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var sourceUri = TryGetAbsoluteUri(request.SourcePageUrl);
        if (sourceUri is null || !IsBollyFlixUri(sourceUri))
            return NeedsBrowser("BigOne accepts only the designated BollyFlix host.", sourceUri, progress);

        var tracker = new StageTracker(progress);
        var titlePageUri = sourceUri;
        tracker.ReportCurrent("Inspecting the BollyFlix source page");

        if (IsSearchListingUri(sourceUri))
        {
            var listing = await SendValidatedAsync(sourceUri, IsBollyFlixUri, ct).ConfigureAwait(false);
            if (!listing.IsSuccess || !IsHtml(listing.ContentType) || !IsBollyFlixListingPage(listing.Body) ||
                !TryExtractTitlePage(listing.Body, sourceUri, out titlePageUri))
            {
                return NeedsBrowser("The BollyFlix search result could not be safely recognized.", sourceUri, progress);
            }
        }
        else if (!IsBollyFlixTitlePageUri(sourceUri))
        {
            return NeedsBrowser("The source is neither a supported BollyFlix listing nor a title-page URL.", sourceUri, progress);
        }

        var titlePage = await SendValidatedAsync(titlePageUri, IsBollyFlixUri, ct).ConfigureAwait(false);
        if (!titlePage.IsSuccess || !IsHtml(titlePage.ContentType) || !IsBollyFlixTitlePage(titlePage.Body))
            return NeedsBrowser("The BollyFlix title page could not be recognized.", titlePageUri, progress);

        var options = ParseQualityOptions(titlePage.Body, titlePageUri);
        var selected = QualityPreferenceMatcher.SelectBestOption(options, request.PreferredQuality);
        if (selected is null)
            return NeedsBrowser("No supported FastDLServer route is available for the preferred or a lower quality.", titlePageUri, progress);

        tracker.MoveTo(BigOneStage.QualityRouteSelected, $"Selected the {selected.QualityLabel} FastDLServer route");
        tracker.MoveTo(BigOneStage.FastDlServer, "Validating the FastDLServer route");
        var fileHost = await SendValidatedAsync(selected.TargetUrl, IsFastDlOrRecognizedFileHost, ct).ConfigureAwait(false);
        if (!fileHost.IsSuccess || !IsHtml(fileHost.ContentType) || !IsRecognizedFileHost(fileHost.FinalUri))
            return NeedsBrowser("FastDLServer did not lead to the expected file-host page.", titlePageUri, progress);

        tracker.MoveTo(BigOneStage.FileHostPage, "Recognizing the FAST CLOUD action");
        if (ContainsChallenge(fileHost.Body) || !IsRecognizedFileHostPage(fileHost.Body))
            return NeedsBrowser("The file-host page requires browser interaction or is not a supported BigOne page.", titlePageUri, progress);

        var cloudUri = ExtractCloudUri(fileHost.Body, _cloudOrigin);
        if (cloudUri is null || !IsApprovedCloudUri(cloudUri))
            return NeedsBrowser("No supported root-relative FAST CLOUD route was found.", titlePageUri, progress);

        var cloudPage = await SendValidatedAsync(cloudUri, IsApprovedCloudUri, ct).ConfigureAwait(false);
        if (!cloudPage.IsSuccess || !IsHtml(cloudPage.ContentType) || !IsCloudLandingPage(cloudPage.Body))
            return NeedsBrowser("The cloud landing page could not be recognized or requires browser interaction.", titlePageUri, progress);

        tracker.MoveTo(BigOneStage.CloudLandingPage, "Validating the cloud landing-page actions");
        var candidates = ParseCloudActions(cloudPage.Body, cloudPage.FinalUri);
        foreach (var candidate in candidates)
        {
            var verification = await SendValidatedAsync(candidate, IsApprovedFinalHost, ct).ConfigureAwait(false);
            if (!IsVerifiedDownloadResponse(verification))
                continue;

            tracker.MoveTo(BigOneStage.FinalDestinationVerified, "Verified a downloadable response; opening the IDM confirmation dialog");
            var fileName = SanitizeFileName(verification.FileName) ?? selected.FileName ??
                           SanitizeFileName(Path.GetFileName(Uri.UnescapeDataString(verification.FinalUri.AbsolutePath)));
            return DownloadResolveOutcome.Success(new ResolvedDownload
            {
                SourceId = request.SourceId,
                MovieTitle = request.MovieTitle,
                Quality = selected.QualityLabel,
                DownloadUrl = verification.FinalUri,
                FileName = fileName
            }, $"Verified {selected.QualityLabel} download destination.");
        }

        return NeedsBrowser("Cloud actions could not be independently verified as a downloadable response.", titlePageUri, progress);
    }

    /// <summary>Parses only Google Drive-labelled FastDLServer anchors inside each h5 quality block.</summary>
    public static IReadOnlyList<DownloadOption> ParseQualityOptions(string html, Uri baseUri)
    {
        var results = new List<DownloadOption>();
        var headings = HeadingRegex.Matches(html ?? string.Empty);
        for (var index = 0; index < headings.Count; index++)
        {
            var heading = headings[index];
            var headingText = CleanText(heading.Groups["body"].Value);
            var quality = QualityPreferenceMatcher.NormalizeQualityLabel(headingText);
            if (QualityPreferenceMatcher.ParseQualityRank(quality) is not (480 or 720 or 1080 or 2160))
                continue;

            var blockStart = heading.Index + heading.Length;
            var blockEnd = index + 1 < headings.Count ? headings[index + 1].Index : html.Length;
            var block = html.Substring(blockStart, blockEnd - blockStart);
            foreach (Match anchor in AnchorRegex.Matches(block))
            {
                var label = CleanText(anchor.Groups["body"].Value);
                if (!HasClass(anchor.Groups["attrs"].Value, "dl") ||
                    !label.Contains("Google Drive", StringComparison.OrdinalIgnoreCase))
                    continue;

                var href = HrefRegex.Match(anchor.Groups["attrs"].Value);
                if (!href.Success) continue;
                var raw = WebUtility.HtmlDecode(href.Groups["url"].Value).Trim();
                if (!Uri.TryCreate(baseUri, raw, out var target) || !IsFastDlRoute(target)) continue;
                results.Add(new DownloadOption(quality, target, null, "Google Drive"));
            }
        }

        return results;
    }

    /// <summary>Extracts the first title URL from the captured BollyFlix post-cards/article shape.</summary>
    public static bool TryExtractTitlePage(string html, Uri listingUri, out Uri titlePageUri)
    {
        titlePageUri = listingUri;
        if (!IsBollyFlixListingPage(html)) return false;

        foreach (Match article in ArticleRegex.Matches(html))
        {
            var attrs = article.Groups["attrs"].Value;
            if (!HasClass(attrs, "latestPost") || !HasClass(attrs, "excerpt")) continue;

            foreach (Match anchor in AnchorRegex.Matches(article.Groups["body"].Value))
            {
                var anchorAttrs = anchor.Groups["attrs"].Value;
                if (!HasClass(anchorAttrs, "post-image")) continue;
                var href = HrefRegex.Match(anchorAttrs);
                if (!href.Success) continue;
                var raw = WebUtility.HtmlDecode(href.Groups["url"].Value).Trim();
                if (!Uri.TryCreate(listingUri, raw, out var candidate) || !IsBollyFlixTitlePageUri(candidate))
                    return false;
                titlePageUri = candidate;
                return true;
            }
        }

        return false;
    }

    /// <summary>Resolves only a root-relative /cloud/... route against a validated configured origin.</summary>
    public static Uri? ResolveCloudPath(string rawHref, Uri configuredOrigin)
    {
        if (!IsValidCloudOrigin(configuredOrigin) || string.IsNullOrWhiteSpace(rawHref) ||
            rawHref.StartsWith("//", StringComparison.Ordinal) ||
            !rawHref.StartsWith("/cloud/", StringComparison.OrdinalIgnoreCase) ||
            !Uri.TryCreate(rawHref, UriKind.Relative, out var relative))
            return null;

        var resolved = new Uri(configuredOrigin, relative);
        return IsApprovedCloudUri(resolved) ? resolved : null;
    }

    private async Task<HttpStepResult> SendValidatedAsync(Uri start, Func<Uri, bool> allowed, CancellationToken ct)
    {
        var current = start;
        for (var redirectCount = 0; redirectCount <= 5; redirectCount++)
        {
            if (!IsSafeUri(current) || !allowed(current))
                return HttpStepResult.Failure(current, "A requested destination failed BigOne host validation.");

            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            var effectiveUri = response.RequestMessage?.RequestUri ?? current;
            if (effectiveUri != current || !IsSafeUri(effectiveUri) || !allowed(effectiveUri))
                return HttpStepResult.Failure(current, "The HTTP handler changed the destination before it could be validated.");

            var status = (int)response.StatusCode;
            if (status is 301 or 302 or 303 or 307 or 308)
            {
                if (redirectCount >= 5 || response.Headers.Location is null ||
                    !Uri.TryCreate(current, response.Headers.Location, out var nextUri) ||
                    !IsSafeUri(nextUri) || !allowed(nextUri))
                    return HttpStepResult.Failure(current, "A redirect destination was rejected before it was requested.");

                current = nextUri;
                continue;
            }

            if (status is 401 or 403 or 429 or 503)
                return HttpStepResult.Failure(current, "The host requires browser interaction or is restricting automated access.");
            if (status < 200 || status >= 300)
                return HttpStepResult.Failure(current, $"The host returned HTTP {status}.");

            var contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
            var disposition = response.Content.Headers.ContentDisposition;
            var fileName = disposition?.FileNameStar ?? disposition?.FileName?.Trim('"');
            var isAttachment = disposition?.DispositionType.Equals("attachment", StringComparison.OrdinalIgnoreCase) == true;
            var body = IsTextContent(contentType)
                ? await ReadBoundedTextAsync(response.Content, 512_000, ct).ConfigureAwait(false)
                : string.Empty;

            if (ContainsChallenge(body))
                return HttpStepResult.Failure(current, "The host returned a login, CAPTCHA, or browser challenge page.");

            return HttpStepResult.Success(current, status, contentType, body, fileName, isAttachment);
        }

        return HttpStepResult.Failure(current, "The redirect chain exceeded the BigOne limit.");
    }

    private static Uri? ExtractCloudUri(string html, Uri configuredOrigin)
    {
        foreach (Match anchor in AnchorRegex.Matches(html ?? string.Empty))
        {
            var label = CleanText(anchor.Groups["body"].Value);
            if (!label.Contains("FAST CLOUD", StringComparison.OrdinalIgnoreCase)) continue;
            var href = HrefRegex.Match(anchor.Groups["attrs"].Value);
            if (!href.Success) return null;
            return ResolveCloudPath(WebUtility.HtmlDecode(href.Groups["url"].Value).Trim(), configuredOrigin);
        }

        return null;
    }

    private static IReadOnlyList<Uri> ParseCloudActions(string html, Uri pageUri)
    {
        var candidates = new List<Uri>();
        foreach (Match anchor in AnchorRegex.Matches(html ?? string.Empty))
        {
            var label = CleanText(anchor.Groups["body"].Value);
            if (!label.Equals("Cloud Resume Download", StringComparison.OrdinalIgnoreCase) &&
                !label.Equals("Quick Download (Ready)", StringComparison.OrdinalIgnoreCase))
                continue;

            var href = HrefRegex.Match(anchor.Groups["attrs"].Value);
            if (!href.Success) continue;
            var raw = WebUtility.HtmlDecode(href.Groups["url"].Value).Trim();
            if (!Uri.TryCreate(pageUri, raw, out var candidate) || !IsApprovedFinalHost(candidate)) continue;
            candidates.Add(candidate);
        }

        return candidates;
    }

    private static bool IsVerifiedDownloadResponse(HttpStepResult response)
    {
        if (!response.IsSuccess || IsHtml(response.ContentType) || !IsApprovedFinalHost(response.FinalUri))
            return false;

        var contentType = response.ContentType;
        var binaryType = contentType.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase) ||
                         contentType.Equals("application/zip", StringComparison.OrdinalIgnoreCase) ||
                         contentType.Equals("application/x-7z-compressed", StringComparison.OrdinalIgnoreCase) ||
                         contentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase);
        var attachment = response.IsAttachment && !string.IsNullOrWhiteSpace(SanitizeFileName(response.FileName));
        return binaryType || attachment;
    }

    private static bool IsBollyFlixListingPage(string html) =>
        html.Contains("post-cards", StringComparison.OrdinalIgnoreCase) &&
        ArticleRegex.Matches(html ?? string.Empty).Cast<Match>().Any(article =>
            HasClass(article.Groups["attrs"].Value, "latestPost") && HasClass(article.Groups["attrs"].Value, "excerpt"));

    private static bool IsBollyFlixTitlePage(string html) =>
        html.Contains("post-single-content", StringComparison.OrdinalIgnoreCase) &&
        html.Contains("thecontent clearfix", StringComparison.OrdinalIgnoreCase);

    private static bool IsRecognizedFileHostPage(string html) =>
        html.Contains("Login To DL", StringComparison.OrdinalIgnoreCase) &&
        html.Contains("FAST CLOUD", StringComparison.OrdinalIgnoreCase);

    private static bool IsCloudLandingPage(string html) =>
        html.Contains("Cloud Resume Download", StringComparison.OrdinalIgnoreCase) &&
        html.Contains("Quick Download (Ready)", StringComparison.OrdinalIgnoreCase);

    private static bool IsSearchListingUri(Uri uri) =>
        uri.AbsolutePath.Equals("/", StringComparison.Ordinal) &&
        QueryContainsKey(uri.Query, "s");

    private static bool IsBollyFlixTitlePageUri(Uri uri) =>
        IsBollyFlixUri(uri) && !uri.AbsolutePath.Equals("/", StringComparison.Ordinal) &&
        !uri.AbsolutePath.Contains("/category/", StringComparison.OrdinalIgnoreCase) &&
        !uri.AbsolutePath.Contains("/tag/", StringComparison.OrdinalIgnoreCase);

    private static bool IsBollyFlixUri(Uri? uri) =>
        uri is { IsAbsoluteUri: true } && IsSafeUri(uri) &&
        uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
        uri.IdnHost.Equals("new1.bollyflix.tours", StringComparison.OrdinalIgnoreCase);

    private static bool IsFastDlRoute(Uri uri) =>
        IsSafeUri(uri) && uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
        uri.IdnHost.Equals("dl.fastdlserver.site", StringComparison.OrdinalIgnoreCase) &&
        uri.AbsolutePath.Equals("/", StringComparison.Ordinal) &&
        QueryContainsKey(uri.Query, "id") && QueryHasValue(uri.Query, "type", "file");

    private static bool IsFastDlOrRecognizedFileHost(Uri uri) => IsFastDlRoute(uri) || IsRecognizedFileHost(uri);

    private static bool IsRecognizedFileHost(Uri uri) =>
        IsSafeUri(uri) && uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
        FileHostHosts.Contains(uri.IdnHost) &&
        (uri.IdnHost.Equals("dl.fastdlserver.site", StringComparison.OrdinalIgnoreCase) ||
         uri.AbsolutePath.StartsWith("/file/", StringComparison.OrdinalIgnoreCase));

    private static bool IsApprovedCloudUri(Uri uri) =>
        IsSafeUri(uri) && uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
        CloudHosts.Contains(uri.IdnHost) && uri.AbsolutePath.StartsWith("/cloud/", StringComparison.OrdinalIgnoreCase);

    private static bool IsApprovedFinalHost(Uri uri) =>
        IsSafeUri(uri) && uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
        FinalDestinationHosts.Contains(uri.IdnHost);

    private static bool IsValidCloudOrigin(Uri uri) =>
        uri.IsAbsoluteUri && IsSafeUri(uri) && uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
        CloudHosts.Contains(uri.IdnHost) && uri.AbsolutePath.Equals("/", StringComparison.Ordinal) &&
        string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment);

    private static bool IsSafeUri(Uri uri) =>
        uri.IsAbsoluteUri && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) &&
        string.IsNullOrEmpty(uri.UserInfo) && uri.IsDefaultPort && !IsLocalOrPrivate(uri.Host) &&
        !IPAddress.TryParse(uri.Host, out _);

    private static bool IsLocalOrPrivate(string host)
    {
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
            return true;

        if (!IPAddress.TryParse(host, out var ip)) return false;
        if (IPAddress.IsLoopback(ip) || ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6UniqueLocal) return true;
        if (ip.AddressFamily != AddressFamily.InterNetwork) return false;

        var bytes = ip.GetAddressBytes();
        return bytes[0] == 0 || bytes[0] == 10 || bytes[0] == 127 ||
               bytes[0] == 169 && bytes[1] == 254 ||
               bytes[0] == 172 && bytes[1] is >= 16 and <= 31 ||
               bytes[0] == 192 && bytes[1] == 168 ||
               bytes[0] == 100 && bytes[1] is >= 64 and <= 127;
    }

    private static bool ContainsChallenge(string body) =>
        new[] { "just a moment", "captcha", "verify you are human", "cf-chl-", "login required", "sign in to continue", "authentication required" }
            .Any(marker => body.Contains(marker, StringComparison.OrdinalIgnoreCase));

    private static bool IsHtml(string contentType) =>
        contentType.Contains("html", StringComparison.OrdinalIgnoreCase) ||
        contentType.Contains("xhtml", StringComparison.OrdinalIgnoreCase);

    private static bool IsTextContent(string contentType) =>
        contentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase) ||
        contentType.Equals("application/xhtml+xml", StringComparison.OrdinalIgnoreCase) ||
        contentType.Equals("application/json", StringComparison.OrdinalIgnoreCase);

    private static string CleanText(string source) =>
        WhitespaceRegex.Replace(WebUtility.HtmlDecode(TagRegex.Replace(source ?? string.Empty, " ")), " ").Trim();

    private static bool HasClass(string attributes, string className)
    {
        var match = ClassRegex.Match(attributes ?? string.Empty);
        return match.Success && match.Groups["value"].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Contains(className, StringComparer.OrdinalIgnoreCase);
    }

    private static bool QueryContainsKey(string query, string key) =>
        query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .Any(parts => Uri.UnescapeDataString(parts[0]).Equals(key, StringComparison.OrdinalIgnoreCase));

    private static bool QueryHasValue(string query, string key, string value) =>
        query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .Any(parts => parts.Length == 2 && Uri.UnescapeDataString(parts[0]).Equals(key, StringComparison.OrdinalIgnoreCase) &&
                          Uri.UnescapeDataString(parts[1]).Equals(value, StringComparison.OrdinalIgnoreCase));

    private static string? SanitizeFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return null;
        var name = Path.GetFileName(fileName.Trim().Trim('"'));
        return name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ? null : name;
    }

    private static Uri? TryGetAbsoluteUri(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) ? uri : null;

    private static async Task<string> ReadBoundedTextAsync(HttpContent content, int maxChars, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8, true, 4096, leaveOpen: false);
        var buffer = new char[8192];
        var builder = new StringBuilder();
        int read;
        while (builder.Length < maxChars &&
               (read = await reader.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, maxChars - builder.Length)), ct).ConfigureAwait(false)) > 0)
            builder.Append(buffer, 0, read);
        return builder.ToString();
    }

    private static DownloadResolveOutcome NeedsBrowser(string reason, Uri? fallback, IProgress<string>? progress)
    {
        progress?.Report("BigOne · NeedsBrowser: " + reason);
        return DownloadResolveOutcome.BrowserRequired(reason, fallback);
    }

    private sealed record HttpStepResult(
        bool IsSuccess,
        Uri FinalUri,
        int StatusCode,
        string ContentType,
        string Body,
        string? FileName,
        bool IsAttachment,
        string? FailureReason)
    {
        public static HttpStepResult Failure(Uri uri, string reason) => new(false, uri, 0, string.Empty, string.Empty, null, false, reason);
        public static HttpStepResult Success(Uri uri, int status, string type, string body, string? name, bool attachment) =>
            new(true, uri, status, type, body, name, attachment, null);
    }

    private sealed class StageTracker(IProgress<string>? progress)
    {
        private BigOneStage _stage = BigOneStage.TitlePage;

        public void ReportCurrent(string message) => progress?.Report($"BigOne · {_stage}: {message}");

        public void MoveTo(BigOneStage next, string message)
        {
            var valid = (_stage, next) switch
            {
                (BigOneStage.TitlePage, BigOneStage.QualityRouteSelected) => true,
                (BigOneStage.QualityRouteSelected, BigOneStage.FastDlServer) => true,
                (BigOneStage.FastDlServer, BigOneStage.FileHostPage) => true,
                (BigOneStage.FileHostPage, BigOneStage.CloudLandingPage) => true,
                (BigOneStage.CloudLandingPage, BigOneStage.FinalDestinationVerified) => true,
                _ => false
            };
            if (!valid) throw new InvalidOperationException("Invalid BigOne workflow stage transition.");
            _stage = next;
            progress?.Report($"BigOne · {_stage}: {message}");
        }
    }
}
