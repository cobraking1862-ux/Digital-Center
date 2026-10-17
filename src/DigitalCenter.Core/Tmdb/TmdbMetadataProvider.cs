using System.Net.Http.Headers;
using System.Text.Json;
using DigitalCenter.Core.Configuration;
using DigitalCenter.Core.Logging;

namespace DigitalCenter.Core.Tmdb;

/// <summary>
/// TMDb REST client (v3, bearer-token authentication) built on HttpClient.
/// Deliberately dependency-free: the small response surface is mapped with
/// System.Text.Json instead of pulling in a client library. Network errors
/// surface as TmdbRequestException with a readable message.
/// </summary>
public sealed class TmdbMetadataProvider : ITmdbMetadataProvider, IDisposable
{
    private readonly TmdbOptions _options;
    private readonly IAppLogger? _logger;
    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;

    public TmdbMetadataProvider(TmdbOptions options, IAppLogger? logger = null, HttpClient? httpClient = null)
    {
        _options = options;
        _logger = logger;
        if (httpClient is null)
        {
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            _ownsHttpClient = true;
        }
        else
        {
            _http = httpClient;
        }
    }

    public bool IsConfigured => _options.IsConfigured;

    private void EnsureConfigured()
    {
        if (!_options.IsConfigured)
        {
            throw new TmdbNotConfiguredException();
        }
    }

    private HttpRequestMessage CreateRequest(string relativeUrl)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(_options.ApiBaseUrl), relativeUrl));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ReadAccessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    private async Task<JsonDocument> GetJsonAsync(string relativeUrl, CancellationToken ct)
    {
        EnsureConfigured();

        using var request = CreateRequest(relativeUrl);
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new TmdbRequestException($"TMDb could not be reached ({ex.Message}). Check the network connection.", ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new TmdbRequestException($"TMDb returned HTTP {(int)response.StatusCode} {response.ReasonPhrase} for '{relativeUrl}'.");
            }

            try
            {
                return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            }
            catch (JsonException ex)
            {
                throw new TmdbRequestException($"TMDb returned a malformed response for '{relativeUrl}'.", ex);
            }
        }
    }

    public async Task<IReadOnlyList<TmdbSearchCandidate>> SearchMoviesAsync(string query, int? year, CancellationToken ct = default)
    {
        var url = $"search/movie?query={Uri.EscapeDataString(query ?? string.Empty)}&include_adult=false&page=1";
        if (year.HasValue)
        {
            url += $"&year={year.Value}";
        }

        using var json = await GetJsonAsync(url, ct);
        var list = new List<TmdbSearchCandidate>();
        foreach (var element in EnumerateResults(json))
        {
            var releaseDate = element.TryGetProperty("release_date", out var rd) && rd.ValueKind == JsonValueKind.String
                ? rd.GetString()
                : null;

            list.Add(new TmdbSearchCandidate(
                element.GetProperty("id").GetInt64(),
                element.TryGetProperty("title", out var t) ? t.GetString() ?? string.Empty : string.Empty,
                ParseYear(releaseDate),
                element.TryGetProperty("original_title", out var ot) ? ot.GetString() : null,
                element.TryGetProperty("overview", out var ov) ? ov.GetString() : null,
                element.TryGetProperty("poster_path", out var pp) ? pp.GetString() : null,
                IsSeries: false,
                ReleaseInfo: releaseDate ?? string.Empty));
        }

        _logger?.Info($"TMDb movie search for '{query}' returned {list.Count} result(s).");
        return list;
    }

    public async Task<IReadOnlyList<TmdbSearchCandidate>> SearchSeriesAsync(string query, int? year, CancellationToken ct = default)
    {
        var url = $"search/tv?query={Uri.EscapeDataString(query ?? string.Empty)}&include_adult=false&page=1";
        if (year.HasValue)
        {
            url += $"&first_air_date_year={year.Value}";
        }

        using var json = await GetJsonAsync(url, ct);
        var list = new List<TmdbSearchCandidate>();
        foreach (var element in EnumerateResults(json))
        {
            var firstAirDate = element.TryGetProperty("first_air_date", out var fd) && fd.ValueKind == JsonValueKind.String
                ? fd.GetString()
                : null;

            list.Add(new TmdbSearchCandidate(
                element.GetProperty("id").GetInt64(),
                element.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty,
                ParseYear(firstAirDate),
                element.TryGetProperty("original_name", out var on) ? on.GetString() : null,
                element.TryGetProperty("overview", out var ov) ? ov.GetString() : null,
                element.TryGetProperty("poster_path", out var pp) ? pp.GetString() : null,
                IsSeries: true,
                ReleaseInfo: firstAirDate ?? string.Empty));
        }

        _logger?.Info($"TMDb series search for '{query}' returned {list.Count} result(s).");
        return list;
    }

    public async Task<(long Id, bool IsSeries)?> FindByImdbIdAsync(string imdbId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(imdbId))
        {
            return null;
        }

        using var json = await GetJsonAsync($"find/{Uri.EscapeDataString(imdbId)}?external_source=imdb_id", ct);
        var root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var (property, isSeries) in new[] { ("movie_results", false), ("tv_results", true) })
        {
            if (root.TryGetProperty(property, out var array) && array.ValueKind == JsonValueKind.Array &&
                array.EnumerateArray().FirstOrDefault() is { ValueKind: JsonValueKind.Object } first &&
                first.TryGetProperty("id", out var id))
            {
                return (id.GetInt64(), isSeries);
            }
        }

        return null;
    }

    public async Task<TmdbTitleMetadata?> GetMovieAsync(long tmdbId, CancellationToken ct = default)
    {
        using var json = await GetJsonAsync($"movie/{tmdbId}?append_to_response=credits,videos,external_ids", ct);
        var root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("id", out _))
        {
            return null;
        }

        var title = root.TryGetProperty("title", out var t) ? t.GetString() ?? string.Empty : string.Empty;
        var releaseDate = ParseDate(root, "release_date");

        var people = new List<TmdbPersonCredit>();
        var genres = ReadGenres(root);
        var countries = ReadCountries(root);
        ReadCredits(root, people);
        var trailer = ReadTrailer(root);

        return new TmdbTitleMetadata
        {
            Title = title,
            OriginalTitle = root.TryGetProperty("original_title", out var ot) ? ot.GetString() : null,
            Year = ParseYear(releaseDate?.ToString("yyyy-MM-dd")),
            ReleaseDate = releaseDate,
            Synopsis = root.TryGetProperty("overview", out var ov) ? ov.GetString() : null,
            OriginalLanguage = root.TryGetProperty("original_language", out var ol) ? ol.GetString() : null,
            PosterPath = root.TryGetProperty("poster_path", out var pp) ? pp.GetString() : null,
            BackdropPath = root.TryGetProperty("backdrop_path", out var bp) ? bp.GetString() : null,
            ImdbId = ReadImdbId(root),
            VoteAverage = root.TryGetProperty("vote_average", out var va) && va.ValueKind == JsonValueKind.Number ? va.GetDouble() : null,
            TrailerName = trailer.Name,
            TrailerUrl = trailer.Url,
            IsSeries = false,
            Genres = genres,
            Countries = countries,
            People = people,
            Seasons = []
        };
    }

    /// <summary>Series metadata including every available season's episodes.</summary>
    public Task<TmdbTitleMetadata?> GetSeriesAsync(long tmdbId, CancellationToken ct = default) =>
        GetSeriesCoreAsync(tmdbId, fetchSeasons: true, ct);

    /// <summary>Series metadata in one request, without the per-season episode lists.</summary>
    public Task<TmdbTitleMetadata?> GetSeriesSummaryAsync(long tmdbId, CancellationToken ct = default) =>
        GetSeriesCoreAsync(tmdbId, fetchSeasons: false, ct);

    private async Task<TmdbTitleMetadata?> GetSeriesCoreAsync(long tmdbId, bool fetchSeasons, CancellationToken ct)
    {
        using var json = await GetJsonAsync($"tv/{tmdbId}?append_to_response=credits,videos,external_ids", ct);
        var root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("id", out _))
        {
            return null;
        }

        var title = root.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty;
        var firstAir = ParseDate(root, "first_air_date");

        var people = new List<TmdbPersonCredit>();
        var genres = ReadGenres(root);
        var countries = ReadCountries(root);
        ReadCredits(root, people);
        var trailer = ReadTrailer(root);

        // Series creators (the TV equivalent of a director).
        if (root.TryGetProperty("created_by", out var creators) && creators.ValueKind == JsonValueKind.Array)
        {
            foreach (var person in creators.EnumerateArray())
            {
                if (person.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
                {
                    people.Add(new TmdbPersonCredit(
                        name.GetString()!,
                        "Creator",
                        null,
                        person.TryGetProperty("id", out var pid) && pid.ValueKind == JsonValueKind.Number ? pid.GetInt64() : null,
                        person.TryGetProperty("profile_path", out var prof) ? prof.GetString() : null));
                }
            }
        }

        // Season/episode breakdown: one request per season, capped to keep
        // pathological metadata from stalling the import. Callers that only
        // need one season (the import workflow) use the summary + a single
        // GetSeriesSeasonAsync call instead of paying for all of them.
        var seasons = new List<TmdbSeasonMetadata>();
        var seasonCount = 0;
        if (root.TryGetProperty("seasons", out var seasonList) && seasonList.ValueKind == JsonValueKind.Array)
        {
            foreach (var season in seasonList.EnumerateArray())
            {
                if (!season.TryGetProperty("season_number", out var sn) || sn.ValueKind != JsonValueKind.Number)
                {
                    continue;
                }

                var number = sn.GetInt32();
                if (number < 0 || number > 25 || seasons.Any(s => s.SeasonNumber == number))
                {
                    continue;
                }

                seasonCount++;
                if (!fetchSeasons)
                {
                    continue;
                }

                var episodes = await FetchSeasonEpisodesAsync(tmdbId, number, ct);
                seasons.Add(new TmdbSeasonMetadata(number, episodes));
            }
        }

        return new TmdbTitleMetadata
        {
            Title = title,
            OriginalTitle = root.TryGetProperty("original_name", out var on) ? on.GetString() : null,
            Year = ParseYear(firstAir?.ToString("yyyy-MM-dd")),
            ReleaseDate = firstAir,
            Synopsis = root.TryGetProperty("overview", out var ov) ? ov.GetString() : null,
            OriginalLanguage = root.TryGetProperty("original_language", out var ol) ? ol.GetString() : null,
            PosterPath = root.TryGetProperty("poster_path", out var pp) ? pp.GetString() : null,
            BackdropPath = root.TryGetProperty("backdrop_path", out var bp) ? bp.GetString() : null,
            ImdbId = ReadImdbId(root),
            VoteAverage = root.TryGetProperty("vote_average", out var va) && va.ValueKind == JsonValueKind.Number ? va.GetDouble() : null,
            TrailerName = trailer.Name,
            TrailerUrl = trailer.Url,
            IsSeries = true,
            AiringStatus = root.TryGetProperty("status", out var st) ? st.GetString() : null,
            Genres = genres,
            Countries = countries,
            People = people,
            Seasons = seasons,
            SeasonCount = seasonCount > 0 ? seasonCount : null
        };
    }

    /// <summary>Episodes of a single season; null when TMDb does not have it.</summary>
    public async Task<TmdbSeasonMetadata?> GetSeriesSeasonAsync(long tmdbId, int seasonNumber, CancellationToken ct = default)
    {
        if (seasonNumber < 0)
        {
            return null;
        }

        try
        {
            using var seasonJson = await GetJsonAsync($"tv/{tmdbId}/season/{seasonNumber}", ct);
            return new TmdbSeasonMetadata(seasonNumber, ParseSeasonEpisodes(seasonJson.RootElement, seasonNumber));
        }
        catch (TmdbRequestException ex)
        {
            // A missing/unavailable season must not fail the whole import.
            _logger?.Warn($"TMDb season fetch failed for series {tmdbId} season {seasonNumber}: {ex.Message}");
            return null;
        }
    }

    private async Task<IReadOnlyList<TmdbEpisodeInfo>> FetchSeasonEpisodesAsync(long tmdbId, int seasonNumber, CancellationToken ct)
    {
        var season = await GetSeriesSeasonAsync(tmdbId, seasonNumber, ct);
        return season?.Episodes ?? [];
    }

    private static IReadOnlyList<TmdbEpisodeInfo> ParseSeasonEpisodes(JsonElement root, int seasonNumber)
    {
        var episodes = new List<TmdbEpisodeInfo>();
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("episodes", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            return episodes;
        }

        foreach (var ep in list.EnumerateArray())
        {
            if (!ep.TryGetProperty("episode_number", out var en) || en.ValueKind != JsonValueKind.Number)
            {
                continue;
            }

            episodes.Add(new TmdbEpisodeInfo(
                seasonNumber,
                en.GetInt32(),
                ep.TryGetProperty("name", out var name) ? name.GetString() : null,
                ep.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number ? id.GetInt64() : null));
        }

        return episodes;
    }

    private async Task<Stream> DownloadImageAsync(string imagePath, string size, CancellationToken ct)
    {
        EnsureConfigured();

        if (string.IsNullOrWhiteSpace(imagePath))
        {
            throw new ArgumentException("Image path is empty.", nameof(imagePath));
        }

        var url = $"{_options.ImageBaseUrl.TrimEnd('/')}/{size}{imagePath}";
        try
        {
            return await _http.GetStreamAsync(url, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new TmdbRequestException($"The image could not be downloaded ({ex.Message}).", ex);
        }
    }

    public Task<Stream> DownloadPosterAsync(string posterPath, CancellationToken ct = default) =>
        DownloadImageAsync(posterPath, _options.PosterSize, ct);

    public Task<Stream> DownloadBackdropAsync(string backdropPath, CancellationToken ct = default) =>
        DownloadImageAsync(backdropPath, _options.BackdropSize, ct);

    public Task<Stream> DownloadProfileAsync(string profilePath, CancellationToken ct = default) =>
        DownloadImageAsync(profilePath, _options.ProfileSize, ct);

    public async Task<bool> TestConnectionAsync(CancellationToken ct = default)
    {
        try
        {
            using var json = await GetJsonAsync("configuration", ct);
            return json.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (Exception ex) when (ex is TmdbRequestException or TmdbNotConfiguredException)
        {
            return false;
        }
    }

    private static IEnumerable<JsonElement> EnumerateResults(JsonDocument document)
    {
        if (document.RootElement.ValueKind == JsonValueKind.Object &&
            document.RootElement.TryGetProperty("results", out var results) &&
            results.ValueKind == JsonValueKind.Array)
        {
            return results.EnumerateArray().ToList();
        }

        return [];
    }

    private static int? ParseYear(string? date)
    {
        if (string.IsNullOrWhiteSpace(date) || date.Length < 4)
        {
            return null;
        }

        return int.TryParse(date[..4], out var year) ? year : null;
    }

    private static DateTime? ParseDate(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var text = property.GetString();
        return DateTime.TryParse(text, out var value) ? value : null;
    }

    private static string? ReadImdbId(JsonElement root)
    {
        if (!root.TryGetProperty("external_ids", out var external) || external.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return external.TryGetProperty("imdb_id", out var imdb) && imdb.ValueKind == JsonValueKind.String
            ? imdb.GetString()
            : null;
    }

    private static (string? Name, string? Url) ReadTrailer(JsonElement root)
    {
        if (!root.TryGetProperty("videos", out var videos) || videos.ValueKind != JsonValueKind.Object ||
            !videos.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
        {
            return (null, null);
        }

        JsonElement? chosen = null;
        foreach (var video in results.EnumerateArray())
        {
            var site = video.TryGetProperty("site", out var s) ? s.GetString() : null;
            if (!string.Equals(site, "YouTube", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var type = video.TryGetProperty("type", out var ty) ? ty.GetString() : null;
            var isTrailer = string.Equals(type, "Trailer", StringComparison.OrdinalIgnoreCase);
            if (isTrailer && chosen is null)
            {
                chosen = video;
            }

            // Prefer an official trailer.
            var official = video.TryGetProperty("official", out var of) && of.ValueKind == JsonValueKind.True;
            if (isTrailer && official)
            {
                chosen = video;
                break;
            }
        }

        if (chosen is null)
        {
            return (null, null);
        }

        var key = chosen.Value.TryGetProperty("key", out var k) ? k.GetString() : null;
        var name = chosen.Value.TryGetProperty("name", out var nm) ? nm.GetString() : null;
        return string.IsNullOrWhiteSpace(key) ? (null, null) : (name, $"https://www.youtube.com/watch?v={key}");
    }

    private static List<string> ReadGenres(JsonElement root)
    {
        var list = new List<string>();
        if (root.TryGetProperty("genres", out var genres) && genres.ValueKind == JsonValueKind.Array)
        {
            foreach (var genre in genres.EnumerateArray())
            {
                if (genre.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
                {
                    list.Add(name.GetString()!);
                }
            }
        }

        return list;
    }

    private static List<string> ReadCountries(JsonElement root)
    {
        var list = new List<string>();
        if (root.TryGetProperty("production_countries", out var countries) && countries.ValueKind == JsonValueKind.Array)
        {
            foreach (var country in countries.EnumerateArray())
            {
                if (country.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
                {
                    list.Add(name.GetString()!);
                }
            }
        }

        return list;
    }

    private static void ReadCredits(JsonElement root, List<TmdbPersonCredit> people)
    {
        if (!root.TryGetProperty("credits", out var credits) || credits.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (credits.TryGetProperty("cast", out var cast) && cast.ValueKind == JsonValueKind.Array)
        {
            var taken = 0;
            foreach (var actor in cast.EnumerateArray())
            {
                if (taken >= 12)
                {
                    break;
                }

                if (actor.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
                {
                    people.Add(new TmdbPersonCredit(
                        name.GetString()!,
                        "Actor",
                        actor.TryGetProperty("character", out var ch) ? ch.GetString() : null,
                        actor.TryGetProperty("id", out var pid) && pid.ValueKind == JsonValueKind.Number ? pid.GetInt64() : null,
                        actor.TryGetProperty("profile_path", out var prof) ? prof.GetString() : null));
                    taken++;
                }
            }
        }

        if (credits.TryGetProperty("crew", out var crew) && crew.ValueKind == JsonValueKind.Array)
        {
            foreach (var member in crew.EnumerateArray())
            {
                var job = member.TryGetProperty("job", out var j) ? j.GetString() : null;
                if (job is null ||
                    !(job.Equals("Director", StringComparison.OrdinalIgnoreCase) ||
                      job.Equals("Screenplay", StringComparison.OrdinalIgnoreCase) ||
                      job.Equals("Writer", StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                if (member.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String &&
                    !people.Any(p => p.Role == job && string.Equals(p.Name, name.GetString(), StringComparison.OrdinalIgnoreCase)))
                {
                    people.Add(new TmdbPersonCredit(
                        name.GetString()!,
                        job,
                        null,
                        member.TryGetProperty("id", out var pid) && pid.ValueKind == JsonValueKind.Number ? pid.GetInt64() : null,
                        member.TryGetProperty("profile_path", out var prof) ? prof.GetString() : null));
                }
            }
        }
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _http.Dispose();
        }
    }
}

/// <summary>Network-level or protocol-level TMDb failure with an operator-readable message.</summary>
public sealed class TmdbRequestException : Exception
{
    public TmdbRequestException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}
