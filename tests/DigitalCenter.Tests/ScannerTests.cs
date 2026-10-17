using DigitalCenter.Core.Models;
using DigitalCenter.Core.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace DigitalCenter.Tests;

/// <summary>
/// End-to-end library scanning against a temporary folder tree: registration,
/// title grouping, series/season/episode structure, categories, duplicate
/// prevention on rescan, and unparsed-file reporting.
/// </summary>
public class ScannerTests : IDisposable
{
    private readonly TestEnvironment _env;
    private readonly ITestOutputHelper _output;

    public ScannerTests(ITestOutputHelper output)
    {
        _output = output;
        _env = TestEnvironment.CreateAsync().GetAwaiter().GetResult();
    }

    private string CreateLibrary()
    {
        var library = _env.NewLibraryDir();
        CreateFile(Path.Combine(library, "Movies", "Hollywood", "Inception (2010).mkv"));
        CreateFile(Path.Combine(library, "Movies", "Hollywood", "Inception.2010.WEBRip.mkv"));
        CreateFile(Path.Combine(library, "Movies", "Bollywood", "Interstellar.2014.1080p.mkv"));
        CreateFile(Path.Combine(library, "Movies", "Bollywood", "readme.txt"));
        CreateFile(Path.Combine(library, "Series", "English", "Example Series", "Season 01", "Example Series S01E01.mkv"));
        CreateFile(Path.Combine(library, "Series", "English", "Example Series", "Season 01", "Example.Series.S01E02.mkv"));
        CreateFile(Path.Combine(library, "Series", "English", "Example Series", "Season 02", "Example Series S02E01.mkv"));
        return library;
    }

    private static void CreateFile(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[] { 1, 2, 3, 4 });
    }

    [Fact]
    public async Task Scan_Registers_Movies_Series_Without_Hierarchy()
    {
        var library = CreateLibrary();

        var result = await _env.Scanner.ScanAsync(library);

        _output.WriteLine($"seen={result.FilesSeen} movies={result.MovieFilesRegistered} episodes={result.EpisodeFilesRegistered} " +
                          $"titles={result.NewTitlesCreated} seasons={result.NewSeasonsCreated} episodes_new={result.NewEpisodesCreated} " +
                          $"unsupported={result.UnsupportedFiles}");

        Assert.Equal(7, result.FilesSeen);
        Assert.Equal(1, result.UnsupportedFiles); // readme.txt
        Assert.Equal(3, result.MovieFilesRegistered);
        Assert.Equal(3, result.EpisodeFilesRegistered);
        Assert.Equal(3, result.NewTitlesCreated); // Inception, Interstellar, Example Series

        using var db = _env.Factory.CreateDbContext();

        var inception = await db.MediaTitles.Include(t => t.Files)
            .SingleAsync(t => t.Title == "Inception" && t.Type == MediaKind.Movie);
        Assert.Equal(2010, inception.ReleaseYear);
        Assert.Equal(2, inception.Files.Count); // both versions grouped under one title

        var series = await db.MediaTitles.Include(t => t.SeriesDetails).ThenInclude(sd => sd!.Seasons).ThenInclude(s => s.Episodes)
            .SingleAsync(t => t.Title == "Example Series" && t.Type == MediaKind.Series);
        Assert.NotNull(series.SeriesDetails);
        Assert.Equal(2, series.SeriesDetails!.Seasons.Count);
        Assert.Equal(3, series.SeriesDetails.Seasons.Sum(s => s.Episodes.Count));

        var episodeFile = await db.MediaFiles.Include(f => f.Episode).ThenInclude(e => e!.Season)
            .SingleAsync(f => f.EpisodeId != null && f.Episode!.EpisodeNumber == 2);
        Assert.Equal(1, episodeFile.Episode!.Season.SeasonNumber);
    }

    [Fact]
    public async Task Rescan_Does_Not_Duplicate_Anything()
    {
        var library = CreateLibrary();
        await _env.Scanner.ScanAsync(library);

        using (var db = _env.Factory.CreateDbContext())
        {
            Assert.Equal(3, db.MediaTitles.Count());
            Assert.Equal(6, db.MediaFiles.Count());
            Assert.Equal(3, db.Episodes.Count());
        }

        var second = await _env.Scanner.ScanAsync(library);

        Assert.Equal(6, second.DuplicatePathsSkipped);
        Assert.Equal(0, second.MovieFilesRegistered);
        Assert.Equal(0, second.EpisodeFilesRegistered);

        using (var db = _env.Factory.CreateDbContext())
        {
            Assert.Equal(3, db.MediaTitles.Count());
            Assert.Equal(6, db.MediaFiles.Count());
            Assert.Equal(3, db.Episodes.Count());
        }
    }

    [Fact]
    public async Task Series_Domain_File_Without_Marker_Is_Reported_Not_Silently_Moved()
    {
        var library = _env.NewLibraryDir();
        CreateFile(Path.Combine(library, "Series", "English", "Mystery Show", "Season 01", "totally_unparsed.mkv"));

        var result = await _env.Scanner.ScanAsync(library);

        Assert.Equal(0, result.MovieFilesRegistered);
        var skipped = Assert.Single(result.Skipped);
        Assert.StartsWith("Unparsed", skipped.Reason, StringComparison.Ordinal);
        Assert.Contains($"Series{Path.DirectorySeparatorChar}", skipped.Path, StringComparison.Ordinal);
        Assert.DoesNotContain($"Movies{Path.DirectorySeparatorChar}", skipped.Path, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Scan_Without_Library_Directory_Throws()
    {
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() =>
            _env.Scanner.ScanAsync(Path.Combine(_env.Root, "does-not-exist")));
    }

    public void Dispose() => _env.Dispose();
}
