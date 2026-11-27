using DigitalCenter.Core.Configuration;
using DigitalCenter.Core.Tmdb;

namespace DigitalCenter.Core.Services;

/// <summary>Outcome of the automatic metadata match for one Watch Folder file.</summary>
public sealed record MatchOutcome
{
    /// <summary>Chosen TMDb id; null when nothing reliable was found.</summary>
    public long? TmdbId { get; init; }

    public bool IsSeries { get; init; }

    /// <summary>Confidence of the match (0-1); 0 when unmatched.</summary>
    public double Confidence { get; init; }

    /// <summary>Original language code returned by TMDb search, when available.</summary>
    public string? DetectedLanguage { get; init; }

    /// <summary>Operator-readable explanation (why this match, or why none).</summary>
    public string Detail { get; init; } = string.Empty;

    public bool IsMatch => TmdbId.HasValue;

    public static MatchOutcome None(string detail) => new() { TmdbId = null, Confidence = 0, Detail = detail };
}

/// <summary>
/// Automatic metadata matcher for the Watch Folder workflow. External
/// identifiers in the file name win (they pin an exact catalog entry);
/// otherwise the parsed title/year is searched and scored. A candidate below
/// the configured confidence threshold is never accepted automatically — the
/// file is reported as "No Match" and stays in place.
/// </summary>
public sealed class MetadataMatcher
{
    public const double IdentifierConfidence = 0.98;

    private readonly ITmdbMetadataProvider _tmdb;
    private readonly SettingsService _settings;

    public MetadataMatcher(ITmdbMetadataProvider tmdb, SettingsService settings)
    {
        _tmdb = tmdb;
        _settings = settings;
    }

    public async Task<MatchOutcome> MatchAsync(ParsedFileName parsed, CancellationToken ct = default)
    {
        // 1. External identifiers first — they are exact references, not guesses.
        if (parsed.TmdbMovieId.HasValue || parsed.TmdbSeriesId.HasValue)
        {
            var isSeries = parsed.TmdbSeriesId.HasValue;
            var id = parsed.TmdbSeriesId ?? parsed.TmdbMovieId!.Value;
            return new MatchOutcome
            {
                TmdbId = id,
                IsSeries = isSeries,
                Confidence = IdentifierConfidence,
                Detail = $"Matched by TMDb id tag [{(isSeries ? "tmdb-tv" : "tmdb-movie")}-{id}]."
            };
        }

        if (!string.IsNullOrWhiteSpace(parsed.ImdbId))
        {
            var found = await _tmdb.FindByImdbIdAsync(parsed.ImdbId!, ct);
            if (found is not null)
            {
                return new MatchOutcome
                {
                    TmdbId = found.Value.Id,
                    IsSeries = found.Value.IsSeries,
                    Confidence = IdentifierConfidence,
                    Detail = $"Matched by IMDb id {parsed.ImdbId}."
                };
            }

            return MatchOutcome.None($"IMDb id {parsed.ImdbId} is not known to TMDb.");
        }

        // 2. Title/year search.
        if (string.IsNullOrWhiteSpace(parsed.Title))
        {
            return MatchOutcome.None("The file name contains neither an identifier nor a usable title.");
        }

        var threshold = Math.Clamp(_settings.Load().MinimumMatchConfidence, 0.30, 0.95);
        return await MatchByTitleAsync(parsed, threshold, ct);
    }

    private async Task<MatchOutcome> MatchByTitleAsync(ParsedFileName parsed, double threshold, CancellationToken ct)
    {
        var searches = parsed.IsSeries
            ? new[] { (Series: true, (string?)parsed.Title, (int?)null) }
            : new[]
            {
                (Series: false, (string?)parsed.Title, parsed.Year),
                (Series: true, (string?)parsed.Title, parsed.Year)
            };

        var minSimilarity = Math.Min(0.45, threshold);
        MatchOutcome? best = null;
        foreach (var (isSeries, title, year) in searches)
        {
            ct.ThrowIfCancellationRequested();
            var results = isSeries
                ? await _tmdb.SearchSeriesAsync(title!, year, ct)
                : await _tmdb.SearchMoviesAsync(title!, year, ct);

            foreach (var candidate in results)
            {
                var similarity = Math.Max(
                    TitleSimilarity.Score(parsed.Title, candidate.Title),
                    TitleSimilarity.Score(parsed.Title, candidate.OriginalTitle));

                if (similarity < minSimilarity)
                {
                    continue;
                }

                var yearScore = !year.HasValue || !candidate.Year.HasValue
                    ? 0.5
                    : candidate.Year == year.Value ? 1.0
                    : Math.Abs(candidate.Year.Value - year.Value) == 1 ? 0.6
                    : 0.0;

                var score = 0.7 * similarity + 0.3 * yearScore;
                if (score >= threshold && (best is null || score > best.Confidence))
                {
                    best = new MatchOutcome
                    {
                        TmdbId = candidate.Id,
                        IsSeries = isSeries,
                        Confidence = score,
                        DetectedLanguage = candidate.OriginalLanguage,
                        Detail = $"Matched '{candidate.Title}" +
                                 (candidate.Year.HasValue ? $" ({candidate.Year.Value})" : "") +
                                 $"' by title/year (confidence {score:P0})."
                    };
                }
            }
        }

        return best ?? MatchOutcome.None(
            parsed.Year.HasValue
                ? $"No TMDb result for '{parsed.Title}' ({parsed.Year}) reached the {threshold:P0} confidence threshold."
                : $"No TMDb result for '{parsed.Title}' reached the {threshold:P0} confidence threshold.");
    }
}
