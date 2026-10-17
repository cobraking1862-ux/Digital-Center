using DigitalCenter.Core.Models;
using DigitalCenter.Core.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DigitalCenter.Tests;

public sealed class MaintenanceTests : IDisposable
{
    private readonly TestEnvironment _env = TestEnvironment.CreateAsync().Result;

    public void Dispose() => _env.Dispose();

    private async Task<int> AddMovieWithFileAsync(string title, string year, string relativePath, string content)
    {
        var fullPath = Path.Combine(_env.LibraryDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllTextAsync(fullPath, content);

        await using var db = await _env.Factory.CreateDbContextAsync();
        var mediaTitle = new MediaTitle
        {
            Type = MediaKind.Movie,
            Title = title,
            ReleaseYear = int.Parse(year)
        };
        db.MediaTitles.Add(mediaTitle);
        await db.SaveChangesAsync();
        db.MediaFiles.Add(new MediaFile
        {
            MovieTitleId = mediaTitle.Id,
            FilePath = fullPath,
            FileSize = new FileInfo(fullPath).Length
        });
        await db.SaveChangesAsync();
        return mediaTitle.Id;
    }

    [Fact]
    public async Task MissingFiles_Report_Finds_Deleted_Files()
    {
        await AddMovieWithFileAsync("Present", "2001", Path.Combine("Movies", "Hollywood", "Present (2001)", "a.mkv"), "aaa");
        await AddMovieWithFileAsync("Vanished", "2002", Path.Combine("Movies", "Hollywood", "Vanished (2002)", "b.mkv"), "bbb");
        // Delete the second file on disk.
        File.Delete(Path.Combine(_env.LibraryDir, "Movies", "Hollywood", "Vanished (2002)", "b.mkv"));

        var missing = await _env.Maintenance.GetMissingFilesAsync();

        var single = Assert.Single(missing);
        Assert.Contains("Vanished", single.TitleDisplay);
        Assert.Contains("b.mkv", single.FilePath);
    }

    [Fact]
    public async Task Duplicates_Report_Finds_Same_Name_And_Size_In_Two_Locations()
    {
        var content = "identical-content";
        await AddMovieWithFileAsync("Copy A", "2010", Path.Combine("Movies", "Hollywood", "Copy A (2010)", "same.mkv"), content);
        await AddMovieWithFileAsync("Copy B", "2011", Path.Combine("Movies", "Hollywood", "Copy B (2011)", "same.mkv"), content);

        var groups = await _env.Maintenance.GetDuplicateCandidatesAsync();

        var exact = groups.FirstOrDefault(g => g.Reason.Contains("Identical"));
        Assert.NotNull(exact);
        Assert.Equal(2, exact!.FilePaths.Count);
    }

    [Fact]
    public async Task Duplicates_Report_Finds_Multiple_Versions_Of_One_Movie()
    {
        await using var db = await _env.Factory.CreateDbContextAsync();
        var title = new MediaTitle { Type = MediaKind.Movie, Title = "Multi Version", ReleaseYear = 2015 };
        db.MediaTitles.Add(title);
        await db.SaveChangesAsync();
        foreach (var name in new[] { "v1.mkv", "v2.mkv" })
        {
            var path = Path.Combine(_env.LibraryDir, "Movies", "Hollywood", "Multi Version (2015)", name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, name);
            db.MediaFiles.Add(new MediaFile { MovieTitleId = title.Id, FilePath = path, FileSize = new FileInfo(path).Length });
        }

        await db.SaveChangesAsync();

        var groups = await _env.Maintenance.GetDuplicateCandidatesAsync();
        Assert.Contains(groups, g => g.Reason.Contains("possible alternate versions"));
    }

    [Fact]
    public async Task SearchIndex_Rebuild_Keeps_Search_Working()
    {
        await AddMovieWithFileAsync("The Matrix", "1999", Path.Combine("Movies", "Hollywood", "The Matrix (1999)", "m.mkv"), "m");

        await using (var db = await _env.Factory.CreateDbContextAsync())
        {
            var person = new Person { Name = "Keanu Reeves" };
            db.People.Add(person);
            await db.SaveChangesAsync();
            db.TitlePeople.Add(new TitlePerson { MediaTitleId = 1, PersonId = person.Id, Role = "Actor" });
            await db.SaveChangesAsync();
        }

        var count = await _env.Maintenance.RebuildSearchIndexAsync();
        Assert.Equal(1, count);

        // The actor is still findable after the rebuild.
        var page = await _env.QueryService.QueryAsync(new LibraryFilter { SearchText = "Keanu" });
        Assert.Equal(1, page.TotalMatches);
    }

    [Fact]
    public async Task Csv_Export_Import_Round_Trip_Is_Additive()
    {
        await AddMovieWithFileAsync("Round Trip", "2020",
            Path.Combine("Movies", "Hollywood", "Round Trip (2020)", "rt.mkv"), "rt");
        await using (var db = await _env.Factory.CreateDbContextAsync())
        {
            var genre = new Genre { Name = "Thriller" };
            db.Genres.Add(genre);
            await db.SaveChangesAsync();
            db.TitleGenres.Add(new TitleGenre { MediaTitleId = 1, GenreId = genre.Id });
            await db.SaveChangesAsync();
        }

        var csvFile = Path.Combine(_env.Root, "export.csv");
        var rows = await _env.Maintenance.ExportCsvAsync(csvFile);
        Assert.Equal(1, rows);

        // Importing the export into the same catalog must not duplicate anything.
        var summary = await _env.Maintenance.ImportCsvAsync(csvFile);

        Assert.Equal(1, summary.RowsRead);
        Assert.Equal(0, summary.TitlesCreated);
        Assert.Equal(1, summary.TitlesMerged);
        Assert.Equal(0, summary.FilesRegistered); // File path already registered.
        Assert.Equal(0, summary.FilesMissing);

        await using var verify = await _env.Factory.CreateDbContextAsync();
        Assert.Single(await verify.MediaTitles.ToListAsync());
        Assert.Single(await verify.MediaFiles.ToListAsync());
    }

    [Fact]
    public async Task Csv_Import_Creates_Titles_And_Registers_Existing_Files()
    {
        // A file that exists on disk but is not registered yet.
        var relative = Path.Combine("Movies", "Bollywood", "Imported (2018)", "imported.mkv");
        var fullPath = Path.Combine(_env.LibraryDir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllTextAsync(fullPath, "imported");

        var csv = string.Join("\r\n",
            "Type,Title,ReleaseYear,Categories,Genres,FilePath",
            $"Movie,Imported,2018,Bollywood,Drama,{fullPath}",
            "Series,Some Show,2021,English,,",
            "Movie,Broken,,Hollywood,,Z:\\definitely-not-there.mkv");

        var csvFile = Path.Combine(_env.Root, "import.csv");
        await File.WriteAllTextAsync(csvFile, csv);

        var summary = await _env.Maintenance.ImportCsvAsync(csvFile);

        Assert.Equal(3, summary.RowsRead);
        Assert.Equal(3, summary.TitlesCreated);
        Assert.Equal(1, summary.FilesRegistered);
        Assert.Equal(1, summary.FilesMissing); // Z:\ path does not exist.
        Assert.Equal(0, summary.RowsSkipped);

        await using var db = await _env.Factory.CreateDbContextAsync();
        var imported = await db.MediaTitles.Include(t => t.Files)
            .Include(t => t.Genres).ThenInclude(tg => tg.Genre)
            .Include(t => t.Categories).ThenInclude(tc => tc.Category)
            .SingleAsync(t => t.Title == "Imported");
        Assert.Equal("Drama", imported.Genres.Single().Genre.Name);
        Assert.Equal("Bollywood", imported.Categories.Single().Category.Name);
        Assert.Equal(fullPath, imported.Files.Single().FilePath);

        var series = await db.MediaTitles.Include(t => t.SeriesDetails).SingleAsync(t => t.Title == "Some Show");
        Assert.NotNull(series.SeriesDetails);
    }

    [Fact]
    public async Task Csv_Import_Without_Header_Columns_Throws()
    {
        var csvFile = Path.Combine(_env.Root, "bad.csv");
        await File.WriteAllTextAsync(csvFile, "Name,Year\r\nFoo,2020");

        await Assert.ThrowsAsync<InvalidOperationException>(() => _env.Maintenance.ImportCsvAsync(csvFile));
    }

    [Fact]
    public async Task Csv_Quoting_Survives_Commas_And_Quotes()
    {
        var fullPath = Path.Combine(_env.LibraryDir, "Movies", "Hollywood", "Quoted (2013)", "q.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllTextAsync(fullPath, "q");
        await using var db = await _env.Factory.CreateDbContextAsync();
        db.MediaTitles.Add(new MediaTitle
        {
            Type = MediaKind.Movie,
            Title = "Movie, with \"quotes\"",
            ReleaseYear = 2013,
            Synopsis = "A plot, with commas \"and quotes\"."
        });
        await db.SaveChangesAsync();

        var csvFile = Path.Combine(_env.Root, "quoted.csv");
        await _env.Maintenance.ExportCsvAsync(csvFile);

        // Round-trip through a fresh catalog directory (import into same db is additive).
        var summary = await _env.Maintenance.ImportCsvAsync(csvFile);
        Assert.Equal(0, summary.RowsSkipped);
        Assert.Equal(1, summary.TitlesMerged);
    }
}
