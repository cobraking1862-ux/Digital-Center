using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace DigitalCenter.Core.Services;

/// <summary>Result of parsing a media file name.</summary>
public sealed record ParsedFileName(
    bool IsSeries,
    string Title,
    int? Year,
    int? Season,
    int? Episode,
    bool Confident,
    string? Note = null)
{
    /// <summary>TMDb id parsed from a "[tmdb-movie-123]" tag, if present.</summary>
    public long? TmdbMovieId { get; init; }

    /// <summary>TMDb id parsed from a "[tmdb-tv-456]" tag, if present.</summary>
    public long? TmdbSeriesId { get; init; }

    /// <summary>IMDb id parsed from "[imdb-tt1234567]" or a bare "tt1234567", if present.</summary>
    public string? ImdbId { get; init; }

    /// <summary>True when an external identifier was present (high-confidence match signal).</summary>
    [JsonIgnore]
    public bool HasExternalId => TmdbMovieId.HasValue || TmdbSeriesId.HasValue || !string.IsNullOrEmpty(ImdbId);

    public static ParsedFileName Failed(string note) =>
        new(false, string.Empty, null, null, null, false, note);
}

/// <summary>
/// Extracts a probable title/year (movies) or title/season/episode (series)
/// from a file name. Deliberately conservative: it recognises common patterns
/// and cleans release noise (quality tags, codecs, groups) but does not try
/// to understand every naming convention in existence. Uncertain results are
/// flagged so the operator can correct them during import review.
/// </summary>
public sealed class FilenameParser
{
    // Years only (19xx / 20xx) - quality tokens like 1080p / 2160p can never match.
    private const string YearPattern = @"\b(19|20)\d{2}\b";
    private const string ParenYearPattern = @"\((19|20)\d{2}\)";

    /// <summary>
    /// Separators that commonly sit between season/episode tokens: "S01E01",
    /// "S01.E01", "S01_E01", "S01-E01", "S01 E01". Treating them all the same
    /// is what makes "Reacher.S01.E01" parse as an episode instead of a movie
    /// called "Reacher S01 E01".
    /// </summary>
    private const string TokenSeparators = @"[\s._\-]*";

    /// <summary>
    /// Word-boundary replacement for season/episode markers. '_' is a word
    /// character, so "\bS01" never matches inside "Show_S01E01"; an explicit
    /// "not preceded by a letter or digit" lookbehind does (and still keeps
    /// resolutions such as "1920x1080" out of the NxNN pattern).
    /// </summary>
    private const string MarkerStart = @"(?<![A-Za-z0-9])";
    private const string WordStart = @"(?<![A-Za-z])";

    private static readonly Regex ParenYear = new(ParenYearPattern, RegexOptions.Compiled);
    private static readonly Regex YearToken = new(@"^(19|20)\d{2}$", RegexOptions.Compiled);
    private static readonly Regex Resolution = new(@"^\d{3,4}x\d{3,4}$", RegexOptions.Compiled);
    private static readonly Regex SeasonEpisode = new(
        $@"{MarkerStart}s(\d{{1,2}}){TokenSeparators}e(?:p)?\.?(\d{{1,3}})(?!\d)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SeasonEpisodeAlt = new(
        $@"{MarkerStart}(\d{{1,2}})x(\d{{1,3}})(?!\d)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    /// <summary>"Season 1 Episode 1", "Season.01.Episode.05", "Season 1 - Episode 1".</summary>
    private static readonly Regex SeasonWordEpisode = new(
        $@"{WordStart}season{TokenSeparators}(\d{{1,2}}){TokenSeparators}(?:,|and|&)?{TokenSeparators}episode{TokenSeparators}(\d{{1,3}})(?!\d)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    /// <summary>A lone "Season 2" token in the file name (episode number comes from another marker).</summary>
    private static readonly Regex SeasonWord = new(
        $@"{WordStart}season{TokenSeparators}(\d{{1,2}})(?!\d)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex EpisodeOnly = new(
        $@"{WordStart}e(?:p)?{TokenSeparators}(\d{{1,3}})(?!\d)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex EpisodeWord = new(
        $@"{WordStart}episode{TokenSeparators}(\d{{1,3}})(?!\d)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex TrailingNumber = new(@"(?:^|\s)-\s*(\d{1,3})\s*$", RegexOptions.Compiled);
    private static readonly Regex BareTrailingNumber = new(@"^(.+?)\s+(\d{1,3})$", RegexOptions.Compiled);
    private static readonly Regex SeasonFolder = new(@"\bseason\s*(\d{1,2})\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SeasonFolderShort = new(@"^s(\d{1,2})$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // External identifier tags, e.g. "[tmdb-movie-693134]", "[tmdb-tv-1399]",
    // "[imdb-tt0111161]" or a bare "tt0111161". They pin the metadata match
    // to an exact catalog entry, so they are extracted before anything else.
    private static readonly Regex TmdbMovieTag = new(@"\[\s*tmdb[\s_-]*(?:movie|film)[\s_-]*(\d{1,10})\s*\]", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex TmdbSeriesTag = new(@"\[\s*tmdb[\s_-]*(?:tv|series|show)[\s_-]*(\d{1,10})\s*\]", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ImdbTag = new(@"\[\s*imdb[\s_-]*(tt\d{7,8})\s*\]", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ImdbBare = new(@"(?<![a-z0-9])(tt\d{7,8})(?![a-z0-9])", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Release-noise tokens; matching is case-insensitive and the first one ends the title.</summary>
    private static readonly HashSet<string> NoiseTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "480p", "576p", "720p", "1080p", "1080i", "2160p", "4k", "8k", "web", "webdl", "webrip",
        "webdub", "hdtv", "pdtv", "dvdrip", "dvdscr", "dvd", "bdrip", "brrip", "bluray", "blu", "ray",
        "bdremux", "remux", "hdrip", "hdcam", "cam", "tc", "hdts", "screener", "proper", "repack",
        "extended", "unrated", "remastered", "imax", "internal", "hevc", "h264", "h265", "x264",
        "x265", "xvid", "divx", "avc", "mpeg2", "aac", "aac2", "ac3", "eac3", "dts", "dtshd",
        "truehd", "atmos", "ddp", "dd", "flac", "mp3", "10bit", "8bit", "hdr", "hdr10", "hdr10plus",
        "dv", "dolby", "vision", "dual", "audio", "auds", "dubbed", "subbed", "subs", "sub",
        "multi", "complete", "season", "episode", "amzn", "nf", "netflix", "hmax", "dsnp", "atvp",
        "hulu", "cc", "tbs", "part", "pt", "vol", "volume", "batch", "rarbg", "yts", "ettv", "gloog",
        "fgt", "amt", "honorable", "iso", "mkv", "mp4", "avi", "reencoded", "hybrid", "upscale"
    };

    /// <summary>
    /// Parses a media file name. <paramref name="directoryHint"/> may carry a
    /// season folder name ("Season 02" / "S02") to resolve episode-only names.
    /// </summary>
    public ParsedFileName Parse(string fileName, string? directoryHint = null)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return ParsedFileName.Failed("Empty file name.");
        }

        var name = Path.GetFileNameWithoutExtension(fileName)?.Trim() ?? string.Empty;
        if (name.Length == 0)
        {
            return ParsedFileName.Failed("File name has no usable text.");
        }

        // External identifiers are removed from the name first; they must not
        // leak into the parsed title.
        var tmdbMovieId = MatchLong(TmdbMovieTag, name);
        var tmdbSeriesId = MatchLong(TmdbSeriesTag, name);
        var imdbId = ImdbTag.Match(name) is { Success: true } imdbTagMatch
            ? imdbTagMatch.Groups[1].Value
            : ImdbBare.Match(name) is { Success: true } bareMatch ? bareMatch.Groups[1].Value : null;
        name = TmdbMovieTag.Replace(name, " ");
        name = TmdbSeriesTag.Replace(name, " ");
        name = ImdbTag.Replace(name, " ");
        name = ImdbBare.Replace(name, " ");
        name = Regex.Replace(name, "\\s{2,}", " ").Trim(' ', '-', '_', '.');

        var seasonHint = ExtractSeasonHint(directoryHint);

        if (TryParseSeries(name, seasonHint, out var series))
        {
            // Episode-only names ("Episode 5.mkv") carry no show title; the
            // enclosing folder structure usually does ("Show Name/Season 2/").
            if (series.Title.Length == 0 && !string.IsNullOrWhiteSpace(directoryHint))
            {
                var fallbackTitle = FindShowTitleFromFolders(directoryHint);
                if (!string.IsNullOrEmpty(fallbackTitle))
                {
                    series = series with
                    {
                        Title = fallbackTitle,
                        Confident = false,
                        Note = "Title taken from the folder name; verify before import."
                    };
                }
            }

            return series with { TmdbMovieId = tmdbMovieId, TmdbSeriesId = tmdbSeriesId, ImdbId = imdbId };
        }

        var movie = ParseMovie(name);
        if (!movie.Confident && movie.Title.Length == 0 && tmdbMovieId is null && tmdbSeriesId is null && string.IsNullOrEmpty(imdbId))
        {
            return movie; // Nothing usable at all.
        }

        return movie with { TmdbMovieId = tmdbMovieId, TmdbSeriesId = tmdbSeriesId, ImdbId = imdbId };
    }

    private static long? MatchLong(Regex regex, string input)
    {
        var match = regex.Match(input);
        return match.Success && long.TryParse(match.Groups[1].Value, out var value) ? value : null;
    }

    /// <summary>Walks up from the file's folder, skipping season-like folders, to find the show folder name.</summary>
    private static string? FindShowTitleFromFolders(string directoryHint)
    {
        var dir = Path.TrimEndingDirectorySeparator(directoryHint!);
        while (!string.IsNullOrEmpty(dir))
        {
            var folder = Path.GetFileName(dir);
            if (string.IsNullOrWhiteSpace(folder))
            {
                break;
            }

            var trimmed = folder.Trim();
            if (!SeasonFolder.IsMatch(trimmed) &&
                !SeasonFolderShort.IsMatch(trimmed) &&
                !YearToken.IsMatch(trimmed))
            {
                var cleaned = CleanTitle(folder, out _);
                if (cleaned.Length > 0 && cleaned.Any(char.IsLetter))
                {
                    return cleaned;
                }

                return null; // The parent folder is noise; do not invent a title.
            }

            dir = Path.GetDirectoryName(dir);
        }

        return null;
    }

    private static int? ExtractSeasonHint(string? directoryHint)
    {
        if (string.IsNullOrWhiteSpace(directoryHint))
        {
            return null;
        }

        var folder = Path.GetFileName(Path.TrimEndingDirectorySeparator(directoryHint));
        if (string.IsNullOrWhiteSpace(folder))
        {
            return null;
        }

        var match = SeasonFolder.Match(folder) is { Success: true } m ? m : SeasonFolderShort.Match(folder.Trim());
        if (match.Success && int.TryParse(match.Groups[1].Value, out var season) && season is >= 0 and <= 99)
        {
            return season;
        }

        return null;
    }

    private bool TryParseSeries(string name, int? seasonHint, out ParsedFileName result)
    {
        result = null!;

        // Ordered attempts, most specific first:
        //   1. S01E02 (any separator between the tokens), 2. 1x02,
        //   3. "Season 1 Episode 1",
        //   4. episode-only forms that need a season number — taken from the
        //      enclosing folder ("Season 02") or from a "Season 2" token in
        //      the file name itself.
        Match? marker = null;
        int? season = null, episode = null;
        var bareNumberForm = false;
        var explicitMarker = false;   // the name carried both numbers
        var seasonFromName = false;   // "Season 2" token inside the file name
        var titleEnd = -1;            // cut the title here when it precedes the marker

        var m = SeasonEpisode.Match(name);
        if (m.Success)
        {
            marker = m;
            explicitMarker = true;
            season = int.Parse(m.Groups[1].Value);
            episode = int.Parse(m.Groups[2].Value);
        }
        else if ((m = SeasonEpisodeAlt.Match(name)).Success)
        {
            marker = m;
            explicitMarker = true;
            season = int.Parse(m.Groups[1].Value);
            episode = int.Parse(m.Groups[2].Value);
        }
        else if ((m = SeasonWordEpisode.Match(name)).Success)
        {
            marker = m;
            explicitMarker = true;
            season = int.Parse(m.Groups[1].Value);
            episode = int.Parse(m.Groups[2].Value);
        }
        else
        {
            var wordSeason = SeasonWord.Match(name);
            int? nameSeason = null;
            if (wordSeason.Success && int.TryParse(wordSeason.Groups[1].Value, out var parsedNameSeason)
                && parsedNameSeason is >= 0 and <= 99)
            {
                nameSeason = parsedNameSeason;
            }

            var effectiveHint = seasonHint ?? nameSeason;
            if (effectiveHint.HasValue)
            {
                seasonFromName = !seasonHint.HasValue && nameSeason.HasValue;
                titleEnd = seasonFromName ? wordSeason.Index : -1;

                if ((m = EpisodeOnly.Match(name)).Success)
                {
                    marker = m;
                    season = effectiveHint.Value;
                    episode = int.Parse(m.Groups[1].Value);
                }
                else if ((m = EpisodeWord.Match(name)).Success)
                {
                    marker = m;
                    season = effectiveHint.Value;
                    episode = int.Parse(m.Groups[1].Value);
                }
                else if ((m = TrailingNumber.Match(name)).Success)
                {
                    marker = m;
                    season = effectiveHint.Value;
                    episode = int.Parse(m.Groups[1].Value);
                }
                else if ((m = BareTrailingNumber.Match(name)).Success)
                {
                    marker = m;
                    season = effectiveHint.Value;
                    episode = int.Parse(m.Groups[2].Value);
                    bareNumberForm = true;
                }
            }
        }

        if (marker is null)
        {
            return false;
        }

        // The bare-trailing-number form carries the title inside the match;
        // every other form keeps it in front of the marker (and in front of a
        // "Season NN" token when that supplied the season number).
        var cut = bareNumberForm ? -1 : titleEnd >= 0 ? Math.Min(titleEnd, marker.Index) : marker.Index;
        var rawTitle = bareNumberForm ? marker.Groups[1].Value : name[..Math.Max(0, cut)];

        var title = CleanTitle(rawTitle, out _);
        int? year = null;
        if (title.Length > 0)
        {
            (title, year) = SplitTrailingYear(title);
        }

        if (title.Length == 0)
        {
            // Keep the series flag so callers know this was series-like.
            result = new ParsedFileName(true, string.Empty, year, season, episode, false,
                "Series pattern found but no title precedes the season/episode marker.");
            return true;
        }

        result = new ParsedFileName(
            IsSeries: true,
            Title: title,
            Year: year,
            Season: season,
            Episode: episode,
            Confident: title.Any(char.IsLetter),
            Note: explicitMarker
                ? null
                : seasonFromName
                    ? "Season taken from the file name."
                    : seasonHint.HasValue
                        ? "Season taken from folder name."
                        : null);
        return true;
    }

    /// <summary>
    /// Splits a trailing release year off a series title ("Reacher 2022" →
    /// "Reacher" + 2022). The year is metadata for the search query, never
    /// part of it: TMDb has no series called "Reacher 2022". Titles whose last
    /// word is not a 19xx/20xx year are returned unchanged ("Blade Runner 2049").
    /// </summary>
    private static (string Title, int? Year) SplitTrailingYear(string title)
    {
        var index = title.LastIndexOf(' ');
        if (index <= 0 || index == title.Length - 1)
        {
            return (title, null);
        }

        var last = title[(index + 1)..];
        return YearToken.IsMatch(last) && int.TryParse(last, out var year)
            ? (title[..index].TrimEnd(), year)
            : (title, null);
    }

    private ParsedFileName ParseMovie(string name)
    {
        // A tag-carrying file name that consists only of the id ("[tmdb-movie-123].mkv")
        // has no title text; keep the parse usable but flag it unconfident.
        var parenMatch = ParenYear.Match(name);
        string rawTitle;
        int? year = null;

        if (parenMatch.Success)
        {
            year = int.Parse(parenMatch.Value.Trim('(', ')'));
            rawTitle = name[..parenMatch.Index];
        }
        else
        {
            var tokens = Tokenize(name);
            var yearIndex = FindYearTokenIndex(tokens);
            if (yearIndex >= 0)
            {
                year = int.Parse(tokens[yearIndex]);
                rawTitle = string.Join(' ', tokens.Take(yearIndex));
            }
            else
            {
                rawTitle = name;
            }
        }

        var title = CleanTitle(rawTitle, out var cutAtNoise);
        if (title.Length == 0)
        {
            return ParsedFileName.Failed("Could not extract a title from the file name.");
        }

        var confident = title.Length >= 2;
        if (!confident)
        {
            return new ParsedFileName(false, title, year, null, null, false, "Title looks incomplete; verify before import.");
        }

        return new ParsedFileName(false, title, year, null, null, confident,
            cutAtNoise ? null : (year.HasValue ? null : "No release year detected."));
    }

    private static int FindYearTokenIndex(List<string> tokens)
    {
        for (var i = tokens.Count - 1; i >= 0; i--)
        {
            if (YearToken.IsMatch(tokens[i]))
            {
                return i;
            }
        }

        return -1;
    }

    private static string CleanTitle(string raw, out bool cutAtNoise)
    {
        cutAtNoise = false;
        var tokens = Tokenize(raw);

        var kept = new List<string>(tokens.Count);
        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];

            // Year-like tokens that appear inside the pre-year title are real
            // words ("Blade Runner 2049", the film "2012"), so only release
            // noise ends the title here.
            if (NoiseTokens.Contains(token) || Resolution.IsMatch(token))
            {
                cutAtNoise = true;
                break;
            }

            kept.Add(token);
        }

        var title = string.Join(' ', kept).Trim(' ', '-', '_', '.');
        return title;
    }

    private static List<string> Tokenize(string text)
    {
        var cleaned = text.Replace('[', ' ').Replace(']', ' ')
            .Replace('(', ' ').Replace(')', ' ')
            .Replace('{', ' ').Replace('}', ' ');
        return cleaned
            .Split(new[] { ' ', '.', '_', '-', '+', ',', ';', '–', '—' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
    }
}
