using System.Globalization;
using System.Text.RegularExpressions;

namespace DigitalCenter.Core.Services;

/// <summary>
/// Resolves regional library sub-folder names ("Hollywood", "Bollywood",
/// "Chinese", "South Indian") and human-readable language display names from
/// TMDb ISO-639-1 codes, filename tokens, or production country metadata.
/// Used by the Watch Folder auto-organizer to move incoming media into its
/// regional sub-folder under the library root (creating the sub-folder if missing).
/// </summary>
public static partial class LanguageResolver
{
    public const string DefaultLanguageFolder = "Unknown";
    public const string HollywoodFolder = "Hollywood";
    public const string BollywoodFolder = "Bollywood";
    public const string ChineseFolder = "Chinese";
    public const string SouthIndianFolder = "South Indian";

    private static readonly Dictionary<string, string> IsoToLanguageName = new(StringComparer.OrdinalIgnoreCase)
    {
        // European & English languages (-> Hollywood)
        ["en"] = "English",
        ["eng"] = "English",
        ["en-gb"] = "English",
        ["en-us"] = "English",
        ["uk"] = "Ukrainian",
        ["ukr"] = "Ukrainian",
        ["es"] = "Spanish",
        ["spa"] = "Spanish",
        ["fr"] = "French",
        ["fra"] = "French",
        ["fre"] = "French",
        ["de"] = "German",
        ["deu"] = "German",
        ["ger"] = "German",
        ["it"] = "Italian",
        ["ita"] = "Italian",
        ["ru"] = "Russian",
        ["rus"] = "Russian",
        ["pt"] = "Portuguese",
        ["por"] = "Portuguese",
        ["pl"] = "Polish",
        ["pol"] = "Polish",
        ["nl"] = "Dutch",
        ["nld"] = "Dutch",
        ["dut"] = "Dutch",
        ["sv"] = "Swedish",
        ["swe"] = "Swedish",
        ["da"] = "Danish",
        ["dan"] = "Danish",
        ["no"] = "Norwegian",
        ["nor"] = "Norwegian",
        ["nb"] = "Norwegian",
        ["nn"] = "Norwegian",
        ["fi"] = "Finnish",
        ["fin"] = "Finnish",
        ["el"] = "Greek",
        ["ell"] = "Greek",
        ["gre"] = "Greek",
        ["cs"] = "Czech",
        ["ces"] = "Czech",
        ["cze"] = "Czech",
        ["hu"] = "Hungarian",
        ["hun"] = "Hungarian",
        ["ro"] = "Romanian",
        ["ron"] = "Romanian",
        ["rum"] = "Romanian",
        ["bg"] = "Bulgarian",
        ["bul"] = "Bulgarian",
        ["sr"] = "Serbian",
        ["srp"] = "Serbian",
        ["hr"] = "Croatian",
        ["hrv"] = "Croatian",
        ["sk"] = "Slovak",
        ["slk"] = "Slovak",
        ["slo"] = "Slovak",
        ["sl"] = "Slovenian",
        ["slv"] = "Slovenian",
        ["lt"] = "Lithuanian",
        ["lit"] = "Lithuanian",
        ["lv"] = "Latvian",
        ["lav"] = "Latvian",
        ["et"] = "Estonian",
        ["est"] = "Estonian",
        ["is"] = "Icelandic",
        ["isl"] = "Icelandic",
        ["ice"] = "Icelandic",
        ["ga"] = "Irish",
        ["gle"] = "Irish",
        ["ca"] = "Catalan",
        ["cat"] = "Catalan",
        ["be"] = "Belarusian",
        ["bel"] = "Belarusian",
        ["sq"] = "Albanian",
        ["sqi"] = "Albanian",
        ["mk"] = "Macedonian",
        ["mkd"] = "Macedonian",
        ["bs"] = "Bosnian",
        ["bos"] = "Bosnian",

        // Hindi (-> Bollywood)
        ["hi"] = "Hindi",
        ["hin"] = "Hindi",

        // East & Southeast Asian languages (-> Chinese)
        ["ko"] = "Korean",
        ["kor"] = "Korean",
        ["ja"] = "Japanese",
        ["jpn"] = "Japanese",
        ["zh"] = "Chinese",
        ["zho"] = "Chinese",
        ["chi"] = "Chinese",
        ["cn"] = "Chinese",
        ["yue"] = "Chinese",
        ["cmn"] = "Chinese",
        ["wuu"] = "Chinese",
        ["tl"] = "Filipino",
        ["tgl"] = "Filipino",
        ["fil"] = "Filipino",
        ["id"] = "Indonesian",
        ["ind"] = "Indonesian",
        ["ms"] = "Malay",
        ["msa"] = "Malay",
        ["may"] = "Malay",
        ["th"] = "Thai",
        ["tha"] = "Thai",
        ["vi"] = "Vietnamese",
        ["vie"] = "Vietnamese",
        ["km"] = "Khmer",
        ["khm"] = "Khmer",
        ["my"] = "Burmese",
        ["mya"] = "Burmese",
        ["lo"] = "Lao",
        ["lao"] = "Lao",

        // South Indian languages (-> South Indian)
        ["ta"] = "Tamil",
        ["tam"] = "Tamil",
        ["te"] = "Telugu",
        ["tel"] = "Telugu",
        ["ml"] = "Malayalam",
        ["mal"] = "Malayalam",
        ["kn"] = "Kannada",
        ["kan"] = "Kannada",

        // Other languages
        ["ur"] = "Urdu",
        ["urd"] = "Urdu",
        ["ar"] = "Arabic",
        ["ara"] = "Arabic",
        ["tr"] = "Turkish",
        ["tur"] = "Turkish",
        ["pa"] = "Punjabi",
        ["pan"] = "Punjabi",
        ["bn"] = "Bengali",
        ["ben"] = "Bengali",
        ["fa"] = "Persian",
        ["per"] = "Persian",
        ["fas"] = "Persian",
        ["he"] = "Hebrew",
        ["heb"] = "Hebrew"
    };

    private static readonly Dictionary<string, string> FilenameTokenToLanguage = new(StringComparer.OrdinalIgnoreCase)
    {
        // European & English tokens
        ["english"] = "English",
        ["eng"] = "English",
        ["ukenglish"] = "English",
        ["british"] = "English",
        ["hollywood"] = "English",
        ["spanish"] = "Spanish",
        ["espanol"] = "Spanish",
        ["castellano"] = "Spanish",
        ["latino"] = "Spanish",
        ["french"] = "French",
        ["frech"] = "French",
        ["vostfr"] = "French",
        ["truefrench"] = "French",
        ["german"] = "German",
        ["germa"] = "German",
        ["deutsch"] = "German",
        ["italian"] = "Italian",
        ["italiano"] = "Italian",
        ["russian"] = "Russian",
        ["portuguese"] = "Portuguese",
        ["portugese"] = "Portuguese",
        ["polish"] = "Polish",
        ["dutch"] = "Dutch",
        ["swedish"] = "Swedish",
        ["danish"] = "Danish",
        ["norwegian"] = "Norwegian",
        ["finnish"] = "Finnish",
        ["greek"] = "Greek",
        ["czech"] = "Czech",
        ["hungarian"] = "Hungarian",
        ["romanian"] = "Romanian",
        ["ukrainian"] = "Ukrainian",
        ["bulgarian"] = "Bulgarian",
        ["serbian"] = "Serbian",
        ["croatian"] = "Croatian",
        ["slovak"] = "Slovak",
        ["slovenian"] = "Slovenian",

        // Hindi / Bollywood tokens
        ["hindi"] = "Hindi",
        ["hin"] = "Hindi",
        ["bollywood"] = "Hindi",

        // Asian tokens (-> Chinese folder)
        ["korean"] = "Korean",
        ["kor"] = "Korean",
        ["japanese"] = "Japanese",
        ["japanes"] = "Japanese",
        ["jpn"] = "Japanese",
        ["chinese"] = "Chinese",
        ["mandarin"] = "Chinese",
        ["cantonese"] = "Chinese",
        ["tagalog"] = "Filipino",
        ["filipino"] = "Filipino",
        ["philippine"] = "Filipino",
        ["philippines"] = "Filipino",
        ["phiphine"] = "Filipino",
        ["indonesian"] = "Indonesian",
        ["indonesia"] = "Indonesian",
        ["indonesi"] = "Indonesian",
        ["malay"] = "Malay",
        ["malaysian"] = "Malay",
        ["malaysia"] = "Malay",
        ["malysia"] = "Malay",
        ["thai"] = "Thai",
        ["vietnamese"] = "Vietnamese",

        // South Indian tokens (-> South Indian folder)
        ["tamil"] = "Tamil",
        ["tam"] = "Tamil",
        ["telugu"] = "Telugu",
        ["telgugu"] = "Telugu",
        ["tel"] = "Telugu",
        ["malayalam"] = "Malayalam",
        ["malyalam"] = "Malayalam",
        ["mal"] = "Malayalam",
        ["kannada"] = "Kannada",
        ["kan"] = "Kannada",
        ["southindian"] = "Tamil",

        // Other tokens
        ["urdu"] = "Urdu",
        ["arabic"] = "Arabic",
        ["turkish"] = "Turkish",
        ["punjabi"] = "Punjabi",
        ["bengali"] = "Bengali",
        ["persian"] = "Persian",
        ["farsi"] = "Persian"
    };

    private static readonly Dictionary<string, string> CountryToLanguage = new(StringComparer.OrdinalIgnoreCase)
    {
        // English & European countries (-> Hollywood)
        ["United States of America"] = "English",
        ["United States"] = "English",
        ["USA"] = "English",
        ["US"] = "English",
        ["United Kingdom"] = "English",
        ["UK"] = "English",
        ["Great Britain"] = "English",
        ["England"] = "English",
        ["Canada"] = "English",
        ["Australia"] = "English",
        ["New Zealand"] = "English",
        ["Ireland"] = "English",
        ["France"] = "French",
        ["Belgium"] = "French",
        ["Spain"] = "Spanish",
        ["Mexico"] = "Spanish",
        ["Argentina"] = "Spanish",
        ["Colombia"] = "Spanish",
        ["Chile"] = "Spanish",
        ["Germany"] = "German",
        ["Austria"] = "German",
        ["Switzerland"] = "German",
        ["Italy"] = "Italian",
        ["Russia"] = "Russian",
        ["Soviet Union"] = "Russian",
        ["Portugal"] = "Portuguese",
        ["Brazil"] = "Portuguese",
        ["Poland"] = "Polish",
        ["Netherlands"] = "Dutch",
        ["Sweden"] = "Swedish",
        ["Denmark"] = "Danish",
        ["Norway"] = "Norwegian",
        ["Finland"] = "Finnish",
        ["Greece"] = "Greek",
        ["Czech Republic"] = "Czech",
        ["Czechia"] = "Czech",
        ["Hungary"] = "Hungarian",
        ["Romania"] = "Romanian",
        ["Ukraine"] = "Ukrainian",

        // India (-> Hindi / Bollywood when not South Indian)
        ["India"] = "Hindi",

        // East & Southeast Asian countries (-> Chinese folder)
        ["South Korea"] = "Korean",
        ["Korea"] = "Korean",
        ["Republic of Korea"] = "Korean",
        ["Japan"] = "Japanese",
        ["China"] = "Chinese",
        ["Hong Kong"] = "Chinese",
        ["Taiwan"] = "Chinese",
        ["Singapore"] = "Chinese",
        ["Philippines"] = "Filipino",
        ["Indonesia"] = "Indonesian",
        ["Malaysia"] = "Malay",
        ["Thailand"] = "Thai",
        ["Vietnam"] = "Vietnamese",

        // Other countries
        ["Pakistan"] = "Urdu",
        ["Turkey"] = "Turkish",
        ["Saudi Arabia"] = "Arabic",
        ["Egypt"] = "Arabic",
        ["Iran"] = "Persian"
    };

    private static readonly HashSet<string> EuropeanLanguages = new(StringComparer.OrdinalIgnoreCase)
    {
        "English", "UK English", "British", "French", "Spanish", "Portuguese",
        "German", "Russian", "Italian", "Dutch", "Polish", "Swedish",
        "Danish", "Norwegian", "Finnish", "Greek", "Czech", "Hungarian",
        "Romanian", "Ukrainian", "Bulgarian", "Serbian", "Croatian",
        "Slovak", "Slovenian", "Lithuanian", "Latvian", "Estonian",
        "Icelandic", "Irish", "Catalan", "Belarusian", "Albanian",
        "Macedonian", "Bosnian", "Hollywood"
    };

    private static readonly HashSet<string> BollywoodLanguages = new(StringComparer.OrdinalIgnoreCase)
    {
        "Hindi", "Bollywood", "Hindustani"
    };

    private static readonly HashSet<string> AsianLanguages = new(StringComparer.OrdinalIgnoreCase)
    {
        "Korean", "Chinese", "Mandarin", "Cantonese", "Japanese",
        "Filipino", "Tagalog", "Philippine", "Philippines",
        "Indonesian", "Indonesia", "Malay", "Malaysian", "Malaysia",
        "Thai", "Vietnamese", "Khmer", "Burmese", "Lao"
    };

    private static readonly HashSet<string> SouthIndianLanguages = new(StringComparer.OrdinalIgnoreCase)
    {
        "Tamil", "Telugu", "Malayalam", "Kannada", "South Indian"
    };

    private static readonly HashSet<string> KnownCategoryFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        HollywoodFolder,
        BollywoodFolder,
        ChineseFolder,
        SouthIndianFolder
    };

    [GeneratedRegex(@"[^a-zA-Z0-9]+")]
    private static partial Regex TokenSplitter();

    /// <summary>
    /// Resolves the target regional library folder name for Watch Folder auto-organization:
    /// - European languages (English, French, Spanish, Portuguese, German, Russian, Italian, etc.) → "Hollywood"
    /// - Hindi → "Bollywood"
    /// - Asian languages (Korean, Chinese, Japanese, Filipino/Tagalog/Philippine, Indonesian, Malay/Malaysian, Thai, Vietnamese) → "Chinese"
    /// - South Indian languages (Tamil, Telugu, Malayalam, Kannada) → "South Indian"
    /// - Other known languages → their normalized language name
    /// - "Unknown" when no signal is present.
    /// </summary>
    public static string Resolve(
        string? originalLanguage,
        string? sourceFileName = null,
        IReadOnlyList<string>? countries = null)
    {
        var languageName = ResolveLanguageName(originalLanguage, sourceFileName, countries);
        if (string.IsNullOrWhiteSpace(languageName) ||
            string.Equals(languageName, DefaultLanguageFolder, StringComparison.OrdinalIgnoreCase))
        {
            return DefaultLanguageFolder;
        }

        return MapToCategoryFolder(languageName);
    }

    /// <summary>
    /// Resolves the specific human-readable language display name (e.g. "French",
    /// "Hindi", "Korean", "Tamil", "English") from TMDb language codes, filename tokens,
    /// or production countries.
    /// </summary>
    public static string ResolveLanguageName(
        string? originalLanguage,
        string? sourceFileName = null,
        IReadOnlyList<string>? countries = null)
    {
        var fromCode = FromLanguageCodeOrName(originalLanguage);
        var fromFile = FromFileName(sourceFileName);

        // If the filename explicitly tags a South Indian language while TMDb or country says Hindi/India,
        // prefer the specific South Indian language tag from the filename.
        if (!string.IsNullOrEmpty(fromFile) && SouthIndianLanguages.Contains(fromFile))
        {
            return fromFile;
        }

        if (!string.IsNullOrEmpty(fromCode))
        {
            return fromCode;
        }

        if (!string.IsNullOrEmpty(fromFile))
        {
            return fromFile;
        }

        if (countries is { Count: > 0 })
        {
            foreach (var country in countries)
            {
                if (!string.IsNullOrWhiteSpace(country) &&
                    CountryToLanguage.TryGetValue(country.Trim(), out var mapped))
                {
                    return mapped;
                }
            }
        }

        return DefaultLanguageFolder;
    }

    /// <summary>
    /// Maps a language name or regional folder label to the target library folder:
    /// Hollywood, Bollywood, Chinese, or South Indian.
    /// </summary>
    public static string MapToCategoryFolder(string? languageOrCode)
    {
        if (string.IsNullOrWhiteSpace(languageOrCode))
        {
            return DefaultLanguageFolder;
        }

        var trimmed = languageOrCode.Trim();
        if (KnownCategoryFolders.TryGetValue(trimmed, out var canonicalFolder))
        {
            return canonicalFolder;
        }

        var normalized = FromLanguageCodeOrName(trimmed) ?? trimmed;

        if (SouthIndianLanguages.Contains(normalized) ||
            trimmed.Equals("south indian", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("southindian", StringComparison.OrdinalIgnoreCase))
        {
            return SouthIndianFolder;
        }

        if (BollywoodLanguages.Contains(normalized))
        {
            return BollywoodFolder;
        }

        if (AsianLanguages.Contains(normalized))
        {
            return ChineseFolder;
        }

        if (EuropeanLanguages.Contains(normalized))
        {
            return HollywoodFolder;
        }

        return normalized;
    }

    /// <summary>Converts an ISO-639 code or raw language string into a clean display name.</summary>
    public static string? FromLanguageCodeOrName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var trimmed = raw.Trim();
        if (KnownCategoryFolders.TryGetValue(trimmed, out var categoryFolder))
        {
            return categoryFolder;
        }

        if (trimmed.Equals("south indian", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("southindian", StringComparison.OrdinalIgnoreCase))
        {
            return SouthIndianFolder;
        }

        if (trimmed.Equals("uk english", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("us english", StringComparison.OrdinalIgnoreCase))
        {
            return "English";
        }

        var primarySubtag = trimmed.Split('-', '_')[0];

        if (IsoToLanguageName.TryGetValue(trimmed, out var exact))
        {
            return exact;
        }

        if (IsoToLanguageName.TryGetValue(primarySubtag, out var bySubtag))
        {
            return bySubtag;
        }

        if (FilenameTokenToLanguage.TryGetValue(trimmed, out var byToken))
        {
            return byToken;
        }

        if (trimmed.Length > 2 && trimmed.All(c => char.IsLetter(c) || c == ' '))
        {
            var sanitized = PathPlanner.SanitizeName(
                CultureInfo.InvariantCulture.TextInfo.ToTitleCase(trimmed.ToLowerInvariant()), 20);
            return sanitized;
        }

        return null;
    }

    /// <summary>Extracts an explicit language tag from a media filename if one is present.</summary>
    public static string? FromFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        var stem = Path.GetFileNameWithoutExtension(fileName);
        if (string.IsNullOrWhiteSpace(stem))
        {
            return null;
        }

        var tokens = TokenSplitter().Split(stem);
        // Check from the end of the filename (where release tags like KOREAN.1080p sit),
        // skipping the very first token so a movie titled "French" or "English" isn't misread.
        for (var i = tokens.Length - 1; i >= 1; i--)
        {
            var token = tokens[i];
            if (token.Length >= 3 && FilenameTokenToLanguage.TryGetValue(token, out var language))
            {
                return language;
            }
        }

        return null;
    }

    /// <summary>True when a folder name matches a known language or regional category folder name.</summary>
    public static bool IsKnownLanguageName(string? folderName) =>
        !string.IsNullOrWhiteSpace(folderName) &&
        (KnownCategoryFolders.Contains(folderName.Trim()) ||
         FilenameTokenToLanguage.ContainsKey(folderName.Trim()) ||
         IsoToLanguageName.Values.Contains(folderName.Trim(), StringComparer.OrdinalIgnoreCase));
}


/// <summary>
/// Resolves and normalizes movie collection / franchise folder names (e.g.
/// "Harry Potter" from TMDb's "Harry Potter Collection", or from multi-part
/// movie filenames like "Harry Potter Part 1" or parent collection folders).
/// Movie collections are treated as separate entities organized into
/// &lt;CollectionsRoot&gt;/&lt;CollectionName&gt; (or &lt;LibraryRoot&gt;/Collections/&lt;CollectionName&gt;)
/// rather than regional language folders.
/// </summary>
public static partial class CollectionResolver
{
    public const string DefaultCollectionsFolder = "Collections";

    [GeneratedRegex(@"\s*(?:-\s*)?\b(?:Collection|Film\s+Series|Movie\s+Series|Anthology|Saga|Trilogy|Tetralogy|Quadrilogy|Pentalogy|Hexalogy|Franchise)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex CollectionSuffixPattern();

    [GeneratedRegex(@"^(?<base>.+?)\s*(?:[-:–—]|\s+)\s*(?:(?:Part|Pt\.?|Chapter|Vol\.?|Volume|Book|Episode|Film|Movie)\s*(?<num>\d+|[IVX]+)|(?<num2>[2-9]|10)\b)(?:\s*[-:–—].*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex MultiPartTitlePattern();

    private static readonly (string Prefix, string CollectionName)[] KnownFranchises =
    [
        ("Harry Potter", "Harry Potter"),
        ("Fantastic Beasts", "Fantastic Beasts"),
        ("The Lord of the Rings", "The Lord of the Rings"),
        ("Lord of the Rings", "The Lord of the Rings"),
        ("The Hobbit", "The Hobbit"),
        ("Star Wars", "Star Wars"),
        ("John Wick", "John Wick"),
        ("Mission Impossible", "Mission Impossible"),
        ("Fast & Furious", "Fast and Furious"),
        ("Fast and Furious", "Fast and Furious"),
        ("The Fast and the Furious", "Fast and Furious"),
        ("Spider Man", "Spider-Man"),
        ("Spider-Man", "Spider-Man"),
        ("Avengers", "The Avengers"),
        ("The Avengers", "The Avengers"),
        ("Iron Man", "Iron Man"),
        ("Captain America", "Captain America"),
        ("Thor ", "Thor"),
        ("Guardians of the Galaxy", "Guardians of the Galaxy"),
        ("Jurassic Park", "Jurassic Park"),
        ("Jurassic World", "Jurassic Park"),
        ("Pirates of the Caribbean", "Pirates of the Caribbean"),
        ("Transformers", "Transformers"),
        ("The Matrix", "The Matrix"),
        ("Toy Story", "Toy Story"),
        ("Shrek", "Shrek"),
        ("Kung Fu Panda", "Kung Fu Panda"),
        ("How to Train Your Dragon", "How to Train Your Dragon"),
        ("Despicable Me", "Despicable Me"),
        ("Ice Age", "Ice Age"),
        ("Madagascar", "Madagascar"),
        ("The Hunger Games", "The Hunger Games"),
        ("Hunger Games", "The Hunger Games"),
        ("Twilight", "The Twilight Saga"),
        ("The Twilight Saga", "The Twilight Saga"),
        ("Maze Runner", "The Maze Runner"),
        ("The Maze Runner", "The Maze Runner"),
        ("Divergent", "Divergent"),
        ("James Bond", "James Bond"),
        ("Indiana Jones", "Indiana Jones"),
        ("Back to the Future", "Back to the Future"),
        ("Terminator", "Terminator"),
        ("Alien ", "Alien"),
        ("Predator", "Predator"),
        ("Die Hard", "Die Hard"),
        ("Lethal Weapon", "Lethal Weapon"),
        ("Rocky ", "Rocky"),
        ("Creed ", "Creed"),
        ("Rambo", "Rambo"),
        ("The Expendables", "The Expendables"),
        ("Expendables", "The Expendables"),
        ("The Godfather", "The Godfather"),
        ("Godfather", "The Godfather"),
        ("Ocean's ", "Ocean's"),
        ("Oceans ", "Ocean's"),
        ("Bourne ", "The Bourne"),
        ("The Bourne", "The Bourne"),
        ("Taken ", "Taken"),
        ("The Equalizer", "The Equalizer"),
        ("Equalizer", "The Equalizer"),
        ("Sherlock Holmes", "Sherlock Holmes"),
        ("Now You See Me", "Now You See Me"),
        ("Kingsman", "Kingsman"),
        ("Men in Black", "Men in Black"),
        ("Ghostbusters", "Ghostbusters"),
        ("Jumanji", "Jumanji"),
        ("Sonic the Hedgehog", "Sonic the Hedgehog"),
        ("Deadpool", "Deadpool"),
        ("X-Men", "X-Men"),
        ("X Men", "X-Men"),
        ("Venom", "Venom"),
        ("Batman", "Batman"),
        ("The Dark Knight", "The Dark Knight"),
        ("Superman", "Superman"),
        ("Wonder Woman", "Wonder Woman"),
        ("Aquaman", "Aquaman"),
        ("Avatar:", "Avatar"),
        ("Dune:", "Dune"),
        ("Dune Part", "Dune"),
        ("Planet of the Apes", "Planet of the Apes"),
        ("A Quiet Place", "A Quiet Place"),
        ("The Conjuring", "The Conjuring"),
        ("Insidious", "Insidious"),
        ("Saw ", "Saw"),
        ("Scream ", "Scream"),
        ("Final Destination", "Final Destination"),
        ("Resident Evil", "Resident Evil"),
        ("Underworld", "Underworld"),
        ("Blade ", "Blade"),
        ("Rush Hour", "Rush Hour"),
        ("Bad Boys", "Bad Boys"),
        ("Beverly Hills Cop", "Beverly Hills Cop"),
        ("Home Alone", "Home Alone"),
        ("Night at the Museum", "Night at the Museum"),
        ("The Hangover", "The Hangover"),
        ("Hangover", "The Hangover"),
        ("Pitch Perfect", "Pitch Perfect"),
        ("High School Musical", "High School Musical"),
        ("Cars ", "Cars"),
        ("Finding Nemo", "Finding Nemo"),
        ("Finding Dory", "Finding Nemo"),
        ("The Incredibles", "The Incredibles"),
        ("Incredibles", "The Incredibles"),
        ("Monsters, Inc", "Monsters, Inc."),
        ("Monsters University", "Monsters, Inc."),
        ("Frozen ", "Frozen"),
        ("Moana ", "Moana"),
        ("Zootopia", "Zootopia"),
        ("Paddington", "Paddington"),
        ("Bridget Jones", "Bridget Jones"),
        ("Johnny English", "Johnny English"),
        ("Ip Man", "Ip Man"),
        ("The Raid", "The Raid"),
        ("Ong Bak", "Ong-Bak"),
        ("Baahubali", "Baahubali"),
        ("Bahubali", "Baahubali"),
        ("K.G.F", "K.G.F"),
        ("KGF", "K.G.F"),
        ("Pushpa", "Pushpa"),
        ("Dhoom", "Dhoom"),
        ("Don ", "Don"),
        ("Krrish", "Krrish"),
        ("Golmaal", "Golmaal"),
        ("Housefull", "Housefull"),
        ("Singham", "Singham"),
        ("Dabangg", "Dabangg"),
        ("Tiger Zinda Hai", "Tiger"),
        ("Ek Tha Tiger", "Tiger"),
        ("Tiger 3", "Tiger"),
        ("Drishyam", "Drishyam"),
        ("Gangs of Wasseypur", "Gangs of Wasseypur"),
        ("Hera Pheri", "Hera Pheri"),
        ("Phir Hera Pheri", "Hera Pheri"),
        ("Welcome ", "Welcome"),
        ("Bhool Bhulaiyaa", "Bhool Bhulaiyaa"),
        ("Stree ", "Stree"),
        ("Munna Bhai", "Munna Bhai")
    ];

    /// <summary>
    /// Cleans a TMDb or user collection name into a clean folder name, e.g.
    /// "Harry Potter Collection" → "Harry Potter",
    /// "The Dark Knight Collection" → "The Dark Knight".
    /// </summary>
    public static string? NormalizeCollectionName(string? rawCollectionName)
    {
        if (string.IsNullOrWhiteSpace(rawCollectionName))
        {
            return null;
        }

        var trimmed = rawCollectionName.Trim();
        var stripped = CollectionSuffixPattern().Replace(trimmed, string.Empty).Trim(' ', '-', ':', '–', '—', '.');
        if (string.IsNullOrWhiteSpace(stripped))
        {
            stripped = trimmed;
        }

        return PathPlanner.SanitizeName(stripped, 70);
    }

    /// <summary>
    /// Resolves a movie collection name (if the movie belongs to a multi-part
    /// collection) from:
    /// 1) TMDb's belongs_to_collection.name,
    /// 2) Known franchise prefixes (e.g. "Harry Potter and the ..."),
    /// 3) Multi-part title/filename patterns (e.g. "Dune Part Two", "Harry Potter Part 1", "John Wick Chapter 4").
    /// Returns null if the movie is a standalone single film.
    /// </summary>
    public static string? Resolve(
        string? tmdbCollectionName,
        string? movieTitle = null,
        string? sourceFileName = null)
    {
        var fromTmdb = NormalizeCollectionName(tmdbCollectionName);
        if (!string.IsNullOrWhiteSpace(fromTmdb))
        {
            return fromTmdb;
        }

        foreach (var candidate in new[] { movieTitle, StripExtensionAndYear(sourceFileName) })
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            var cleaned = candidate.Replace('.', ' ').Replace('_', ' ').Trim();

            foreach (var (prefix, collectionName) in KnownFranchises)
            {
                if (cleaned.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return collectionName;
                }
            }

            var match = MultiPartTitlePattern().Match(cleaned);
            if (match.Success)
            {
                var baseName = match.Groups["base"].Value.Trim(' ', '-', ':', '_', '.');
                if (baseName.Length >= 3 && !Regex.IsMatch(baseName, @"^\d+$"))
                {
                    return PathPlanner.SanitizeName(baseName, 70);
                }
            }
        }

        return null;
    }

    private static string? StripExtensionAndYear(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        var stem = Path.GetFileNameWithoutExtension(fileName);
        if (string.IsNullOrWhiteSpace(stem))
        {
            return null;
        }

        stem = Regex.Replace(stem, @"\[[^\]]*\]", " ");
        stem = Regex.Replace(stem, @"\((19|20)\d{2}\).*$", " ");
        stem = Regex.Replace(stem, @"\b(19|20)\d{2}\b.*$", " ");
        return Regex.Replace(stem, @"\s{2,}", " ").Trim();
    }
}
