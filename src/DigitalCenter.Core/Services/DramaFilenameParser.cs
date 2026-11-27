using System.Text.RegularExpressions;

namespace DigitalCenter.Core.Services;

public sealed record ParsedDramaEpisode(
    string InferredDramaTitle,
    int SeasonNumber,
    int EpisodeNumber,
    string CleanEpisodeTitle,
    string? YouTubeVideoId,
    string? DetectedChannel);

/// <summary>
/// Parses YouTube-downloaded and local drama video filenames (Pakistani, Turkish,
/// Korean, Indian, and Web Series formats) into clean drama titles, season/episode
/// numbers, YouTube video IDs, and channel hints.
/// </summary>
public static class DramaFilenameParser
{
    public static readonly HashSet<string> SupportedVideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".webm", ".avi", ".mov", ".m4v", ".ts", ".wmv", ".flv"
    };

    public static readonly HashSet<string> SupportedPosterExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp"
    };

    private static readonly Regex YouTubeIdBracketRegex = new(
        @"\[([A-Za-z0-9_-]{11})\]\s*$",
        RegexOptions.Compiled);

    private static readonly Regex SeasonEpisodeRegex = new(
        @"(?:S(?:eason)?\s*0*(\d{1,2})\s*[-_.\s]*E(?:p(?:isode)?)?\.?\s*0*(\d{1,4}))|(?:\b(\d{1,2})x0*(\d{1,4})\b)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex SeasonFolderRegex = new(
        @"^S(?:eason)?\s*[-_. ]*0*(\d{1,2})$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Match optional modifiers right before "Episode / Ep / Qist / Bolum" (e.g. "Tapish Last Episode 33", "Tapish 2nd Last Episode 32")
    private static readonly Regex EpisodeWordRegex = new(
        @"\b(?:(?:2nd\s+Last|Second\s+Last|3rd\s+Last|Third\s+Last|Grand\s+Finale|Grand|Mega|Final|Last|Special|Bonus|Full|Complete|New|Latest)\s+)*(?:Episode|Ep\.?|Epi\.?|Qist|Bolum|Bölüm|Part)\s*[-_#:. ]*0*(\d{1,4})\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex StandaloneNumberRegex = new(
        @"(?:^|[\s\-_#])0*(\d{1,4})(?:\s*$|\s*[\(\[\-|])",
        RegexOptions.Compiled);

    private static readonly Regex SpecialTagRegex = new(
        @"\b(2nd\s+Last\s+Episode|Second\s+Last\s+Episode|Last\s+Episode|Grand\s+Finale|Final\s+Episode|Mega\s+Episode)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Strip trailing release/episode modifiers accidentally left on inferred drama names
    private static readonly Regex TrailingModifierRegex = new(
        @"(?:\s*[-_|–—]?\s*\b(?:2nd\s+Last|Second\s+Last|3rd\s+Last|Third\s+Last|Grand\s+Finale|Grand|Mega|Final|Last|Special|Bonus|Teaser|Promo|OST|Full|Complete|New|Latest|HD|UHD|4K|1080p|720p|480p|Urdu\s+Dubbed|Hindi\s+Dubbed|Dubbed|Subbed|Eng\s+Sub)\b)+\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly (string Pattern, string Channel)[] KnownChannels =
    [
        ("ARY Digital", "ARY Digital"),
        ("HUM TV", "HUM TV"),
        ("Har Pal Geo", "Har Pal Geo"),
        ("Geo Entertainment", "Geo Entertainment"),
        ("Green TV", "Green TV Entertainment"),
        ("Express TV", "Express TV"),
        ("Aaj Entertainment", "Aaj Entertainment"),
        ("SAB TV", "Sony SAB"),
        ("Colors TV", "Colors TV"),
        ("StarPlus", "StarPlus"),
        ("Zee TV", "Zee TV"),
        ("ATV", "ATV"),
        ("TRT", "TRT")
    ];

    public static bool IsVideoFile(string path) =>
        SupportedVideoExtensions.Contains(Path.GetExtension(path));

    public static int? TryParseSeasonFolder(string? folderName)
    {
        if (string.IsNullOrWhiteSpace(folderName))
        {
            return null;
        }

        var m = SeasonFolderRegex.Match(folderName.Trim());
        return m.Success && int.TryParse(m.Groups[1].Value, out var s) && s > 0 ? s : null;
    }

    public static ParsedDramaEpisode Parse(string fileName, string? dramaFolderName = null, int? seasonHint = null)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName).Trim();
        string? videoId = null;

        var ytMatch = YouTubeIdBracketRegex.Match(stem);
        if (ytMatch.Success)
        {
            videoId = ytMatch.Groups[1].Value;
            stem = stem[..ytMatch.Index].Trim();
        }

        string? channel = null;
        foreach (var (pattern, ch) in KnownChannels)
        {
            if (stem.Contains(pattern, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrWhiteSpace(dramaFolderName) &&
                 dramaFolderName.Contains(pattern, StringComparison.OrdinalIgnoreCase)))
            {
                channel = ch;
                break;
            }
        }

        var season = seasonHint.GetValueOrDefault(1);
        if (season <= 0) season = 1;
        var episode = 0;
        var matchIndex = -1;

        var seMatch = SeasonEpisodeRegex.Match(stem);
        if (seMatch.Success)
        {
            matchIndex = seMatch.Index;
            if (seMatch.Groups[1].Success)
            {
                season = int.Parse(seMatch.Groups[1].Value);
                episode = int.Parse(seMatch.Groups[2].Value);
            }
            else
            {
                season = int.Parse(seMatch.Groups[3].Value);
                episode = int.Parse(seMatch.Groups[4].Value);
            }
        }
        else
        {
            var epMatch = EpisodeWordRegex.Match(stem);
            if (epMatch.Success)
            {
                matchIndex = epMatch.Index;
                episode = int.Parse(epMatch.Groups[1].Value);
            }
            else
            {
                var numMatch = StandaloneNumberRegex.Match(stem);
                if (numMatch.Success && int.TryParse(numMatch.Groups[1].Value, out var parsedNum) &&
                    parsedNum is > 0 and <= 1500 && parsedNum is < 1950 or > 2035)
                {
                    matchIndex = numMatch.Index;
                    episode = parsedNum;
                }
            }
        }

        var specialMatch = SpecialTagRegex.Match(stem);
        var specialTag = specialMatch.Success ? NormalizeWhitespace(specialMatch.Groups[1].Value) : null;

        string inferredTitle;
        if (!string.IsNullOrWhiteSpace(dramaFolderName))
        {
            inferredTitle = CleanDramaName(dramaFolderName);
        }
        else if (matchIndex > 0)
        {
            inferredTitle = CleanDramaName(stem[..matchIndex]);
        }
        else
        {
            var beforePipe = stem.Split(['|', '｜'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? stem;
            inferredTitle = CleanDramaName(beforePipe);
        }

        if (string.IsNullOrWhiteSpace(inferredTitle))
        {
            inferredTitle = CleanTitle(stem);
        }

        string cleanEpTitle;
        if (episode > 0)
        {
            cleanEpTitle = $"Episode {episode:00}";
            if (!string.IsNullOrWhiteSpace(specialTag))
            {
                cleanEpTitle += $" ({specialTag})";
            }
        }
        else
        {
            cleanEpTitle = CleanTitle(stem);
        }

        return new ParsedDramaEpisode(
            InferredDramaTitle: inferredTitle,
            SeasonNumber: season,
            EpisodeNumber: episode,
            CleanEpisodeTitle: cleanEpTitle,
            YouTubeVideoId: videoId,
            DetectedChannel: channel);
    }

    /// <summary>Builds a clean, consistent filename for an imported drama episode.</summary>
    public static string BuildCleanEpisodeFileName(string dramaName, ParsedDramaEpisode parsed, string originalFilePath)
    {
        var ext = Path.GetExtension(originalFilePath);
        var safeDrama = PathPlanner.SanitizeName(dramaName, 70);
        if (parsed.EpisodeNumber > 0)
        {
            return parsed.SeasonNumber > 1
                ? $"{safeDrama} - S{parsed.SeasonNumber:00}E{parsed.EpisodeNumber:00}{ext}"
                : $"{safeDrama} - Episode {parsed.EpisodeNumber:00}{ext}";
        }

        var safeTitle = PathPlanner.SanitizeName(parsed.CleanEpisodeTitle, 70);
        return string.IsNullOrWhiteSpace(safeTitle)
            ? Path.GetFileName(originalFilePath)
            : $"{safeDrama} - {safeTitle}{ext}";
    }

    public static string? FindPosterInFolder(string folderPath)
    {
        if (!Directory.Exists(folderPath))
        {
            return null;
        }

        string[] preferredNames = ["poster", "cover", "folder", "thumb", "thumbnail"];
        try
        {
            var images = Directory.EnumerateFiles(folderPath)
                .Where(f => SupportedPosterExtensions.Contains(Path.GetExtension(f)))
                .ToList();

            foreach (var pref in preferredNames)
            {
                var match = images.FirstOrDefault(f =>
                    string.Equals(Path.GetFileNameWithoutExtension(f), pref, StringComparison.OrdinalIgnoreCase));
                if (match is not null)
                {
                    return match;
                }
            }

            return images.FirstOrDefault();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string CleanDramaName(string raw)
    {
        var cleaned = CleanTitle(raw);
        var stripped = TrailingModifierRegex.Replace(cleaned, string.Empty);
        var result = CleanTitle(stripped);
        return string.IsNullOrWhiteSpace(result) ? cleaned : result;
    }

    private static string CleanTitle(string raw)
    {
        var s = raw
            .Replace('_', ' ')
            .Replace('.', ' ')
            .Trim(' ', '-', '|', '｜', ':', '–', '—', '[', ']');
        return NormalizeWhitespace(s);
    }

    private static string NormalizeWhitespace(string input) =>
        Regex.Replace(input.Trim(), @"\s+", " ");
}
