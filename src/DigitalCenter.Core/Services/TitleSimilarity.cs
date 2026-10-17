using System.Text.RegularExpressions;

namespace DigitalCenter.Core.Services;

/// <summary>
/// String similarity used by the automatic metadata match. Compares normalized
/// titles (lowercase, punctuation folded) with a blend of Levenshtein distance
/// and token-set overlap, so "The Matrix" matches "Matrix, The" as well as
/// minor punctuation differences.
/// </summary>
public static partial class TitleSimilarity
{
    [GeneratedRegex(@"[^a-z0-9]+")]
    private static partial Regex NonAlphanumeric();

    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        return NonAlphanumeric().Replace(text.ToLowerInvariant(), " ").Trim();
    }

    /// <summary>Similarity between 0 (unrelated) and 1 (identical).</summary>
    public static double Score(string? left, string? right)
    {
        var a = Normalize(left);
        var b = Normalize(right);
        if (a.Length == 0 || b.Length == 0)
        {
            return 0;
        }

        if (string.Equals(a, b, StringComparison.Ordinal))
        {
            return 1;
        }

        var levenshtein = 1.0 - (double)LevenshteinDistance(a, b) / Math.Max(a.Length, b.Length);
        var jaccard = TokenOverlap(a, b);
        return Math.Max(levenshtein, jaccard);
    }

    private static double TokenOverlap(string a, string b)
    {
        var left = a.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var right = b.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (left.Length == 0 || right.Length == 0)
        {
            return 0;
        }

        var common = 0;
        var used = new bool[right.Length];
        foreach (var token in left)
        {
            for (var i = 0; i < right.Length; i++)
            {
                if (!used[i] && string.Equals(token, right[i], StringComparison.Ordinal))
                {
                    used[i] = true;
                    common++;
                    break;
                }
            }
        }

        return (double)common / (left.Length + right.Length - common);
    }

    private static int LevenshteinDistance(string a, string b)
    {
        if (a.Length == 0)
        {
            return b.Length;
        }

        if (b.Length == 0)
        {
            return a.Length;
        }

        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var substitution = previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1);
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), substitution);
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }
}
