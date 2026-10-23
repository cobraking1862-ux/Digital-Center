using DigitalCenter.Core.Configuration;
using DigitalCenter.Core.Models;
using DigitalCenter.Core.Services;
using DigitalCenter.Core.Tmdb;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DigitalCenter.Tests;

/// <summary>
/// The self-organizing Watch Folder: a new file is detected, its metadata is
/// fetched, and the file is filed under the language it was detected in — the
/// language folder is created in the library root when it does not exist yet.
/// Everything runs offline against the scripted TMDb double and temp folders.
/// </summary>
public sealed class LanguageOrganizationTests : IDisposable
{
    private readonly TestEnvironment _env = TestEnvironment.CreateAsync().Result;

    public void Dispose() => _env.Dispose();

    private string MoviesRoot => Path.Combine(_env.LibraryDir, "Movies");
    private string SeriesRoot => Path.Combine(_env.LibraryDir, "Series");

    /// <summary>Saves settings with the two organization switches under test.</summary>
    private void SaveSettings(bool autoImport, bool byLanguage = true)
    {
        _env.Settings.Save(new AppSettings
        {
            MoviesRoot = MoviesRoot,
            SeriesRoot = SeriesRoot,
            WatchFolderPath = _env.WatchDir,
            StabilityDelaySeconds = 2,
            AutoImportMatchedFiles = autoImport,
            OrganizeLibraryByLanguage = byLanguage
        });
        Directory.CreateDirectory(MoviesRoot);
        Directory.CreateDirectory(SeriesRoot);
    }

    /// <summary>Drops a file into the watch folder and runs one full pass
    /// (detect → metadata → automatic move when it is switched on).</summary>
    private async Task<MetadataImportLog> RunWatchFolderPassAsync(string fileName, string content = "content")
    {
        var path = Path.Combine(_env.WatchDir, fileName);
        await File.WriteAllTextAsync(path, content);
        await _env.WatchFolder.RescanOnceAsync(_env.WatchDir);
        await using var db = await _env.Factory.CreateDbContextAsync();
        return await db.MetadataImportLogs.SingleAsync(l => l.SourcePath == path);
    }

    private async Task<MetadataImportLog> ReloadAsync(int logId)
    {
        await using var db = await _env.Factory.CreateDbContextAsync();
        return await db.MetadataImportLogs.SingleAsync(l => l.Id == logId);
    }

    // ---------------------------------------------------- language folder names

    [Theory]
    [InlineData("en", "English")]
    [InlineData("ur", "Urdu")]
    [InlineData("hi", "Hindi")]
    [InlineData("ko", "Korean")]
    [InlineData("ja", "Japanese")]
    [InlineData("fr", "French")]
    [InlineData("ar", "Arabic")]
    [InlineData("pt-BR", "Portuguese")]
    [InlineData("zh-Hant", "Chinese")]
    public void Language_Code_Becomes_A_Readable_Folder_Name(string code, string expected) =>
        Assert.Equal(expected, LanguageFolder.NameFor(code));

    [Theory]
    [InlineData("xx")]
    [InlineData("qq")]
    public void Unknown_Language_Code_Still_Groups_By_Language(string code) =>
        Assert.Equal(char.ToUpperInvariant(code[0]) + code[1..], LanguageFolder.NameFor(code));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Missing_Language_Never_Invents_A_Folder(string? code)
    {
        Assert.Null(LanguageFolder.NameFor(code));
        Assert.Equal(@"C:\Library\Movies", LanguageFolder.Resolve(@"C:\Library\Movies", code));
    }

    [Fact]
    public void Folder_Name_Is_Sanitized_For_The_File_System()
    {
        // A slash can never survive into a folder name, whatever the source.
        var name = LanguageFolder.NameFor("en/us");
        Assert.NotNull(name);
        Assert.DoesNotContain('/', name);
        Assert.DoesNotContain('\\', name);
    }

    // ------------------------------------------------------- automatic filing

    [Fact]
    public async Task Matched_Movie_Is_Filed_Under_Its_Detected_Language()
    {
        SaveSettings(autoImport: true);
        _env.Tmdb.MovieSearchResults =
        [
            new TmdbSearchCandidate(31337, "Parasite", 2019, "기생충", null, "/parasite.jpg", false, "2019-05-30", "ko")
        ];
        _env.Tmdb.Movies[31337] = new TmdbTitleMetadata
        {
            Title = "Parasite",
            Year = 2019,
            IsSeries = false,
            OriginalLanguage = "ko",
            PosterPath = "/parasite.jpg",
            Genres = ["Drama", "Thriller"]
        };

        var log = await RunWatchFolderPassAsync("Parasite.2019.mkv");

        var languageFolder = Path.Combine(MoviesRoot, "Korean");
        Assert.True(Directory.Exists(languageFolder), "the language folder is created when it is missing");

        var target = Path.Combine(languageFolder, "Parasite (2019).mkv");
        Assert.True(File.Exists(target), $"expected the file at {target}");
        Assert.False(File.Exists(Path.Combine(_env.WatchDir, "Parasite.2019.mkv")));

        Assert.Equal(ImportStatuses.Imported, log.Status);
        Assert.Equal(languageFolder, log.ChosenFolder);
        Assert.Equal(target, log.TargetPath);

        await using var db = await _env.Factory.CreateDbContextAsync();
        var title = await db.MediaTitles.Include(t => t.Files).SingleAsync(t => t.TmdbId == 31337);
        Assert.Equal("ko", title.OriginalLanguage);
        Assert.Equal(target, Assert.Single(title.Files).FilePath);
    }

    [Fact]
    public async Task Matched_Series_Episode_Uses_The_Series_Root_Language_Folder()
    {
        SaveSettings(autoImport: true);
        _env.Tmdb.SeriesSearchResults =
        [
            new TmdbSearchCandidate(7711, "Shinya Shokudo", 2016, "深夜食堂", null, "/shokudo.jpg", true, "2016-10-21", "ja")
        ];
        _env.Tmdb.Series[7711] = new TmdbTitleMetadata
        {
            Title = "Shinya Shokudo",
            Year = 2016,
            IsSeries = true,
            OriginalLanguage = "ja",
            SeasonCount = 1,
            Seasons =
            [
                new TmdbSeasonMetadata(1,
                [
                    new TmdbEpisodeInfo(1, 1, "First Plate", 771101),
                    new TmdbEpisodeInfo(1, 2, "Second Plate", 771102)
                ])
            ]
        };

        var log = await RunWatchFolderPassAsync("Shinya.Shokudo.S01E02.mkv");

        var languageFolder = Path.Combine(SeriesRoot, "Japanese");
        Assert.True(Directory.Exists(languageFolder));
        Assert.Equal(ImportStatuses.Imported, log.Status);
        Assert.True(log.IsSeries);
        Assert.StartsWith(languageFolder, log.TargetPath, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_env.WatchDir, "Shinya.Shokudo.S01E02.mkv")));

        await using var db = await _env.Factory.CreateDbContextAsync();
        var file = await db.MediaFiles.Include(f => f.Episode).SingleAsync();
        Assert.Equal(2, file.Episode!.EpisodeNumber);
        Assert.Equal(log.TargetPath, file.FilePath);
    }

    [Fact]
    public async Task Language_Organization_Off_Files_Straight_Into_The_Root()
    {
        SaveSettings(autoImport: true, byLanguage: false);
        _env.Tmdb.MovieSearchResults =
        [
            new TmdbSearchCandidate(4422, "Bol", 2011, "Bol", null, null, false, "2011-06-24", "ur")
        ];
        _env.Tmdb.Movies[4422] = new TmdbTitleMetadata
        {
            Title = "Bol",
            Year = 2011,
            IsSeries = false,
            OriginalLanguage = "ur"
        };

        var log = await RunWatchFolderPassAsync("Bol.2011.mkv");

        Assert.Equal(ImportStatuses.Imported, log.Status);
        Assert.Equal(MoviesRoot, log.ChosenFolder);
        Assert.True(File.Exists(Path.Combine(MoviesRoot, "Bol (2011).mkv")));
        Assert.False(Directory.Exists(Path.Combine(MoviesRoot, "Urdu")));
    }

    [Fact]
    public async Task Automatic_Move_Off_Plans_The_Folder_But_Leaves_The_File()
    {
        SaveSettings(autoImport: false);
        _env.Tmdb.MovieSearchResults =
        [
            new TmdbSearchCandidate(5533, "Baahubali", 2015, "బాహుబలి", null, null, false, "2015-07-10", "te")
        ];
        _env.Tmdb.Movies[5533] = new TmdbTitleMetadata
        {
            Title = "Baahubali",
            Year = 2015,
            IsSeries = false,
            OriginalLanguage = "te"
        };

        var log = await RunWatchFolderPassAsync("Baahubali.2015.mkv");

        // Planned, not moved: the operator still confirms (or uses Browse / Move).
        Assert.Equal(ImportStatuses.Matched, log.Status);
        Assert.Equal(Path.Combine(MoviesRoot, "Telugu"), log.ChosenFolder);
        Assert.True(Directory.Exists(Path.Combine(MoviesRoot, "Telugu")));
        Assert.True(File.Exists(Path.Combine(_env.WatchDir, "Baahubali.2015.mkv")));

        // Confirming by hand then uses the planned language folder.
        var outcome = await _env.Imports.ConfirmImportAsync(log.Id);
        Assert.True(outcome.Success, outcome.Message);
        Assert.True(File.Exists(Path.Combine(MoviesRoot, "Telugu", "Baahubali (2015).mkv")));
    }

    [Fact]
    public async Task Unmatched_File_Is_Never_Moved_Automatically()
    {
        SaveSettings(autoImport: true);
        _env.Tmdb.MovieSearchResults =
        [
            new TmdbSearchCandidate(9999, "Something Completely Different", 1999, null, null, null, false, "1999-01-01", "en")
        ];

        var log = await RunWatchFolderPassAsync("Mystery.Clip.2024.mkv");

        Assert.Equal(ImportStatuses.NoMatch, log.Status);
        Assert.True(File.Exists(Path.Combine(_env.WatchDir, "Mystery.Clip.2024.mkv")));
        Assert.Empty(Directory.GetDirectories(MoviesRoot));
    }

    [Fact]
    public async Task Unknown_Language_Code_Still_Files_The_Movie()
    {
        SaveSettings(autoImport: true);
        _env.Tmdb.MovieSearchResults =
        [
            new TmdbSearchCandidate(6644, "Rare Cut", 2013, "Rare Cut", null, null, false, "2013-03-03", "xx")
        ];
        _env.Tmdb.Movies[6644] = new TmdbTitleMetadata
        {
            Title = "Rare Cut",
            Year = 2013,
            IsSeries = false,
            OriginalLanguage = "xx"
        };

        var log = await RunWatchFolderPassAsync("Rare.Cut.2013.mkv");

        Assert.Equal(ImportStatuses.Imported, log.Status);
        Assert.True(File.Exists(Path.Combine(MoviesRoot, "Xx", "Rare Cut (2013).mkv")));
    }

    [Fact]
    public async Task Automatic_Organization_Reports_What_It_Did()
    {
        SaveSettings(autoImport: true);
        _env.Tmdb.MovieSearchResults =
        [
            new TmdbSearchCandidate(2211, "Reported Film", 2008, "Reported Film", null, null, false, "2008-08-08", "fr")
        ];
        _env.Tmdb.Movies[2211] = new TmdbTitleMetadata
        {
            Title = "Reported Film",
            Year = 2008,
            IsSeries = false,
            OriginalLanguage = "fr"
        };

        var reported = new List<string>();
        _env.Imports.ActivityReported += message => reported.Add(message);

        await RunWatchFolderPassAsync("Reported.Film.2008.mkv");

        Assert.Contains(reported, m => m.StartsWith("Downloading metadata for 1 pending file"));
        Assert.Contains(reported, m => m.StartsWith("Moving file to Library: 1 matched file"));
        Assert.Contains(reported, m => m.StartsWith("Moved into the library:"));
        Assert.Contains(reported, m => m.StartsWith("Automatic organization finished: 1 file(s) moved"));
        Assert.True(File.Exists(Path.Combine(MoviesRoot, "French", "Reported Film (2008).mkv")));
    }

    [Fact]
    public async Task Second_File_In_The_Same_Language_Reuses_The_Folder()
    {
        SaveSettings(autoImport: true);
        _env.Tmdb.MovieSearchResults =
        [
            new TmdbSearchCandidate(1201, "First French Film", 2001, "First French Film", null, null, false, "2001-01-01", "fr"),
            new TmdbSearchCandidate(1202, "Second French Film", 2002, "Second French Film", null, null, false, "2002-02-02", "fr")
        ];
        _env.Tmdb.Movies[1201] = new TmdbTitleMetadata { Title = "First French Film", Year = 2001, IsSeries = false, OriginalLanguage = "fr" };
        _env.Tmdb.Movies[1202] = new TmdbTitleMetadata { Title = "Second French Film", Year = 2002, IsSeries = false, OriginalLanguage = "fr" };

        await RunWatchFolderPassAsync("First.French.Film.2001.mkv");
        await RunWatchFolderPassAsync("Second.French.Film.2002.mkv");

        var french = Path.Combine(MoviesRoot, "French");
        Assert.Equal(2, Directory.GetFiles(french).Length);
        Assert.True(File.Exists(Path.Combine(french, "First French Film (2001).mkv")));
        Assert.True(File.Exists(Path.Combine(french, "Second French Film (2002).mkv")));
    }

    [Fact]
    public async Task Existing_Library_Records_Survive_A_Language_Change()
    {
        // A re-import of the same title under another language must not duplicate
        // the catalog entry: the language decides the FOLDER, never the identity.
        SaveSettings(autoImport: true);
        _env.Tmdb.MovieSearchResults =
        [
            new TmdbSearchCandidate(1201, "First French Film", 2001, "First French Film", null, null, false, "2001-01-01", "fr")
        ];
        _env.Tmdb.Movies[1201] = new TmdbTitleMetadata { Title = "First French Film", Year = 2001, IsSeries = false, OriginalLanguage = "fr" };

        await RunWatchFolderPassAsync("First.French.Film.2001.mkv");

        _env.Tmdb.MovieSearchResults =
        [
            new TmdbSearchCandidate(1201, "First French Film", 2001, "First French Film", null, null, false, "2001-01-01", "de")
        ];
        await RunWatchFolderPassAsync("First.French.Film.2001.1080p.BluRay.mkv", "second copy");

        await using var db = await _env.Factory.CreateDbContextAsync();
        var titles = await db.MediaTitles.Where(t => t.TmdbId == 1201).ToListAsync();
        var title = Assert.Single(titles);
        Assert.Equal(2, (await db.MediaFiles.Where(f => f.MovieTitleId == title.Id).ToListAsync()).Count);
        Assert.True(Directory.Exists(Path.Combine(MoviesRoot, "French")));
        Assert.True(Directory.Exists(Path.Combine(MoviesRoot, "German")));
    }
}
