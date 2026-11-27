using System.Text.RegularExpressions;

namespace DigitalCenter.Core.Services;

/// <summary>
/// Ranks candidate download options against the user's configured default quality,
/// automatically falling back to the next-best lower quality when the preferred
/// tier is unavailable (e.g., 1080p -&gt; 720p -&gt; 480p).
/// </summary>
public static class QualityPreferenceMatcher
{
    private static readonly Regex QualityNumberRegex = new(
        @"\b(2160|1080|720|576|480|360)p\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex FourKRegex = new(
        @"\b(4K|UHD)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Extracts a normalized vertical resolution rank (2160, 1080, 720, 480, 360) or 0 if unknown.</summary>
    public static int ParseQualityRank(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        var m = QualityNumberRegex.Match(text);
        if (m.Success && int.TryParse(m.Groups[1].Value, out var q))
        {
            return q;
        }

        if (FourKRegex.IsMatch(text))
        {
            return 2160;
        }

        return 0;
    }

    public static string NormalizeQualityLabel(string? text)
    {
        var rank = ParseQualityRank(text);
        return rank > 0 ? $"{rank}p" : (text ?? string.Empty).Trim();
    }

    /// <summary>
    /// Selects the best option:
    /// 1. Exact match to preferred quality.
    /// 2. Highest available quality lower than preferred (automatic downward fallback).
    /// 3. Lowest available quality higher than preferred (if only higher tiers exist).
    /// 4. First available option when no quality labels are recognized.
    /// </summary>
    public static DownloadOption? SelectBestOption(
        IReadOnlyList<DownloadOption> options,
        string? preferredQuality)
    {
        if (options is null || options.Count == 0)
        {
            return null;
        }

        var targetRank = ParseQualityRank(preferredQuality);
        if (targetRank <= 0)
        {
            targetRank = 1080;
        }

        var ranked = options
            .Select((opt, idx) => new
            {
                Option = opt,
                Index = idx,
                Rank = ParseQualityRank(opt.QualityLabel)
            })
            .ToList();

        // 1. Exact preferred match
        var exact = ranked.FirstOrDefault(x => x.Rank == targetRank);
        if (exact is not null)
        {
            return exact.Option;
        }

        // 2. Automatic downward fallback: highest quality strictly below preferred
        var bestLower = ranked
            .Where(x => x.Rank > 0 && x.Rank < targetRank)
            .OrderByDescending(x => x.Rank)
            .ThenBy(x => x.Index)
            .FirstOrDefault();
        if (bestLower is not null)
        {
            return bestLower.Option;
        }

        // 3. Only higher qualities available: pick the closest higher tier
        var closestHigher = ranked
            .Where(x => x.Rank > targetRank)
            .OrderBy(x => x.Rank)
            .ThenBy(x => x.Index)
            .FirstOrDefault();
        if (closestHigher is not null)
        {
            return closestHigher.Option;
        }

        // 4. Fallback to first unranked option
        return options[0];
    }
}
