using DigitalCenter.Core.Models;
using DigitalCenter.Core.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DigitalCenter.Tests;

/// <summary>
/// Library query behavior: free-text search (FTS5 and LIKE fallback),
/// structured filters, sorting and match counts.
/// </summary>
public class SearchTests : IDisposable
{
    private readonly TestEnvironment _env;
    private readonly LibraryQueryService _query;
    private readonly LibraryQueryService _likeQuery;

    public SearchTests()
    {
        _env = TestEnvironment.CreateAsync().GetAwaiter().GetResult();
        _query = new LibraryQueryService(_env.Factory, ftsEnabled: true);
        _likeQuery = new LibraryQueryService(_env.Factory, ftsEnabled: false);
        Seed().GetAwaiter().GetResult();
    }

    private async Task Seed()
    {
        using var db = _env.Factory.CreateDbContext();

        var starWars = new MediaTitle { Type = MediaKind.Movie, Title = "Star Wars", ReleaseYear = 1977 };
        var starTrek = new MediaTitle { Type = MediaKind.Movie, Title = "Star Trek", ReleaseYear = 2009 };
        var matrix = new MediaTitle { Type = MediaKind.Movie, Title = "The Matrix", ReleaseYear = 1999 };
        var breakingBad = new MediaTitle { Type = MediaKind.Series, Title = "Breaking Bad", ReleaseYear = 2008 };

        db.MediaTitles.AddRange(starWars, starTrek, matrix, breakingBad);

        var genre = new Genre { Name = "Sci-Fi" };
        var country = new Country { Name = "United States" };
        db.Genres.Add(genre);
        db.Countries.Add(country);
        db.SaveChanges();

        // Categories are no longer seeded by the initializer; create one here.
        var category = new Category { Name = "Hollywood", Type = MediaKind.Movie };
        db.Categories.Add(category);
        db.SaveChanges();

        db.TitleGenres.Add(new TitleGenre { MediaTitleId = matrix.Id, GenreId = genre.Id });
        db.TitleCategories.Add(new TitleCategory { MediaTitleId = matrix.Id, CategoryId = category.Id });
        db.TitleCountries.Add(new TitleCountry { MediaTitleId = starWars.Id, CountryId = country.Id });

        var person = new Person { Name = "Mark Hamill" };
        db.People.Add(person);
        db.SaveChanges();
        db.TitlePeople.Add(new TitlePerson { MediaTitleId = starWars.Id, PersonId = person.Id, Role = "Actor" });

        db.MediaFiles.Add(new MediaFile { MovieTitleId = matrix.Id, FilePath = "/data/the-matrix.mkv", FileSize = 500 });
        db.SaveChanges();

        var details = new SeriesDetails { MediaTitleId = breakingBad.Id };
        db.SeriesDetails.Add(details);
        db.SaveChanges();
        var season = new Season { SeriesDetailsId = details.MediaTitleId, SeasonNumber = 1 };
        db.Seasons.Add(season);
        db.SaveChanges();
        db.Episodes.Add(new Episode { SeasonId = season.Id, EpisodeNumber = 1, Title = "Pilot" });
        db.SaveChanges();
    }

    [Fact]
    public async Task Text_Search_Finds_Titles_Via_Fts()
    {
        var page = await _query.QueryAsync(new LibraryFilter { SearchText = "star" });

        Assert.Equal(2, page.TotalMatches);
        Assert.Equal(2, page.Items.Count);
    }

    [Fact]
    public async Task Text_Search_Finds_Titles_Via_Like_Fallback()
    {
        var page = await _likeQuery.QueryAsync(new LibraryFilter { SearchText = "star" });

        Assert.Equal(2, page.TotalMatches);
    }

    [Fact]
    public async Task Text_Search_Finds_People()
    {
        var page = await _query.QueryAsync(new LibraryFilter { SearchText = "hamill" });

        Assert.Equal(1, page.TotalMatches);
        Assert.Equal("Star Wars", page.Items.Single().Title);
    }

    [Fact]
    public async Task Type_Filter()
    {
        var series = await _query.QueryAsync(new LibraryFilter { Type = MediaKind.Series });
        var movies = await _query.QueryAsync(new LibraryFilter { Type = MediaKind.Movie });

        Assert.Single(series.Items);
        Assert.Equal("Breaking Bad", series.Items.Single().Title);
        Assert.Equal(3, movies.TotalMatches);
    }

    [Fact]
    public async Task Year_Range_Filter()
    {
        // 1990-2005 only matches The Matrix (1999); Breaking Bad (2008) and
        // Star Trek (2009) are outside the upper bound.
        var page = await _query.QueryAsync(new LibraryFilter { YearFrom = 1990, YearTo = 2005 });

        Assert.Equal(1, page.TotalMatches);
        Assert.Equal("The Matrix", page.Items.Single().Title);
    }

    [Fact]
    public async Task Genre_Country_Category_Filters()
    {
        var byGenre = await _query.QueryAsync(new LibraryFilter { Genre = "Sci-Fi" });
        var byCategory = await _query.QueryAsync(new LibraryFilter { Category = "Hollywood" });
        var byCountry = await _query.QueryAsync(new LibraryFilter { Country = "United States" });

        Assert.Equal("The Matrix", byGenre.Items.Single().Title);
        Assert.Equal("The Matrix", byCategory.Items.Single().Title);
        Assert.Equal("Star Wars", byCountry.Items.Single().Title);
    }

    [Fact]
    public async Task Sorting_By_Title_And_Year()
    {
        var byYearDesc = await _query.QueryAsync(new LibraryFilter { SortBy = "Year", SortDescending = true });
        Assert.Equal("Star Trek", byYearDesc.Items.First().Title);

        var byTitleAsc = await _query.QueryAsync(new LibraryFilter { SortBy = "Title" });
        Assert.Equal("Breaking Bad", byTitleAsc.Items.First().Title);
    }

    [Fact]
    public async Task File_Availability_And_Size_Are_Reported()
    {
        var page = await _query.QueryAsync(new LibraryFilter { SearchText = "matrix" });

        var matrix = page.Items.Single();
        Assert.Equal(1, matrix.FileCount);
        Assert.Equal(0, matrix.FilesOnDisk); // /data/the-matrix.mkv does not exist in this environment
        Assert.Equal(500, matrix.TotalSize);
    }

    [Fact]
    public async Task Combined_Filters_Narrow_Results()
    {
        var page = await _query.QueryAsync(new LibraryFilter
        {
            SearchText = "star",
            Type = MediaKind.Movie,
            YearTo = 1980
        });

        Assert.Equal(1, page.TotalMatches);
        Assert.Equal("Star Wars", page.Items.Single().Title);
    }

    public void Dispose() => _env.Dispose();
}
