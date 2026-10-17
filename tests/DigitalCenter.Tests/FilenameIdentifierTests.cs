using DigitalCenter.Core.Configuration;
using DigitalCenter.Core.Services;
using DigitalCenter.Core.Tmdb;
using DigitalCenter.Core.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DigitalCenter.Tests;

/// <summary>
/// External identifier tags in file names ([tmdb-movie-ID], [tmdb-tv-ID],
/// [imdb-ttID], bare ttID) and the title-similarity/matcher scoring rules.
/// </summary>
public class FilenameIdentifierTests
{
    private readonly FilenameParser _parser = new();

    [Theory]
    [InlineData("[tmdb-movie-693134] Dune.Part.Two.2024.mkv", 693134L, null, null)]
    [InlineData("Dune Part Two [tmdb movie 693134].mkv", 693134L, null, null)]
    [InlineData("Show.S01E02.[tmdb-tv-1399].mkv", null, 1399L, null)]
    [InlineData("[imdb-tt0111161] Movie.mkv", null, null, "tt0111161")]
    [InlineData("The.Matrix.1999.tt0133093.mkv", null, null, "tt0133093")]
    public void Parses_External_Identifiers(string fileName, long? movieId, long? seriesId, string? imdbId)
    {
        var parsed = _parser.Parse(fileName);

        Assert.Equal(movieId, parsed.TmdbMovieId);
        Assert.Equal(seriesId, parsed.TmdbSeriesId);
        Assert.Equal(imdbId, parsed.ImdbId);
        Assert.True(parsed.HasExternalId);
    }

    [Fact]
    public void Identifiers_Are_Removed_From_The_Title()
    {
        var parsed = _parser.Parse("[tmdb-movie-27205] Inception (2010).mkv");

        Assert.Equal(27205L, parsed.TmdbMovieId);
        Assert.Equal("Inception", parsed.Title);
        Assert.Equal(2010, parsed.Year);
    }

    [Fact]
    public void Id_Only_Name_Keeps_The_Identifier()
    {
        var parsed = _parser.Parse("[tmdb-movie-123].mkv");

        Assert.Equal(123L, parsed.TmdbMovieId);
        Assert.True(parsed.HasExternalId);
    }

    [Theory]
    [InlineData("The Matrix", "Matrix, The", 1.0)]
    [InlineData("Inception", "Inception", 1.0)]
    [InlineData("Inception", "Inseption", 0.8, 0.99)]
    [InlineData("Completely Different", "Totally Unrelated", 0.0, 0.4)]
    public void Similarity_Scores_Are_Reasonable(string a, string b, double min, double max = 1.0)
    {
        var score = TitleSimilarity.Score(a, b);
        Assert.InRange(score, min, max);
    }
}

/// <summary>Automatic matcher rules: identifiers win, title/year needs the threshold.</summary>
public sealed class MetadataMatcherTests : IDisposable
{
    private readonly TestEnvironment _env = TestEnvironment.CreateAsync().GetAwaiter().GetResult();

    public void Dispose() => _env.Dispose();

    private MetadataMatcher CreateMatcher() => new(_env.Tmdb, _env.Settings);

    [Fact]
    public async Task TmdbId_Tag_Beats_Everything()
    {
        var parsed = _env.Parser.Parse("[tmdb-tv-1399] Something.Wrong.S01E01.mkv");
        var outcome = await CreateMatcher().MatchAsync(parsed);

        Assert.True(outcome.IsMatch);
        Assert.Equal(1399L, outcome.TmdbId);
        Assert.True(outcome.IsSeries);
        Assert.Equal(0.98, outcome.Confidence);
    }

    [Fact]
    public async Task ImdbId_Uses_Find_Endpoint()
    {
        _env.Tmdb.ImdbIndex["tt0111161"] = (27205, false);
        var parsed = _env.Parser.Parse("Movie [imdb-tt0111161].mkv");

        var outcome = await CreateMatcher().MatchAsync(parsed);

        Assert.Equal(27205L, outcome.TmdbId);
        Assert.Equal(0.98, outcome.Confidence);
    }

    [Fact]
    public async Task Title_Year_Match_Respects_Threshold()
    {
        _env.Settings.Save(new AppSettings { MinimumMatchConfidence = 0.60 });
        _env.Tmdb.MovieSearchResults =
        [
            new TmdbSearchCandidate(1, "Great Flick", 2020, "Great Flick", null, null, false, ""),
            new TmdbSearchCandidate(2, "Something Else Entirely", 2020, null, null, null, false, "")
        ];

        var good = await CreateMatcher().MatchAsync(_env.Parser.Parse("Great.Flick.2020.mkv"));
        Assert.True(good.IsMatch);
        Assert.Equal(1L, good.TmdbId);

        var bad = await CreateMatcher().MatchAsync(_env.Parser.Parse("No.Such.Title.Anywhere.2020.mkv"));
        Assert.False(bad.IsMatch);
    }

    [Fact]
    public async Task Wrong_Year_Lowers_Confidence_Below_Threshold()
    {
        _env.Settings.Save(new AppSettings { MinimumMatchConfidence = 0.60 });
        _env.Tmdb.MovieSearchResults =
        [
            new TmdbSearchCandidate(3, "Distant Movie", 1990, "Distant Movie", null, null, false, "")
        ];

        // Same title but 20 years apart: title sim 1.0*0.7 + 0 = 0.70 ≥ 0.60... use a stricter threshold.
        _env.Settings.Save(new AppSettings { MinimumMatchConfidence = 0.85 });
        var outcome = await CreateMatcher().MatchAsync(_env.Parser.Parse("Distant.Movie.2010.mkv"));

        Assert.False(outcome.IsMatch);
    }

    [Fact]
    public async Task No_Title_And_No_Id_Is_NoMatch()
    {
        var parsed = _env.Parser.Parse("S01E02.mkv"); // series marker, no title
        var outcome = await CreateMatcher().MatchAsync(parsed);

        Assert.False(outcome.IsMatch);
        Assert.Contains("title", outcome.Detail, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>Cart enqueue rules: title-level, duplicates prevented, layout options.</summary>
public sealed class UsbCartEnqueueTests : IDisposable
{
    private readonly TestEnvironment _env = TestEnvironment.CreateAsync().GetAwaiter().GetResult();

    public void Dispose() => _env.Dispose();

    private async Task<int> CreateMovieAsync(string title, int year, string fileName)
    {
        // Deliberately NOT under a library root so the clean-name fallback
        // layout applies (library-relative mirroring wins when it is).
        var inbox = Path.Combine(_env.Root, "inbox");
        Directory.CreateDirectory(inbox);
        var filePath = Path.Combine(inbox, fileName);
        await File.WriteAllTextAsync(filePath, "data");
        await using var db = await _env.Factory.CreateDbContextAsync();
        var movie = new MediaTitle { Type = MediaKind.Movie, Title = title, ReleaseYear = year };
        db.MediaTitles.Add(movie);
        await db.SaveChangesAsync();
        db.MediaFiles.Add(new MediaFile { MovieTitleId = movie.Id, FilePath = filePath, FileSize = 4 });
        await db.SaveChangesAsync();
        return movie.Id;
    }

    [Fact]
    public async Task EnqueueTitles_Prevents_Duplicates_And_Uses_Clean_Names()
    {
        var titleId = await CreateMovieAsync("Clean Movie", 2021, "Some.Release.Group.2021.mkv");

        var first = await _env.CopyService.EnqueueTitlesAsync(
            [titleId], Path.Combine(_env.LibraryDir, "Movies"), Path.Combine(_env.LibraryDir, "Series"),
            createPerTitleFolders: false, cleanFilenames: true);
        var second = await _env.CopyService.EnqueueTitlesAsync(
            [titleId], Path.Combine(_env.LibraryDir, "Movies"), Path.Combine(_env.LibraryDir, "Series"),
            createPerTitleFolders: false, cleanFilenames: true);

        Assert.Equal(1, first.Queued);
        Assert.Equal(0, second.Queued);
        Assert.Equal(1, second.AlreadyQueued);

        var jobs = await _env.CopyService.GetJobsAsync();
        var job = Assert.Single(jobs);
        Assert.Equal("Clean Movie (2021).mkv", job.RelativeTargetPath);
    }

    [Fact]
    public async Task EnqueueTitles_PerTitleFolders()
    {
        var titleId = await CreateMovieAsync("Folder Movie", 2019, "folder.movie.2019.mkv");

        await _env.CopyService.EnqueueTitlesAsync(
            [titleId], null, null, createPerTitleFolders: true, cleanFilenames: true);

        var jobs = await _env.CopyService.GetJobsAsync();
        Assert.Equal(Path.Combine("Folder Movie (2019)", "Folder Movie (2019).mkv"), Assert.Single(jobs).RelativeTargetPath);
    }

    [Fact]
    public async Task EnqueueTitles_Mirrors_Library_Relative_Path_When_Under_Root()
    {
        var moviesRoot = Path.Combine(_env.LibraryDir, "Movies");
        var deep = Path.Combine(moviesRoot, "Nice Movie (2020)", "nice.movie.2020.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(deep)!);
        await File.WriteAllTextAsync(deep, "data");
        await using var db = await _env.Factory.CreateDbContextAsync();
        var movie = new MediaTitle { Type = MediaKind.Movie, Title = "Nice Movie", ReleaseYear = 2020 };
        db.MediaTitles.Add(movie);
        await db.SaveChangesAsync();
        db.MediaFiles.Add(new MediaFile { MovieTitleId = movie.Id, FilePath = deep, FileSize = 4 });
        await db.SaveChangesAsync();

        await _env.CopyService.EnqueueTitlesAsync([movie.Id], moviesRoot, null, false, true);

        var job = Assert.Single(await _env.CopyService.GetJobsAsync());
        Assert.Equal(Path.Combine("Nice Movie (2020)", "nice.movie.2020.mkv"), job.RelativeTargetPath);
    }
}
