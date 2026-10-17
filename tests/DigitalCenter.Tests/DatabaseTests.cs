using DigitalCenter.Core.Data;
using DigitalCenter.Core.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace DigitalCenter.Tests;

/// <summary>
/// Database relationship and file-association rules: type CHECK constraints,
/// the MediaFile XOR rule, unique paths (case-insensitive), cascades and the
/// FTS5 index sync.
/// </summary>
public class DatabaseTests : IDisposable
{
    private readonly TestEnvironment _env;
    private readonly ITestOutputHelper _output;

    public DatabaseTests(ITestOutputHelper output)
    {
        _output = output;
        _env = TestEnvironment.CreateAsync().GetAwaiter().GetResult();
        _output.WriteLine($"Test data root: {_env.Root}");
    }

    [Fact]
    public void Migrations_Are_Applied()
    {
        using var db = _env.Factory.CreateDbContext();
        var applied = db.Database.SqlQuery<string>(
                $"SELECT MigrationId FROM __EFMigrationsHistory")
            .ToList();

        Assert.Contains(applied, id => id.EndsWith("InitialCreate", StringComparison.Ordinal));
    }

    [Fact]
    public void MediaTitle_Type_Is_Restricted_To_Movie_Or_Series()
    {
        using var db = _env.Factory.CreateDbContext();

        // Raw SQL bypasses EF's enum conversion, so the CHECK constraint fires directly.
        Assert.ThrowsAny<Microsoft.Data.Sqlite.SqliteException>(() =>
        {
            db.Database.ExecuteSqlRaw(
                "INSERT INTO MediaTitles (Type, Title, CreatedAt, UpdatedAt) VALUES ('Documentary', 'X', '2024-01-01', '2024-01-01')");
        });
    }

    [Fact]
    public void MediaFile_Must_Belong_To_Exactly_One_Owner()
    {
        using var db = _env.Factory.CreateDbContext();
        var title = new MediaTitle { Type = MediaKind.Movie, Title = "Orphan Test" };
        db.MediaTitles.Add(title);
        db.SaveChanges();

        // Neither owner -> rejected.
        db.MediaFiles.Add(new MediaFile { FilePath = "/a/orphan.mkv" });
        Assert.ThrowsAny<DbUpdateException>(() => db.SaveChanges());
        db.ChangeTracker.Clear();

        // Both owners -> rejected.
        var season = new Season { SeriesDetailsId = AddSeries(db, "Series X"), SeasonNumber = 1 };
        db.Seasons.Add(season);
        db.SaveChanges();
        var episode = new Episode { SeasonId = season.Id, EpisodeNumber = 1 };
        db.Episodes.Add(episode);
        db.SaveChanges();

        db.MediaFiles.Add(new MediaFile { FilePath = "/a/both.mkv", MovieTitleId = title.Id, EpisodeId = episode.Id });
        Assert.ThrowsAny<DbUpdateException>(() => db.SaveChanges());
    }

    [Fact]
    public void Duplicate_File_Paths_Are_Rejected_Case_Insensitively()
    {
        using var db = _env.Factory.CreateDbContext();
        var title = new MediaTitle { Type = MediaKind.Movie, Title = "Dup Test" };
        db.MediaTitles.Add(title);
        db.SaveChanges();

        db.MediaFiles.Add(new MediaFile { MovieTitleId = title.Id, FilePath = "/data/Movie.mkv" });
        db.SaveChanges();

        db.ChangeTracker.Clear();
        db.MediaFiles.Add(new MediaFile { MovieTitleId = title.Id, FilePath = "/DATA/MOVIE.MKV" });
        Assert.ThrowsAny<DbUpdateException>(() => db.SaveChanges());
    }

    [Fact]
    public void Deleting_A_Title_Cascades_To_Files()
    {
        using var db = _env.Factory.CreateDbContext();
        var title = new MediaTitle { Type = MediaKind.Movie, Title = "Cascade Test" };
        db.MediaTitles.Add(title);
        db.SaveChanges();
        db.MediaFiles.Add(new MediaFile { MovieTitleId = title.Id, FilePath = "/data/cascade.mkv" });
        db.SaveChanges();

        db.MediaTitles.Remove(title);
        db.SaveChanges();

        Assert.Equal(0, db.MediaFiles.Count(f => f.FilePath == "/data/cascade.mkv"));
    }

    [Fact]
    public void Deleting_A_Title_Cascades_To_Seasons_And_Episodes()
    {
        using var db = _env.Factory.CreateDbContext();
        var series = new MediaTitle { Type = MediaKind.Series, Title = "Cascade Series" };
        db.MediaTitles.Add(series);
        db.SaveChanges();
        var details = new SeriesDetails { MediaTitleId = series.Id };
        db.SeriesDetails.Add(details);
        db.SaveChanges();
        var season = new Season { SeriesDetailsId = details.MediaTitleId, SeasonNumber = 1 };
        db.Seasons.Add(season);
        db.SaveChanges();
        db.Episodes.Add(new Episode { SeasonId = season.Id, EpisodeNumber = 1 });
        db.SaveChanges();

        db.MediaTitles.Remove(series);
        db.SaveChanges();

        Assert.Empty(db.Episodes);
        Assert.Empty(db.Seasons);
        Assert.Empty(db.SeriesDetails);
    }

    [Fact]
    public void Fts_Index_Finds_Titles_And_People_And_Stays_In_Sync()
    {
        using var db = _env.Factory.CreateDbContext();
        Assert.True(_env.Initializer.FullTextSearchEnabled, "FTS5 should be available in the bundled SQLite build.");

        var title = new MediaTitle { Type = MediaKind.Movie, Title = "Star Wars" };
        db.MediaTitles.Add(title);
        db.SaveChanges();

        var person = new Person { Name = "Mark Hamill" };
        db.People.Add(person);
        db.SaveChanges();
        db.TitlePeople.Add(new TitlePerson { MediaTitleId = title.Id, PersonId = person.Id, Role = "Actor" });
        db.SaveChanges();

        Assert.Equal(title.Id, QuerySingleId("ham*"));
        Assert.Equal(title.Id, QuerySingleId("star*"));

        // Title updates propagate to the FTS index through triggers.
        title.Title = "Star Wars: A New Hope";
        db.SaveChanges();
        Assert.Equal(title.Id, QuerySingleId("new hope"));

        // Removing the person link removes them from the index as well.
        var link = db.TitlePeople.Single();
        db.TitlePeople.Remove(link);
        db.SaveChanges();
        Assert.Equal(0, QuerySingleId("ham*"));

        int QuerySingleId(string match)
        {
            var connection = db.Database.GetDbConnection();
            if (connection.State != System.Data.ConnectionState.Open)
            {
                connection.Open();
            }

            using var command = connection.CreateCommand();
            command.CommandText = "SELECT MediaTitleId FROM MediaTitleFts WHERE MediaTitleFts MATCH @m";
            var p = command.CreateParameter();
            p.ParameterName = "@m";
            p.Value = match;
            command.Parameters.Add(p);
            var result = command.ExecuteScalar();
            return result is null ? 0 : Convert.ToInt32(result);
        }
    }

    private static int AddSeries(DigitalCenterDbContext db, string title)
    {
        var series = new MediaTitle { Type = MediaKind.Series, Title = title };
        db.MediaTitles.Add(series);
        db.SaveChanges();
        db.SeriesDetails.Add(new SeriesDetails { MediaTitleId = series.Id });
        db.SaveChanges();
        return series.Id;
    }

    public void Dispose() => _env.Dispose();
}
