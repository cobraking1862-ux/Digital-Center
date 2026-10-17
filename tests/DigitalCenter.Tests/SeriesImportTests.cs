using DigitalCenter.Core.Models;
using DigitalCenter.Core.Tmdb;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DigitalCenter.Tests;

/// <summary>
/// Regression tests for the TV-series import path.
///
/// "Reacher.S01.E01.mkv" — and every other supported marker format — must be
/// classified as a TV episode, searched on TMDb as a series (never as a movie),
/// matched to the parent series and linked to the right Series / Season /
/// Episode records. The original file name and the full source path survive
/// until the operator confirms a destination, repeated scans never duplicate
/// rows, and a missing season, an ambiguous match, an API failure or being
/// offline must leave the library intact with the file where it was.
/// </summary>
public sealed class SeriesImportTests : IDisposable
{
    private const long ReacherId = 5623;

    private readonly TestEnvironment _env = TestEnvironment.CreateAsync().Result;

    public void Dispose() => _env.Dispose();

    private string SeriesRoot => Path.Combine(_env.LibraryDir, "Series");

    /// <summary>Drops a file into the watch folder and runs a full rescan (detect + auto-match).</summary>
    private async Task<MetadataImportLog> DetectAndMatchAsync(string fileName, string content = "content")
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

    /// <summary>Scripts TMDb with one series ("Reacher", 2022) and season 1.</summary>
    private void ArrangeReacher()
    {
        _env.SaveSettings();
        _env.Tmdb.Configured = true;
        _env.Tmdb.SeasonUnavailable = false;
        _env.Tmdb.SeriesSearchResults =
        [
            new TmdbSearchCandidate(ReacherId, "Reacher", 2022, "Reacher", "A military investigator travels light.", "/reacher.jpg", true, "2022-02-04")
        ];
        _env.Tmdb.Series[ReacherId] = new TmdbTitleMetadata
        {
            Title = "Reacher",
            Year = 2022,
            IsSeries = true,
            Synopsis = "Jack Reacher travels light and gets involved.",
            OriginalLanguage = "en",
            AiringStatus = "Returning Series",
            Genres = ["Action & Adventure", "Crime"],
            Countries = ["United States of America"],
            SeasonCount = 2,
            Seasons =
            [
                new TmdbSeasonMetadata(1,
                [
                    new TmdbEpisodeInfo(1, 1, "Pilot", 88001),
                    new TmdbEpisodeInfo(1, 2, "Walk In", 88002)
                ])
            ]
        };
    }

    [Fact]
    public async Task Reacher_Dotted_Marker_Is_Imported_As_A_Series_Episode()
    {
        ArrangeReacher();

        var log = await DetectAndMatchAsync("Reacher.S01.E01.mkv");

        Assert.True(log.IsSeries, "A dotted S01.E01 file must be classified as a TV episode, not a movie.");
        Assert.Equal("Reacher", log.ParsedTitle);
        Assert.Equal(1, log.ParsedSeason);
        Assert.Equal(1, log.ParsedEpisode);
        Assert.Equal(ImportStatuses.Matched, log.Status);
        Assert.Equal(ReacherId, log.TmdbId);

        // Searched as a series, with the season/episode tokens stripped, and
        // never through the movie endpoint.
        Assert.Empty(_env.Tmdb.MovieSearches);
        var search = Assert.Single(_env.Tmdb.SeriesSearches);
        Assert.Equal("Reacher", search.Query);

        // The original name and the full source path survive until confirmation.
        Assert.Equal("Reacher.S01.E01.mkv", log.SourceFile);
        Assert.Equal(Path.Combine(_env.WatchDir, "Reacher.S01.E01.mkv"), log.SourcePath);
        Assert.Null(log.TargetPath);
        Assert.True(File.Exists(log.SourcePath), "Before confirmation the file must still sit in the Watch Folder.");

        await _env.Imports.ChooseDestinationAsync(log.Id, SeriesRoot);
        var outcome = await _env.Imports.ConfirmImportAsync(log.Id);
        Assert.True(outcome.Success, outcome.Message);

        var targetPath = Path.Combine(SeriesRoot, "Reacher - S01E01.mkv");
        Assert.Equal(targetPath, outcome.TargetPath);
        Assert.True(File.Exists(targetPath), $"Expected {targetPath}");
        Assert.False(File.Exists(log.SourcePath), "The confirmed import moves the file out of the Watch Folder.");

        await using var db = await _env.Factory.CreateDbContextAsync();

        var title = await db.MediaTitles.Include(t => t.SeriesDetails).SingleAsync();
        Assert.Equal(MediaKind.Series, title.Type);
        Assert.Equal("Reacher", title.Title);
        Assert.Equal(ReacherId, title.TmdbId);
        Assert.Equal(2, title.SeriesDetails!.TotalSeasons);
        Assert.Equal("Returning Series", title.SeriesDetails.AiringStatus);

        var episode = await db.Episodes
            .Include(e => e.Files)
            .Include(e => e.Season).ThenInclude(s => s.SeriesDetails).ThenInclude(sd => sd.MediaTitle)
            .SingleAsync(e => e.Season.SeasonNumber == 1 && e.EpisodeNumber == 1);
        Assert.Equal("Pilot", episode.Title);
        Assert.Equal(88001L, episode.TmdbId);
        Assert.Equal(ReacherId, episode.Season.SeriesDetails.MediaTitle.TmdbId);

        var file = Assert.Single(episode.Files);
        Assert.Equal(targetPath, file.FilePath);

        // The import log keeps the original file name and the full source path
        // even after the confirmed move.
        var afterImport = await db.MetadataImportLogs.SingleAsync(l => l.Id == log.Id);
        Assert.Equal("Reacher.S01.E01.mkv", afterImport.SourceFile);
        Assert.Equal(log.SourcePath, afterImport.SourcePath);
        Assert.Equal(ImportStatuses.Imported, afterImport.Status);
        Assert.Equal(title.Id, afterImport.MediaTitleId);

        // A movie record for an episode is exactly what must not happen.
        Assert.Equal(0, await db.MediaTitles.CountAsync(t => t.Type == MediaKind.Movie));
    }

    [Theory]
    [InlineData("Reacher.S01E01.mkv")]
    [InlineData("Reacher Season 1 Episode 1.mkv")]
    [InlineData("Reacher.1x01.mkv")]
    [InlineData("Reacher.S01.E01.1080p.WEB-DL.x264-NTb.mkv")]
    [InlineData("Reacher.2022.S01.E01.mkv")]
    public async Task Every_Supported_Marker_Format_Matches_The_Same_Series(string fileName)
    {
        ArrangeReacher();

        var log = await DetectAndMatchAsync(fileName);

        Assert.True(log.IsSeries, $"{fileName} must parse as a TV episode.");
        Assert.Equal("Reacher", log.ParsedTitle);
        Assert.Equal(1, log.ParsedSeason);
        Assert.Equal(1, log.ParsedEpisode);
        Assert.Equal(ImportStatuses.Matched, log.Status);
        Assert.Equal(ReacherId, log.TmdbId);
        Assert.Empty(_env.Tmdb.MovieSearches);
    }

    [Fact]
    public async Task Confirming_Fetches_The_Series_Summary_And_Only_The_Imported_Season()
    {
        ArrangeReacher();

        var log = await DetectAndMatchAsync("Reacher.S01.E01.mkv");
        Assert.Equal(ImportStatuses.Matched, log.Status);

        await _env.Imports.ChooseDestinationAsync(log.Id, SeriesRoot);
        Assert.True((await _env.Imports.ConfirmImportAsync(log.Id)).Success);

        // One overview request plus one season request — not one per season.
        Assert.Equal(1, _env.Tmdb.SeriesSummaryCalls);
        Assert.Equal([(ReacherId, 1)], _env.Tmdb.SeasonCalls);
        Assert.Equal(0, _env.Tmdb.SeriesDetailCalls);
    }

    [Fact]
    public async Task Repeated_Scans_Never_Duplicate_Series_Season_Or_Episode_Rows()
    {
        ArrangeReacher();

        var first = await DetectAndMatchAsync("Reacher.S01.E01.mkv");
        await _env.Imports.ChooseDestinationAsync(first.Id, SeriesRoot);
        Assert.True((await _env.Imports.ConfirmImportAsync(first.Id)).Success);

        var second = await DetectAndMatchAsync("Reacher.S01.E02.mkv");
        await _env.Imports.ChooseDestinationAsync(second.Id, SeriesRoot);
        Assert.True((await _env.Imports.ConfirmImportAsync(second.Id)).Success);

        // A rescan of the same Watch Folder must not add anything.
        await _env.WatchFolder.RescanOnceAsync(_env.WatchDir);

        await using var db = await _env.Factory.CreateDbContextAsync();
        Assert.Equal(1, await db.MediaTitles.CountAsync());
        Assert.Equal(1, await db.SeriesDetails.CountAsync());
        Assert.Equal(1, await db.Seasons.CountAsync());
        Assert.Equal(2, await db.Episodes.CountAsync());
        Assert.Equal(2, await db.MediaFiles.CountAsync());
        Assert.Equal(2, await db.MetadataImportLogs.CountAsync());

        var episodeOne = await db.Episodes.Include(e => e.Files).SingleAsync(e => e.EpisodeNumber == 1);
        Assert.Equal("Pilot", episodeOne.Title);
        Assert.Single(episodeOne.Files);
    }

    [Fact]
    public async Task A_Different_Tmdb_Id_Creates_A_New_Title_Instead_Of_Reusing_One()
    {
        ArrangeReacher();

        var first = await DetectAndMatchAsync("Reacher.S01.E01.mkv");
        await _env.Imports.ChooseDestinationAsync(first.Id, SeriesRoot);
        Assert.True((await _env.Imports.ConfirmImportAsync(first.Id)).Success);

        // The operator re-imports the same name but TMDb now resolves a
        // different series id: that must never be merged into the first record.
        _env.Tmdb.SeriesSearchResults =
        [
            new TmdbSearchCandidate(7777, "Reacher", 2022, "Reacher", "Another entry.", null, true, "2022-02-04")
        ];
        _env.Tmdb.Series[7777] = new TmdbTitleMetadata
        {
            Title = "Reacher",
            Year = 2022,
            IsSeries = true,
            SeasonCount = 1,
            Seasons = [new TmdbSeasonMetadata(1, [new TmdbEpisodeInfo(1, 3, "Third", 99003)])]
        };

        var third = await DetectAndMatchAsync("Reacher.S01.E03.mkv");
        Assert.Equal(7777L, third.TmdbId);
        await _env.Imports.ChooseDestinationAsync(third.Id, SeriesRoot);
        Assert.True((await _env.Imports.ConfirmImportAsync(third.Id)).Success);

        await using var db = await _env.Factory.CreateDbContextAsync();
        Assert.Equal(2, await db.MediaTitles.CountAsync(t => t.Title == "Reacher"));
        Assert.Equal(1, await db.MediaTitles.CountAsync(t => t.TmdbId == ReacherId));
        Assert.Equal(1, await db.MediaTitles.CountAsync(t => t.TmdbId == 7777));
    }

    [Fact]
    public async Task Unmatched_Series_Episode_Is_Marked_No_Match_And_Stays_In_Place()
    {
        _env.SaveSettings();
        _env.Tmdb.SeriesSearchResults =
        [
            new TmdbSearchCandidate(9999, "Completely Different Show", 2019, "Completely Different Show", "Not it.", null, true, "2019-01-01")
        ];

        var log = await DetectAndMatchAsync("Reacher.S01.E01.mkv");

        Assert.True(log.IsSeries);
        Assert.Equal(ImportStatuses.NoMatch, log.Status);
        Assert.Null(log.TmdbId);
        Assert.True(File.Exists(Path.Combine(_env.WatchDir, "Reacher.S01.E01.mkv")));

        await using var db = await _env.Factory.CreateDbContextAsync();
        Assert.Equal(0, await db.MediaTitles.CountAsync());
        Assert.Equal(0, await db.Episodes.CountAsync());
    }

    [Fact]
    public async Task Offline_Series_Detection_Stays_Pending_And_Recovers_As_A_Series()
    {
        _env.SaveSettings();
        _env.Tmdb.Configured = false;

        var log = await DetectAndMatchAsync("Reacher.S01.E01.mkv");
        Assert.Equal(ImportStatuses.Pending, log.Status);
        Assert.True(log.IsSeries, "Parsing is local: the classification must be right even while TMDb is unreachable.");
        Assert.True(File.Exists(log.SourcePath));

        // TMDb comes back; the pending log is matched by the next pass.
        ArrangeReacher();
        await _env.Imports.MatchPendingAsync();

        var after = await ReloadAsync(log.Id);
        Assert.Equal(ImportStatuses.Matched, after.Status);
        Assert.True(after.IsSeries);
        Assert.Equal(ReacherId, after.TmdbId);
        Assert.Equal(1, after.ParsedEpisode);
    }

    [Fact]
    public async Task A_Log_Classified_As_Movie_By_An_Older_Parse_Is_Upgraded_On_Rematch()
    {
        ArrangeReacher();

        var path = Path.Combine(_env.WatchDir, "Reacher.S01.E01.mkv");
        await File.WriteAllTextAsync(path, "content");

        // Simulate a log written before the parser fix: classified as a movie
        // and carrying the marker text inside the title.
        await using (var db = await _env.Factory.CreateDbContextAsync())
        {
            db.MetadataImportLogs.Add(new MetadataImportLog
            {
                SourceFile = "Reacher.S01.E01.mkv",
                SourcePath = path,
                Status = ImportStatuses.Pending,
                IsSeries = false,
                ParsedTitle = "Reacher S01 E01",
                FileSize = new FileInfo(path).Length,
                Timestamp = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        await _env.Imports.MatchPendingAsync();

        await using (var db = await _env.Factory.CreateDbContextAsync())
        {
            var log = await db.MetadataImportLogs.SingleAsync(l => l.SourcePath == path);
            Assert.True(log.IsSeries, "The rematch must re-parse and upgrade the stale movie classification.");
            Assert.Equal("Reacher", log.ParsedTitle);
            Assert.Equal(1, log.ParsedSeason);
            Assert.Equal(1, log.ParsedEpisode);
            Assert.Equal(ImportStatuses.Matched, log.Status);
            Assert.Equal(ReacherId, log.TmdbId);
            Assert.Empty(_env.Tmdb.MovieSearches);
        }
    }

    [Fact]
    public async Task An_Unavailable_Season_Still_Imports_The_Episode()
    {
        ArrangeReacher();
        _env.Tmdb.SeasonUnavailable = true; // TMDb 404 / partial outage

        var log = await DetectAndMatchAsync("Reacher.S01.E01.mkv");
        Assert.Equal(ImportStatuses.Matched, log.Status);

        await _env.Imports.ChooseDestinationAsync(log.Id, SeriesRoot);
        var outcome = await _env.Imports.ConfirmImportAsync(log.Id);
        Assert.True(outcome.Success, outcome.Message);

        await using var db = await _env.Factory.CreateDbContextAsync();
        var title = await db.MediaTitles.Include(t => t.SeriesDetails).SingleAsync();
        Assert.Equal("Reacher", title.Title);
        Assert.Equal(2, title.SeriesDetails!.TotalSeasons);

        var episode = await db.Episodes.Include(e => e.Files).SingleAsync();
        Assert.Equal("Episode 1", episode.Title); // placeholder until the season is known
        Assert.Single(episode.Files);
    }
}
