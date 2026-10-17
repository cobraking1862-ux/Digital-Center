using DigitalCenter.Core.Configuration;
using DigitalCenter.Core.Models;
using DigitalCenter.Core.Services;
using DigitalCenter.Core.Tmdb;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DigitalCenter.Tests;

/// <summary>
/// End-to-end tests of the 5.0 import workflow: automatic match (identifiers
/// first, then title/year), destination choice, confirmed move with
/// verification, skip, no-match handling and crash recovery. Uses the fake
/// TMDb provider and temp folders; no network, no real files.
/// </summary>
public sealed class ImportServiceTests : IDisposable
{
    private readonly TestEnvironment _env = TestEnvironment.CreateAsync().Result;

    public void Dispose() => _env.Dispose();

    private string MoviesRoot => Path.Combine(_env.LibraryDir, "Movies");
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

    private static TmdbTitleMetadata InceptionMetadata() => new()
    {
        Title = "Inception",
        Year = 2010,
        Synopsis = "Cobb steals secrets.",
        PosterPath = "/inc.jpg",
        IsSeries = false,
        Genres = ["Action", "Science Fiction"],
        Countries = ["United States of America"],
        People =
        [
            new TmdbPersonCredit("Leonardo DiCaprio", "Actor", "Cobb", 101, "/leo.jpg"),
            new TmdbPersonCredit("Christopher Nolan", "Director", null, 102, "/nolan.jpg")
        ]
    };

    [Fact]
    public async Task Confirm_Movie_After_TitleYear_Match_Moves_Registers_Caches()
    {
        _env.SaveSettings();
        _env.Tmdb.MovieSearchResults =
        [
            new TmdbSearchCandidate(27205, "Inception", 2010, "Inception", "Cobb steals secrets.", "/inc.jpg", false, "2010-07-16")
        ];
        _env.Tmdb.Movies[27205] = InceptionMetadata();

        var log = await DetectAndMatchAsync("Inception (2010).mkv");
        Assert.Equal(ImportStatuses.Matched, log.Status);
        Assert.Equal(27205L, log.TmdbId);
        Assert.NotNull(log.Confidence);
        Assert.True(log.Confidence >= 0.6, $"confidence {log.Confidence}");

        await _env.Imports.ChooseDestinationAsync(log.Id, MoviesRoot);
        var outcome = await _env.Imports.ConfirmImportAsync(log.Id);

        Assert.True(outcome.Success, outcome.Message);

        // Clean file name from the template; no category folders anywhere.
        var targetPath = Path.Combine(MoviesRoot, "Inception (2010).mkv");
        Assert.True(File.Exists(targetPath), $"Expected {targetPath}");
        Assert.False(File.Exists(Path.Combine(_env.WatchDir, "Inception (2010).mkv")));

        await using var db = await _env.Factory.CreateDbContextAsync();
        var title = await db.MediaTitles.Include(t => t.Files)
            .Include(t => t.Genres).ThenInclude(tg => tg.Genre)
            .Include(t => t.People).ThenInclude(tp => tp.Person)
            .SingleAsync(t => t.TmdbId == 27205);
        Assert.Equal("Inception", title.Title);
        Assert.Equal(2010, title.ReleaseYear);
        Assert.Equal("Cobb steals secrets.", title.Synopsis);
        var file = Assert.Single(title.Files);
        Assert.Equal(targetPath, file.FilePath);
        Assert.Equal(title.Id, file.MovieTitleId);
        Assert.Null(file.EpisodeId);
        Assert.Contains(title.Genres, g => g.Genre.Name == "Action");
        Assert.Contains(title.People, p => p.Person.Name == "Leonardo DiCaprio" && p.Character == "Cobb");
        Assert.Contains(title.People, p => p.Person.Name == "Christopher Nolan" && p.Role == "Director");

        // Poster cached locally (fake bytes) and referenced by the title.
        var posterFile = Path.Combine(_env.Paths.PostersDirectory, title.PosterPath!);
        Assert.True(File.Exists(posterFile));
        Assert.Equal(_env.Tmdb.PosterBytes, await File.ReadAllBytesAsync(posterFile));

        // Log finalized.
        var finalLog = await db.MetadataImportLogs.SingleAsync(l => l.Id == log.Id);
        Assert.Equal(ImportStatuses.Imported, finalLog.Status);
        Assert.Equal(ImportStages.Imported, finalLog.Stage);
        Assert.Equal(targetPath, finalLog.TargetPath);
        Assert.Equal("TMDb", finalLog.MetadataSource);
    }

    [Fact]
    public async Task Tmdb_Movie_Tag_Pins_Exact_Match_Without_Search()
    {
        _env.SaveSettings();
        _env.Tmdb.Movies[693134] = new TmdbTitleMetadata
        {
            Title = "Dune: Part Two",
            Year = 2024,
            IsSeries = false
        };

        var log = await DetectAndMatchAsync("[tmdb-movie-693134] Dune.Part.Two.2024.mkv");

        Assert.Equal(ImportStatuses.Matched, log.Status);
        Assert.Equal(693134L, log.TmdbId);
        Assert.Equal(0.98, log.Confidence);
        Assert.Contains("tmdb", log.Note ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Imdb_Tag_Resolves_Via_Find_Endpoint()
    {
        _env.SaveSettings();
        _env.Tmdb.ImdbIndex["tt0111161"] = (27205, false);
        _env.Tmdb.Movies[27205] = new TmdbTitleMetadata { Title = "The Shawshank Redemption", Year = 1994, IsSeries = false };

        var log = await DetectAndMatchAsync("The.Shawshank.Redemption.1994.[imdb-tt0111161].mkv");

        Assert.Contains("tt0111161", _env.Tmdb.ImdbLookups);
        Assert.Equal(ImportStatuses.Matched, log.Status);
        Assert.Equal(27205L, log.TmdbId);
    }

    [Fact]
    public async Task No_Reliable_Match_Leaves_File_In_Place()
    {
        _env.SaveSettings();
        _env.Tmdb.MovieSearchResults =
        [
            new TmdbSearchCandidate(999, "Something Completely Different", 2019, null, "About other things.", null, false, "")
        ];

        var log = await DetectAndMatchAsync("Totally.Unknown.Movie.2023.mkv");

        Assert.Equal(ImportStatuses.NoMatch, log.Status);
        Assert.True(File.Exists(Path.Combine(_env.WatchDir, "Totally.Unknown.Movie.2023.mkv")),
            "No-match files must stay in the Watch Folder.");
        await using var db = await _env.Factory.CreateDbContextAsync();
        Assert.Empty(db.MediaTitles.Local);
        Assert.Equal(0, await db.MediaTitles.CountAsync());
    }

    [Fact]
    public async Task Offline_Match_Stays_Pending_And_Is_Retried_Later()
    {
        _env.SaveSettings();
        _env.Tmdb.Configured = false;

        var log = await DetectAndMatchAsync("Retry.Movie.2020.mkv");
        Assert.Equal(ImportStatuses.Pending, log.Status);
        Assert.Contains("TMDb", log.ErrorDetails);

        // The connection comes back and the next rescan matches the file.
        _env.Tmdb.Configured = true;
        _env.Tmdb.MovieSearchResults =
        [
            new TmdbSearchCandidate(4242, "Retry Movie", 2020, "Retry Movie", "It retries.", null, false, "")
        ];
        await _env.WatchFolder.RescanOnceAsync(_env.WatchDir);
        var after = await ReloadAsync(log.Id);
        Assert.Equal(ImportStatuses.Matched, after.Status);
    }

    [Fact]
    public async Task Non_Media_File_Is_Logged_Unsupported_And_Stays()
    {
        _env.SaveSettings();
        var log = await DetectAndMatchAsync("Avengers.Endgame.zip");

        Assert.Equal(ImportStatuses.Unsupported, log.Status);
        Assert.True(File.Exists(Path.Combine(_env.WatchDir, "Avengers.Endgame.zip")));
        Assert.Contains("not a supported", log.ErrorDetails, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Confirm_Series_Links_Episode_File()
    {
        _env.SaveSettings();
        _env.Tmdb.SeriesSearchResults =
        [
            new TmdbSearchCandidate(1396, "Breaking Bad", 2008, "Breaking Bad", "Walter White.", "/bb.jpg", true, "2008-01-20")
        ];
        _env.Tmdb.Series[1396] = new TmdbTitleMetadata
        {
            Title = "Breaking Bad",
            Year = 2008,
            IsSeries = true,
            AiringStatus = "Ended",
            Genres = ["Drama"],
            Seasons =
            [
                new TmdbSeasonMetadata(1,
                [
                    new TmdbEpisodeInfo(1, 1, "Pilot"),
                    new TmdbEpisodeInfo(1, 2, "Cat's in the Bag...")
                ])
            ]
        };

        var log = await DetectAndMatchAsync("Breaking.Bad.S01E02.720p.mkv");
        Assert.True(log.IsSeries);
        Assert.Equal(1, log.ParsedSeason);
        Assert.Equal(2, log.ParsedEpisode);

        await _env.Imports.ChooseDestinationAsync(log.Id, SeriesRoot);
        var outcome = await _env.Imports.ConfirmImportAsync(log.Id);
        Assert.True(outcome.Success, outcome.Message);

        var expectedPath = Path.Combine(SeriesRoot, "Breaking Bad - S01E02.mkv");
        Assert.True(File.Exists(expectedPath), $"Expected {expectedPath}");

        await using var db = await _env.Factory.CreateDbContextAsync();
        var episode = await db.Episodes
            .Include(e => e.Files)
            .Include(e => e.Season).ThenInclude(s => s.SeriesDetails).ThenInclude(sd => sd.MediaTitle)
            .SingleAsync(e => e.Season.SeasonNumber == 1 && e.EpisodeNumber == 2);
        Assert.Equal("Cat's in the Bag...", episode.Title);
        var episodeFile = Assert.Single(episode.Files);
        Assert.Equal(expectedPath, episodeFile.FilePath);
        Assert.Equal(1396, episode.Season.SeriesDetails.MediaTitle.TmdbId);
        Assert.Equal("Ended", episode.Season.SeriesDetails.AiringStatus);
    }

    [Fact]
    public async Task Skip_Keeps_File_In_WatchFolder()
    {
        _env.SaveSettings();
        _env.Tmdb.MovieSearchResults =
        [
            new TmdbSearchCandidate(777, "Rejected Movie", 2021, "Rejected Movie", null, null, false, "")
        ];

        var log = await DetectAndMatchAsync("Rejected.Movie.2021.mkv");
        Assert.Equal(ImportStatuses.Matched, log.Status);

        await _env.Imports.SkipAsync(log.Id);

        Assert.True(File.Exists(Path.Combine(_env.WatchDir, "Rejected.Movie.2021.mkv")));
        var after = await ReloadAsync(log.Id);
        Assert.Equal(ImportStatuses.Rejected, after.Status);
    }

    [Fact]
    public async Task Second_File_With_Same_Name_Is_Renamed_Not_Overwritten()
    {
        _env.SaveSettings();
        _env.Tmdb.MovieSearchResults =
        [
            new TmdbSearchCandidate(1001, "Alternate Cut", 2015, "Alternate Cut", "Two copies.", null, false, "")
        ];
        _env.Tmdb.Movies[1001] = new TmdbTitleMetadata { Title = "Alternate Cut", Year = 2015, IsSeries = false };

        var first = await DetectAndMatchAsync("Alternate.Cut.2015.mkv", "first content");
        await _env.Imports.ChooseDestinationAsync(first.Id, MoviesRoot);
        var outcome1 = await _env.Imports.ConfirmImportAsync(first.Id);
        Assert.True(outcome1.Success, outcome1.Message);

        // Remove the old log so the watch folder pass detects the second copy
        // as a new import (realistic: operator re-adds a better version later).
        await using (var db = await _env.Factory.CreateDbContextAsync())
        {
            var done = await db.MetadataImportLogs.SingleAsync(l => l.Id == first.Id);
            done.SourcePath = done.SourcePath + ".done";
            await db.SaveChangesAsync();
        }

        var second = await DetectAndMatchAsync("Alternate.Cut.2015.mkv", "second content");
        await _env.Imports.ChooseDestinationAsync(second.Id, MoviesRoot);
        var outcome2 = await _env.Imports.ConfirmImportAsync(second.Id);

        Assert.True(outcome2.Success, outcome2.Message);
        Assert.True(File.Exists(Path.Combine(MoviesRoot, "Alternate Cut (2015).mkv")));
        Assert.True(File.Exists(Path.Combine(MoviesRoot, "Alternate Cut (2015) (2).mkv")));
        Assert.Equal("first content", await File.ReadAllTextAsync(Path.Combine(MoviesRoot, "Alternate Cut (2015).mkv")));
        Assert.Equal("second content", await File.ReadAllTextAsync(Path.Combine(MoviesRoot, "Alternate Cut (2015) (2).mkv")));
    }

    [Fact]
    public async Task Confirm_Without_Destination_Throws_And_File_Stays()
    {
        _env.SaveSettings();
        _env.Tmdb.MovieSearchResults =
        [
            new TmdbSearchCandidate(555, "No Destination", 2018, "No Destination", null, null, false, "")
        ];

        var log = await DetectAndMatchAsync("No.Destination.2018.mkv");
        // No root configured through ChooseDestination and no default either.
        _env.Settings.Save(new AppSettings { WatchFolderPath = _env.WatchDir, StabilityDelaySeconds = 2 });

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _env.Imports.ConfirmImportAsync(log.Id));
        Assert.True(File.Exists(Path.Combine(_env.WatchDir, "No.Destination.2018.mkv")));
    }

    [Fact]
    public async Task Stats_Count_Every_State()
    {
        _env.SaveSettings();
        _env.Tmdb.MovieSearchResults =
        [
            new TmdbSearchCandidate(800, "Stats Movie", 2017, "Stats Movie", null, null, false, "")
        ];

        var matched = await DetectAndMatchAsync("Stats.Movie.2017.mkv");
        _ = await DetectAndMatchAsync("Unknown.Nothing.1999.mkv"); // NoMatch
        _ = await DetectAndMatchAsync("junk.rar");                // Unsupported

        await _env.Imports.SkipAsync(matched.Id);

        var stats = await _env.Imports.GetStatsAsync();
        Assert.Equal(3, stats.Total);
        Assert.Equal(1, stats.Skipped);
        Assert.Equal(1, stats.NoMatch);
        Assert.Equal(1, stats.Unsupported);
    }

    // ------------------------------------------------------- crash recovery

    [Fact]
    public async Task Recovery_Completes_Import_When_Crash_Happened_After_Copy()
    {
        _env.SaveSettings();
        var log = await DetectAndMatchAsync("Interrupted.2012.mkv", "data");

        // Simulate the crash: mark approved + move started, and place a
        // complete copy at the target without finishing the import.
        var target = Path.Combine(MoviesRoot, "Interrupted.2012.mkv");
        Directory.CreateDirectory(MoviesRoot);
        await File.WriteAllTextAsync(target, "data");

        await using (var db = await _env.Factory.CreateDbContextAsync())
        {
            var tracked = await db.MetadataImportLogs.SingleAsync(l => l.Id == log.Id);
            tracked.Status = ImportStatuses.Approved;
            tracked.Stage = ImportStages.MoveStarted;
            tracked.ChosenFolder = MoviesRoot;
            tracked.TargetPath = target;
            await db.SaveChangesAsync();
        }

        // The source is still in the watch folder (not yet deleted).
        var report = await _env.Imports.RecoverInterruptedAsync();

        await using var verify = await _env.Factory.CreateDbContextAsync();
        var recovered = await verify.MetadataImportLogs.SingleAsync(l => l.Id == log.Id);
        Assert.Equal(ImportStatuses.Imported, recovered.Status);
        var file = await verify.MediaFiles.SingleAsync(f => f.FilePath == target);
        Assert.Equal(file.MovieTitleId, recovered.MediaTitleId);
        Assert.Contains(report, r => r.Contains("Recovered"));
    }

    [Fact]
    public async Task Recovery_Fails_Safely_When_Source_And_Target_Are_Gone()
    {
        _env.SaveSettings();
        var log = await DetectAndMatchAsync("Vanished.2011.mkv", "data");
        File.Delete(Path.Combine(_env.WatchDir, "Vanished.2011.mkv"));

        await using (var db = await _env.Factory.CreateDbContextAsync())
        {
            var tracked = await db.MetadataImportLogs.SingleAsync(l => l.Id == log.Id);
            tracked.Status = ImportStatuses.Approved;
            tracked.Stage = ImportStages.MoveStarted;
            tracked.ChosenFolder = MoviesRoot;
            tracked.TargetPath = Path.Combine(MoviesRoot, "Vanished (2011)", "Vanished.2011.mkv");
            await db.SaveChangesAsync();
        }

        var report = await _env.Imports.RecoverInterruptedAsync();

        await using var verify = await _env.Factory.CreateDbContextAsync();
        var failed = await verify.MetadataImportLogs.SingleAsync(l => l.Id == log.Id);
        Assert.Equal(ImportStatuses.Failed, failed.Status);
        Assert.Contains("no longer present", failed.ErrorDetails);
        Assert.Contains(report, r => r.Contains("failed"));
    }

    [Fact]
    public async Task Recovery_Completes_Registration_When_Crash_Happened_After_Move()
    {
        _env.SaveSettings();
        var log = await DetectAndMatchAsync("MovedOnly.2014.mkv", "moved");

        // Simulate: file fully moved into the library, crash before registering.
        var target = Path.Combine(MoviesRoot, "MovedOnly.2014.mkv");
        Directory.CreateDirectory(MoviesRoot);
        await File.WriteAllTextAsync(target, "moved");
        File.Delete(Path.Combine(_env.WatchDir, "MovedOnly.2014.mkv"));

        await using (var db = await _env.Factory.CreateDbContextAsync())
        {
            var tracked = await db.MetadataImportLogs.SingleAsync(l => l.Id == log.Id);
            tracked.Status = ImportStatuses.Approved;
            tracked.Stage = ImportStages.MovedToLibrary;
            tracked.ChosenFolder = MoviesRoot;
            tracked.TargetPath = target;
            tracked.SourcePath = Path.Combine(_env.WatchDir, "MovedOnly.2014.mkv");
            await db.SaveChangesAsync();
        }

        await _env.Imports.RecoverInterruptedAsync();

        await using var verify = await _env.Factory.CreateDbContextAsync();
        var recovered = await verify.MetadataImportLogs.SingleAsync(l => l.Id == log.Id);
        Assert.Equal(ImportStatuses.Imported, recovered.Status);
        Assert.Single(await verify.MediaFiles.ToListAsync());
    }
}
