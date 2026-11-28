using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DigitalCenter.Core.Configuration;
using DigitalCenter.Core.Data;
using DigitalCenter.Core.Logging;
using Microsoft.EntityFrameworkCore;

namespace DigitalCenter.Core.Services;

public enum LookupCheckStatus
{
    Pending,
    Checking,
    Results,
    NoResults,
    Blocked,
    Error
}

public sealed record ImdbLookupResult(
    string ImdbId,
    string Title,
    int? Year,
    string MediaType,
    string? CastSummary,
    string? PosterUrl,
    bool InLocalLibrary,
    int? LocalTitleId);

public sealed record SiteAvailabilityCheckResult(
    LookupSiteEntry Site,
    string CheckedUrl,
    LookupCheckStatus Status,
    string Detail,
    int? HttpStatusCode,
    long ElapsedMs);

/// <summary>
/// Resolves movies and series on IMDb (via IMDb's public suggestion API) to obtain
/// canonical IMDb IDs (tt...), cross-checks against DigitalCenter's local catalog,
/// and runs background availability checks across user-configured lookup sites.
/// </summary>
public sealed class ImdbLookupService
{
    private static readonly Regex ImdbIdRegex = new(@"^tt\d{6,10}$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex HeadBlockRegex = new(@"<head\b[^>]*>.*?</head>", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex ScriptStyleRegex = new(@"<(script|style|noscript|form)[^>]*>.*?</\1>", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex InputTagRegex = new(@"<input\b[^>]*>", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex TitleTagRegex = new(@"<title[^>]*>(.*?)</title>", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex AltTitleAttrRegex = new(@"\b(?:alt|title)\s*=\s*[""']([^""']{2,160})[""']", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex HtmlTagRegex = new(@"<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex NonAlphaNumRegex = new(@"[^\p{L}\p{N}]+", RegexOptions.Compiled);

    // Matches heading elements that echo the search query, including nested spans like:
    // <h1>Search Results for <span class="hl">"The Sweeper"</span></h1>
    private static readonly Regex EchoHeadingElementRegex = new(
        @"<(h[1-4])\b[^>]*>[^<]{0,60}(?:search\s+results?\s+for|results?\s+for|you\s+searched\s+for|searching\s+for)\s*[:\-–—]?\s*(?:<[^>]+>|[^<]){0,220}</\1>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private static readonly Regex EchoInlineElementRegex = new(
        @"<(p|span|div)\b[^>]*>\s*(?:search\s+results?\s+for|results?\s+for|you\s+searched\s+for|searching\s+for)\s*[:\-–—]?\s*[^<]{0,140}</\1>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private static readonly string[] ChallengeTitleIndicators =
    [
        "just a moment",
        "attention required",
        "access denied",
        "verify you are human",
        "security check",
        "checking your browser",
        "ddos-guard",
        "one more step"
    ];

    private static readonly string[] ChallengeBodyIndicators =
    [
        "cf-browser-verification",
        "checking your browser before accessing",
        "enable javascript and cookies to continue",
        "verify you are human by completing the action below"
    ];

    private static readonly string[] NoResultsIndicators =
    [
        "no results found",
        "no result found",
        "nothing found",
        "sorry, no posts matched",
        "sorry, but nothing matched",
        "we apologize for any inconvenience",
        "please hit back on your browser",
        "use the search form below",
        "it seems we can't find",
        "it seems we can’t find",
        "0 results",
        "no matches found",
        "did not match any",
        "no posts found",
        "no post found",
        "no content found",
        "couldn't find any",
        "could not find any",
        "no movies found",
        "no titles found",
        "no items found",
        "no search results",
        "apologies, but no results",
        "there are no results",
        "nothing matched your search",
        "sorry, nothing found",
        "no matching results",
        "try searching with different keywords",
        "try another search"
    ];

    private readonly IDbContextFactory<DigitalCenterDbContext> _factory;
    private readonly HttpClient _http;
    private readonly IAppLogger? _logger;

    public ImdbLookupService(
        IDbContextFactory<DigitalCenterDbContext> factory,
        IAppLogger? logger = null,
        HttpClient? httpClient = null)
    {
        _factory = factory;
        _logger = logger;
        _http = httpClient ?? CreateDefaultHttpClient();
    }

    private static HttpClient CreateDefaultHttpClient()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5
        };

        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(12) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
        client.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,application/json;q=0.9,*/*;q=0.8");
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
        return client;
    }

    public static IReadOnlyList<LookupSiteEntry> GetDefaultSites() =>
    [
        new() { Id = "builtin-imdb", Name = "IMDb", UrlTemplate = "https://www.imdb.com/title/{imdb_id}/", IsEnabled = true, IsBuiltIn = true },
        new() { Id = "builtin-tmdb", Name = "TMDb", UrlTemplate = "https://www.themoviedb.org/search?query={query}", IsEnabled = true, IsBuiltIn = true },
        new() { Id = "builtin-justwatch", Name = "JustWatch", UrlTemplate = "https://www.justwatch.com/us/search?q={title}", IsEnabled = true, IsBuiltIn = true },
        new() { Id = DownloadWorkflowIds.BigOneSite, Name = "BollyFlix", UrlTemplate = DownloadWorkflowIds.BigOneLookupUrlTemplate, IsEnabled = true, IsBuiltIn = true },
        new() { Id = "builtin-rt", Name = "Rotten Tomatoes", UrlTemplate = "https://www.rottentomatoes.com/search?search={title}", IsEnabled = false, IsBuiltIn = true },
        new() { Id = "builtin-letterboxd", Name = "Letterboxd", UrlTemplate = "https://letterboxd.com/imdb/{imdb_id}/", IsEnabled = false, IsBuiltIn = true },
        new() { Id = "builtin-youtube", Name = "YouTube Trailer", UrlTemplate = "https://www.youtube.com/results?search_query={title}+{year}+official+trailer", IsEnabled = false, IsBuiltIn = true }
    ];

    public static bool IsImdbId(string? text) =>
        !string.IsNullOrWhiteSpace(text) && ImdbIdRegex.IsMatch(text.Trim());

    /// <summary>
    /// Searches IMDb for the given movie/series title or IMDb ID (tt...) and marks
    /// which results are already present in DigitalCenter's local catalog.
    /// </summary>
    public async Task<IReadOnlyList<ImdbLookupResult>> SearchImdbAsync(string query, CancellationToken ct = default)
    {
        var trimmed = (query ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return [];
        }

        var normalizedForUrl = NormalizeSuggestionKey(trimmed);
        var firstChar = normalizedForUrl.Length > 0 && char.IsLetterOrDigit(normalizedForUrl[0])
            ? char.ToLowerInvariant(normalizedForUrl[0])
            : 'x';

        var url = $"https://v3.sg.media-imdb.com/suggestion/{firstChar}/{Uri.EscapeDataString(normalizedForUrl)}.json";

        List<ImdbLookupResult> rawResults = [];
        try
        {
            using var response = await _http.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(ct);
            rawResults = ParseSuggestionJson(json);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.Warn($"IMDb lookup request failed for '{trimmed}': {ex.Message}");
            if (IsImdbId(trimmed))
            {
                rawResults.Add(new ImdbLookupResult(
                    ImdbId: trimmed.ToLowerInvariant(),
                    Title: trimmed.ToLowerInvariant(),
                    Year: null,
                    MediaType: "Title",
                    CastSummary: null,
                    PosterUrl: null,
                    InLocalLibrary: false,
                    LocalTitleId: null));
            }
            else
            {
                throw new InvalidOperationException($"Could not reach IMDb search ({ex.Message}).", ex);
            }
        }

        if (rawResults.Count == 0 && IsImdbId(trimmed))
        {
            rawResults.Add(new ImdbLookupResult(
                ImdbId: trimmed.ToLowerInvariant(),
                Title: trimmed.ToLowerInvariant(),
                Year: null,
                MediaType: "Title",
                CastSummary: null,
                PosterUrl: null,
                InLocalLibrary: false,
                LocalTitleId: null));
        }

        return await AnnotateLocalLibraryMatchesAsync(rawResults, ct);
    }

    /// <summary>
    /// Runs a background HTTP check against one configured lookup site and classifies
    /// whether the site returned Results, NoResults, Blocked (CAPTCHA/403), or Error.
    /// </summary>
    public async Task<SiteAvailabilityCheckResult> CheckSiteAvailabilityAsync(
        LookupSiteEntry site,
        string imdbId,
        string title,
        int? year,
        CancellationToken ct = default)
    {
        var url = BuildLookupUrl(site.UrlTemplate, imdbId, title, year);
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return new SiteAvailabilityCheckResult(site, url, LookupCheckStatus.Error, "Invalid URL template", null, 0);
        }

        var sw = Stopwatch.StartNew();
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, uri);
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            var statusCode = (int)resp.StatusCode;

            var body = await ReadBodySnippetAsync(resp, maxChars: 512_000, ct);
            sw.Stop();

            var (status, detail) = ClassifyResponse(statusCode, body, imdbId, title, year, site.NoResultsMarker);
            return new SiteAvailabilityCheckResult(site, url, status, detail, statusCode, sw.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException)
        {
            sw.Stop();
            return new SiteAvailabilityCheckResult(site, url, LookupCheckStatus.Error, "Timed out", null, sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new SiteAvailabilityCheckResult(site, url, LookupCheckStatus.Error, ex.Message, null, sw.ElapsedMilliseconds);
        }
    }

    /// <summary>
    /// Classifies an HTTP response from a lookup site into Results, NoResults, Blocked, or Error.
    /// Strips &lt;head&gt;, &lt;form&gt;, &lt;script&gt;, and echoed search-heading elements (including nested spans)
    /// while preserving image/link alt &amp; title attributes and normalizing whitespace.
    /// </summary>
    public static (LookupCheckStatus Status, string Detail) ClassifyResponse(
        int statusCode,
        string? body,
        string? imdbId,
        string? title,
        int? year,
        string? customNoResultsMarker = null)
    {
        var raw = body ?? string.Empty;

        if (statusCode == 404)
        {
            return (LookupCheckStatus.NoResults, "No results (HTTP 404)");
        }

        if (statusCode is 403 or 429 or 503)
        {
            return (LookupCheckStatus.Blocked, $"Blocked / protection (HTTP {statusCode})");
        }

        if (statusCode < 200 || statusCode >= 400)
        {
            return (LookupCheckStatus.Error, $"HTTP {statusCode}");
        }

        // Check <title> for genuine bot-challenge interstitials (e.g. "Just a moment...")
        var titleMatch = TitleTagRegex.Match(raw);
        var pageTitle = titleMatch.Success ? WebUtility.HtmlDecode(titleMatch.Groups[1].Value).Trim() : string.Empty;
        if (!string.IsNullOrEmpty(pageTitle) &&
            ChallengeTitleIndicators.Any(ind => pageTitle.Contains(ind, StringComparison.OrdinalIgnoreCase)))
        {
            return (LookupCheckStatus.Blocked, "Bot protection / CAPTCHA page detected");
        }

        // Strip <head>, <script>, <style>, <form>, <input>, and <title> so meta/canonical URLs
        // and search input boxes that echo the query do not cause false positives.
        var bodyHtml = HeadBlockRegex.Replace(raw, " ");
        bodyHtml = ScriptStyleRegex.Replace(bodyHtml, " ");
        bodyHtml = InputTagRegex.Replace(bodyHtml, " ");
        bodyHtml = TitleTagRegex.Replace(bodyHtml, " ");

        // Remove heading elements echoing "Search Results for ..." (including nested <span> tags)
        var contentHtml = EchoHeadingElementRegex.Replace(bodyHtml, " ");
        contentHtml = EchoInlineElementRegex.Replace(contentHtml, " ");

        // Preserve alt="..." and title="..." attribute text from poster images and result links before stripping tags
        var attrBuilder = new StringBuilder();
        foreach (Match m in AltTitleAttrRegex.Matches(contentHtml))
        {
            attrBuilder.Append(' ').Append(m.Groups[1].Value);
        }

        // Normalize whitespace so "No\n  posts  found" or "No <span>posts</span> found" still matches cleanly
        var visibleText = WhitespaceRegex.Replace(
            WebUtility.HtmlDecode(HtmlTagRegex.Replace(contentHtml, " ") + attrBuilder),
            " ");

        // 1. Optional site-specific custom "no results" marker configured in Maintenance
        if (!string.IsNullOrWhiteSpace(customNoResultsMarker) &&
            (visibleText.Contains(customNoResultsMarker.Trim(), StringComparison.OrdinalIgnoreCase) ||
             raw.Contains(customNoResultsMarker.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            return (LookupCheckStatus.NoResults, $"No results ('{customNoResultsMarker.Trim()}')");
        }

        // 2. Standard "no results" phrases in visible content
        foreach (var marker in NoResultsIndicators)
        {
            if (visibleText.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return (LookupCheckStatus.NoResults, "No matching results");
            }
        }

        // 3. Direct IMDb ID hit in remaining page content or links
        if (!string.IsNullOrWhiteSpace(imdbId) &&
            contentHtml.Contains(imdbId.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return (LookupCheckStatus.Results, $"Matched IMDb ID ({imdbId.Trim()})");
        }

        // 4. Punctuation-normalized title match in visible text & alt/title attributes
        var cleanTitle = (title ?? string.Empty).Trim();
        var normalizedTitle = NormalizeForComparison(cleanTitle);
        var normalizedVisible = NormalizeForComparison(visibleText);

        if (normalizedTitle.Length >= 2 && normalizedVisible.Contains(normalizedTitle, StringComparison.Ordinal))
        {
            var yearNote = year.HasValue && normalizedVisible.Contains(year.Value.ToString(), StringComparison.Ordinal)
                ? $" ({year.Value})"
                : string.Empty;
            return (LookupCheckStatus.Results, $"Matched '{cleanTitle}'{yearNote}");
        }

        // 5. Multi-word title token fallback (handles subtitles/colons/reordered punctuation on title-based sites)
        var titleWords = normalizedTitle.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length >= 3)
            .Distinct()
            .ToList();

        if (titleWords.Count >= 2 && titleWords.All(w => normalizedVisible.Contains(w, StringComparison.Ordinal)))
        {
            var yearNote = year.HasValue && normalizedVisible.Contains(year.Value.ToString(), StringComparison.Ordinal)
                ? $" ({year.Value})"
                : string.Empty;
            return (LookupCheckStatus.Results, $"Matched '{cleanTitle}'{yearNote}");
        }

        // 6. Only if no results were found, check for interstitial body challenge phrases
        if (ChallengeBodyIndicators.Any(ind => raw.Contains(ind, StringComparison.OrdinalIgnoreCase)))
        {
            return (LookupCheckStatus.Blocked, "Bot protection / CAPTCHA page detected");
        }

        return (LookupCheckStatus.NoResults, "No matching title or IMDb ID found on page");
    }

    private static string NormalizeForComparison(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        return NonAlphaNumRegex.Replace(text.ToLowerInvariant(), " ").Trim();
    }

    /// <summary>Parses IMDb's public suggestion JSON payload into title results with IMDb IDs.</summary>
    public static List<ImdbLookupResult> ParseSuggestionJson(string json)
    {
        var results = new List<ImdbLookupResult>();
        if (string.IsNullOrWhiteSpace(json))
        {
            return results;
        }

        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("d", out var items) || items.ValueKind != JsonValueKind.Array)
        {
            return results;
        }

        foreach (var item in items.EnumerateArray())
        {
            if (!item.TryGetProperty("id", out var idProp))
            {
                continue;
            }

            var id = idProp.GetString()?.Trim();
            if (string.IsNullOrWhiteSpace(id) || !id.StartsWith("tt", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var title = item.TryGetProperty("l", out var lProp) ? lProp.GetString()?.Trim() : null;
            if (string.IsNullOrWhiteSpace(title))
            {
                continue;
            }

            int? year = null;
            if (item.TryGetProperty("y", out var yProp) && yProp.ValueKind == JsonValueKind.Number && yProp.TryGetInt32(out var y))
            {
                year = y;
            }

            var qRaw = item.TryGetProperty("q", out var qProp) ? qProp.GetString()?.Trim() : null;
            var mediaType = MapMediaType(qRaw);

            var cast = item.TryGetProperty("s", out var sProp) ? sProp.GetString()?.Trim() : null;

            string? posterUrl = null;
            if (item.TryGetProperty("i", out var iProp) && iProp.ValueKind == JsonValueKind.Object &&
                iProp.TryGetProperty("imageUrl", out var imgProp))
            {
                posterUrl = imgProp.GetString()?.Trim();
            }

            results.Add(new ImdbLookupResult(
                ImdbId: id,
                Title: title,
                Year: year,
                MediaType: mediaType,
                CastSummary: string.IsNullOrWhiteSpace(cast) ? null : cast,
                PosterUrl: string.IsNullOrWhiteSpace(posterUrl) ? null : posterUrl,
                InLocalLibrary: false,
                LocalTitleId: null));
        }

        return results;
    }

    /// <summary>
    /// Formats a user-configured URL template using the resolved IMDb ID (preferred for {query} and {imdb_id})
    /// plus optional {title}, {title_plus}, and {year} placeholders.
    /// </summary>
    public static string BuildLookupUrl(string urlTemplate, string? imdbId, string? title, int? year)
    {
        var template = (urlTemplate ?? string.Empty).Trim();
        if (template.Length == 0)
        {
            return string.Empty;
        }

        var cleanImdb = (imdbId ?? string.Empty).Trim();
        var cleanTitle = (title ?? string.Empty).Trim();
        var yearText = year.HasValue ? year.Value.ToString() : string.Empty;
        var primaryQuery = !string.IsNullOrEmpty(cleanImdb) ? cleanImdb : cleanTitle;

        var encodedImdb = Uri.EscapeDataString(cleanImdb);
        var encodedTitle = Uri.EscapeDataString(cleanTitle);
        var encodedTitlePlus = Uri.EscapeDataString(cleanTitle).Replace("%20", "+");
        var encodedQuery = Uri.EscapeDataString(primaryQuery);

        var hasPlaceholder =
            template.Contains("{imdb_id}", StringComparison.OrdinalIgnoreCase) ||
            template.Contains("{imdb}", StringComparison.OrdinalIgnoreCase) ||
            template.Contains("{title}", StringComparison.OrdinalIgnoreCase) ||
            template.Contains("{title_plus}", StringComparison.OrdinalIgnoreCase) ||
            template.Contains("{year}", StringComparison.OrdinalIgnoreCase) ||
            template.Contains("{query}", StringComparison.OrdinalIgnoreCase);

        if (!hasPlaceholder)
        {
            return template + encodedQuery;
        }

        var result = template;
        result = Regex.Replace(result, @"\{imdb_id\}", encodedImdb, RegexOptions.IgnoreCase);
        result = Regex.Replace(result, @"\{imdb\}", encodedImdb, RegexOptions.IgnoreCase);
        result = Regex.Replace(result, @"\{title\}", encodedTitle, RegexOptions.IgnoreCase);
        result = Regex.Replace(result, @"\{title_plus\}", encodedTitlePlus, RegexOptions.IgnoreCase);
        result = Regex.Replace(result, @"\{year\}", yearText, RegexOptions.IgnoreCase);
        result = Regex.Replace(result, @"\{query\}", encodedQuery, RegexOptions.IgnoreCase);
        return result;
    }

    private static async Task<string> ReadBodySnippetAsync(HttpResponseMessage response, int maxChars, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        var buffer = new char[8192];
        var sb = new StringBuilder();
        int read;
        while (sb.Length < maxChars && (read = await reader.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, maxChars - sb.Length)), ct)) > 0)
        {
            sb.Append(buffer, 0, read);
        }

        return sb.ToString();
    }

    private async Task<IReadOnlyList<ImdbLookupResult>> AnnotateLocalLibraryMatchesAsync(
        List<ImdbLookupResult> results,
        CancellationToken ct)
    {
        if (results.Count == 0)
        {
            return results;
        }

        try
        {
            await using var db = await _factory.CreateDbContextAsync(ct);
            var imdbIds = results.Select(r => r.ImdbId).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            var localByImdb = await db.MediaTitles
                .AsNoTracking()
                .Where(t => t.ImdbId != null && imdbIds.Contains(t.ImdbId))
                .Select(t => new { t.Id, t.ImdbId })
                .ToListAsync(ct);

            var imdbMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in localByImdb)
            {
                if (!string.IsNullOrWhiteSpace(row.ImdbId))
                {
                    imdbMap[row.ImdbId] = row.Id;
                }
            }

            var titles = results.Select(r => r.Title).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var localByTitle = await db.MediaTitles
                .AsNoTracking()
                .Where(t => titles.Contains(t.Title))
                .Select(t => new { t.Id, t.Title, t.ReleaseYear })
                .ToListAsync(ct);

            var annotated = new List<ImdbLookupResult>(results.Count);
            foreach (var r in results)
            {
                if (imdbMap.TryGetValue(r.ImdbId, out var matchedId))
                {
                    annotated.Add(r with { InLocalLibrary = true, LocalTitleId = matchedId });
                    continue;
                }

                var titleMatch = localByTitle.FirstOrDefault(t =>
                    string.Equals(t.Title, r.Title, StringComparison.OrdinalIgnoreCase) &&
                    (!r.Year.HasValue || !t.ReleaseYear.HasValue || t.ReleaseYear == r.Year));

                if (titleMatch is not null)
                {
                    annotated.Add(r with { InLocalLibrary = true, LocalTitleId = titleMatch.Id });
                }
                else
                {
                    annotated.Add(r);
                }
            }

            return annotated;
        }
        catch (Exception ex)
        {
            _logger?.Warn($"Local library cross-check during IMDb lookup failed: {ex.Message}");
            return results;
        }
    }

    private static string NormalizeSuggestionKey(string input)
    {
        var cleaned = Regex.Replace(input.Trim().ToLowerInvariant(), @"[^a-z0-9\s]", " ");
        cleaned = Regex.Replace(cleaned, @"\s+", "_").Trim('_');
        return string.IsNullOrEmpty(cleaned) ? input.Trim() : cleaned;
    }

    private static string MapMediaType(string? raw) => (raw ?? string.Empty).ToLowerInvariant() switch
    {
        "feature" => "Movie",
        "tv series" => "TV Series",
        "tv mini-series" => "Mini-Series",
        "tv movie" => "TV Movie",
        "short" => "Short",
        "video" => "Video",
        "" => "Title",
        _ => raw!
    };
}
