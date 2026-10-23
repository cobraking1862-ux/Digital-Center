using System.Globalization;

namespace DigitalCenter.Core.Services;

/// <summary>
/// Turns the language a title was detected in (TMDb reports ISO-639 codes such
/// as "en", "ur", "ko", "pt-BR") into the sub-folder the library is organized
/// by, and resolves that sub-folder against a library root.
///
/// Names come from the runtime's own culture table instead of a hard-coded list,
/// so every language TMDb can report is covered ("English", "Urdu", "Korean",
/// "Portuguese"). A code the runtime does not know is still used as the folder
/// name, because grouping by language is the point of the feature; a missing or
/// empty language never invents a folder — the file then lands in the library
/// root. Folder names are sanitized against the Windows-invalid character set on
/// every platform, so no code or culture name can produce an unusable path.
/// </summary>
public static class LanguageFolder
{
    /// <summary>Characters Windows rejects in a folder name (checked on every platform).</summary>
    private static readonly char[] InvalidFolderChars = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    /// <summary>The folder name for a language code, or null when no language is known.</summary>
    public static string? NameFor(string? languageCode)
    {
        if (string.IsNullOrWhiteSpace(languageCode))
        {
            return null;
        }

        // TMDb sends ISO-639-1 ("en"); be tolerant of "pt-BR" / "zh-Hant".
        var code = languageCode.Trim();
        var separator = code.IndexOfAny(['-', '_']);
        if (separator > 0)
        {
            code = code[..separator];
        }

        if (code.Length == 0)
        {
            return null;
        }

        var name = Sanitize(TryCultureName(code) ?? code);
        return name.Length == 0 ? null : name;
    }

    /// <summary>
    /// The destination folder for a language inside a library root: the language
    /// sub-folder when a language is known, the root itself when it is not.
    /// </summary>
    public static string Resolve(string rootFolder, string? languageCode) =>
        NameFor(languageCode) is { } name
            ? Path.Combine(rootFolder, name)
            : rootFolder;

    /// <summary>
    /// English culture name for a code, or null when the runtime does not know
    /// the code (it then answers with the invariant culture or with
    /// "Unknown Language (xx)", neither of which is a usable folder name).
    /// </summary>
    private static string? TryCultureName(string code)
    {
        try
        {
            var culture = CultureInfo.GetCultureInfo(code);
            if (culture.Equals(CultureInfo.InvariantCulture))
            {
                return null;
            }

            var name = culture.EnglishName;
            return string.IsNullOrWhiteSpace(name) ||
                   name.StartsWith("Unknown Language", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(name, code, StringComparison.OrdinalIgnoreCase)
                ? null
                : name;
        }
        catch (CultureNotFoundException)
        {
            return null;
        }
    }

    /// <summary>Makes a culture name safe to use as one folder level.</summary>
    private static string Sanitize(string name)
    {
        var cleaned = new string(name
            .Select(c => Array.IndexOf(InvalidFolderChars, c) >= 0 ? ' ' : c)
            .ToArray());

        // Culture names can carry a parenthetical ("Chinese (Simplified)");
        // the folder keeps the leading name only.
        var paren = cleaned.IndexOf('(');
        if (paren > 0)
        {
            cleaned = cleaned[..paren];
        }

        cleaned = cleaned.Trim().TrimEnd('.');
        while (cleaned.Contains("  ", StringComparison.Ordinal))
        {
            cleaned = cleaned.Replace("  ", " ", StringComparison.Ordinal);
        }

        return cleaned.Length == 0
            ? string.Empty
            : char.ToUpperInvariant(cleaned[0]) + cleaned[1..];
    }
}
