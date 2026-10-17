using DigitalCenter.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace DigitalCenter.Core.Data;

/// <summary>
/// EF Core context for the DigitalCenter catalog (SQLite).
/// Schema decisions of note:
///  - MediaTitles.Type is stored as TEXT restricted by CK_MediaTitles_Type to ('Movie','Series').
///  - MediaFiles has an XOR association: exactly one of MovieTitleId / EpisodeId (CK_MediaFiles_Association).
///  - MediaFiles.FilePath is unique with COLLATE NOCASE so Windows path duplicates are rejected.
///  - Foreign keys cascade so deleting a title/season/episode removes its dependent rows.
/// </summary>
public class DigitalCenterDbContext : DbContext
{
    public DigitalCenterDbContext(DbContextOptions<DigitalCenterDbContext> options)
        : base(options)
    {
    }

    public DbSet<MediaTitle> MediaTitles => Set<MediaTitle>();
    public DbSet<SeriesDetails> SeriesDetails => Set<SeriesDetails>();
    public DbSet<Season> Seasons => Set<Season>();
    public DbSet<Episode> Episodes => Set<Episode>();
    public DbSet<MediaFile> MediaFiles => Set<MediaFile>();
    public DbSet<Person> People => Set<Person>();
    public DbSet<TitlePerson> TitlePeople => Set<TitlePerson>();
    public DbSet<Genre> Genres => Set<Genre>();
    public DbSet<TitleGenre> TitleGenres => Set<TitleGenre>();
    public DbSet<Country> Countries => Set<Country>();
    public DbSet<TitleCountry> TitleCountries => Set<TitleCountry>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<TitleCategory> TitleCategories => Set<TitleCategory>();
    public DbSet<MetadataImportLog> MetadataImportLogs => Set<MetadataImportLog>();
    public DbSet<CopyJob> CopyJobs => Set<CopyJob>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ConfigureMediaTitle(modelBuilder);
        ConfigureSeries(modelBuilder);
        ConfigureMediaFile(modelBuilder);
        ConfigureAssociations(modelBuilder);
        ConfigureImportLog(modelBuilder);
    }

    private static void ConfigureMediaTitle(ModelBuilder b)
    {
        b.Entity<MediaTitle>(e =>
        {
            e.ToTable("MediaTitles", t =>
            {
                t.HasCheckConstraint("CK_MediaTitles_Type", "Type IN ('Movie', 'Series')");
            });

            e.Property(x => x.Type).HasConversion<string>().IsRequired().HasMaxLength(10);
            e.Property(x => x.Title).IsRequired().HasMaxLength(400).UseCollation("NOCASE");
            e.Property(x => x.OriginalTitle).HasMaxLength(400);
            e.Property(x => x.Synopsis).HasMaxLength(4000);
            e.Property(x => x.PosterPath).HasMaxLength(600);
            e.Property(x => x.BackdropPath).HasMaxLength(600);
            e.Property(x => x.TmdbPosterPath).HasMaxLength(600);
            e.Property(x => x.TmdbBackdropPath).HasMaxLength(600);
            e.Property(x => x.TrailerUrl).HasMaxLength(600);
            e.Property(x => x.TrailerName).HasMaxLength(300);
            e.Property(x => x.OriginalLanguage).HasMaxLength(20);
            e.Property(x => x.ImdbId).HasMaxLength(20);
            e.Property(x => x.MetadataOverrides).HasMaxLength(1000);

            e.HasIndex(x => x.Title);
            e.HasIndex(x => x.ReleaseYear);
            e.HasIndex(x => x.Type);
            e.HasIndex(x => x.TmdbId).IsUnique();
            e.HasIndex(x => x.ImdbId).IsUnique();
            e.HasIndex(x => x.OriginalLanguage);
        });
    }

    private static void ConfigureSeries(ModelBuilder b)
    {
        b.Entity<SeriesDetails>(e =>
        {
            e.ToTable("SeriesDetails");
            e.HasKey(x => x.MediaTitleId);
            e.Property(x => x.AiringStatus).HasMaxLength(60);

            e.HasOne(x => x.MediaTitle)
                .WithOne(t => t.SeriesDetails)
                .HasForeignKey<SeriesDetails>(x => x.MediaTitleId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Season>(e =>
        {
            e.ToTable("Seasons");
            e.HasIndex(x => new { x.SeriesDetailsId, x.SeasonNumber }).IsUnique();

            e.HasOne(x => x.SeriesDetails)
                .WithMany(sd => sd.Seasons)
                .HasForeignKey(x => x.SeriesDetailsId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Episode>(e =>
        {
            e.ToTable("Episodes");
            e.HasIndex(x => new { x.SeasonId, x.EpisodeNumber }).IsUnique();
            e.Property(x => x.Title).HasMaxLength(400);

            e.HasOne(x => x.Season)
                .WithMany(s => s.Episodes)
                .HasForeignKey(x => x.SeasonId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }

    private static void ConfigureMediaFile(ModelBuilder b)
    {
        b.Entity<MediaFile>(e =>
        {
            e.ToTable("MediaFiles", t =>
            {
                t.HasCheckConstraint(
                    "CK_MediaFiles_Association",
                    "(MovieTitleId IS NOT NULL AND EpisodeId IS NULL) OR (MovieTitleId IS NULL AND EpisodeId IS NOT NULL)");
            });

            e.Property(x => x.FilePath).IsRequired().HasMaxLength(1000).UseCollation("NOCASE");
            e.Property(x => x.AddedAt).IsRequired();

            e.HasIndex(x => x.FilePath).IsUnique();
            e.HasIndex(x => x.MovieTitleId);
            e.HasIndex(x => x.EpisodeId);

            e.HasOne(x => x.MovieTitle)
                .WithMany(t => t.Files)
                .HasForeignKey(x => x.MovieTitleId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasOne(x => x.Episode)
                .WithMany(ep => ep.Files)
                .HasForeignKey(x => x.EpisodeId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }

    private static void ConfigureAssociations(ModelBuilder b)
    {
        b.Entity<Person>(e =>
        {
            e.ToTable("People");
            e.Property(x => x.Name).IsRequired().HasMaxLength(300).UseCollation("NOCASE");
            e.Property(x => x.ProfilePath).HasMaxLength(600);
            e.HasIndex(x => x.Name).IsUnique();
            e.HasIndex(x => x.TmdbPersonId).IsUnique();
        });

        b.Entity<TitlePerson>(e =>
        {
            e.ToTable("TitlePeople");
            e.HasKey(x => new { x.MediaTitleId, x.PersonId, x.Role });
            e.Property(x => x.Role).IsRequired().HasMaxLength(50);
            e.Property(x => x.Character).HasMaxLength(300);
            e.HasIndex(x => x.PersonId);

            e.HasOne(x => x.MediaTitle)
                .WithMany(t => t.People)
                .HasForeignKey(x => x.MediaTitleId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasOne(x => x.Person)
                .WithMany(p => p.Titles)
                .HasForeignKey(x => x.PersonId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Genre>(e =>
        {
            e.ToTable("Genres");
            e.Property(x => x.Name).IsRequired().HasMaxLength(100).UseCollation("NOCASE");
            e.HasIndex(x => x.Name).IsUnique();
        });

        b.Entity<TitleGenre>(e =>
        {
            e.ToTable("TitleGenres");
            e.HasKey(x => new { x.MediaTitleId, x.GenreId });
            e.HasIndex(x => x.GenreId);

            e.HasOne(x => x.MediaTitle)
                .WithMany(t => t.Genres)
                .HasForeignKey(x => x.MediaTitleId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasOne(x => x.Genre)
                .WithMany(g => g.Titles)
                .HasForeignKey(x => x.GenreId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Country>(e =>
        {
            e.ToTable("Countries");
            e.Property(x => x.Name).IsRequired().HasMaxLength(100).UseCollation("NOCASE");
            e.HasIndex(x => x.Name).IsUnique();
        });

        b.Entity<TitleCountry>(e =>
        {
            e.ToTable("TitleCountries");
            e.HasKey(x => new { x.MediaTitleId, x.CountryId });
            e.HasIndex(x => x.CountryId);

            e.HasOne(x => x.MediaTitle)
                .WithMany(t => t.Countries)
                .HasForeignKey(x => x.MediaTitleId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasOne(x => x.Country)
                .WithMany(c => c.Titles)
                .HasForeignKey(x => x.CountryId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Category>(e =>
        {
            e.ToTable("Categories", t =>
            {
                t.HasCheckConstraint("CK_Categories_Type", "Type IN ('Movie', 'Series')");
            });

            e.Property(x => x.Type).HasConversion<string>().IsRequired().HasMaxLength(10);
            e.Property(x => x.Name).IsRequired().HasMaxLength(100).UseCollation("NOCASE");
            e.HasIndex(x => new { x.Name, x.Type }).IsUnique();
        });

        b.Entity<TitleCategory>(e =>
        {
            e.ToTable("TitleCategories");
            e.HasKey(x => new { x.MediaTitleId, x.CategoryId });
            e.HasIndex(x => x.CategoryId);

            e.HasOne(x => x.MediaTitle)
                .WithMany(t => t.Categories)
                .HasForeignKey(x => x.MediaTitleId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasOne(x => x.Category)
                .WithMany(c => c.Titles)
                .HasForeignKey(x => x.CategoryId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }

    private static void ConfigureImportLog(ModelBuilder b)
    {
        b.Entity<MetadataImportLog>(e =>
        {
            e.ToTable("MetadataImportLogs");
            e.Property(x => x.SourceFile).IsRequired().HasMaxLength(600);
            e.Property(x => x.SourcePath).HasMaxLength(1000);
            e.Property(x => x.Status).IsRequired().HasMaxLength(30);
            e.Property(x => x.Stage).HasMaxLength(30);
            e.Property(x => x.MetadataSource).HasMaxLength(60);
            e.Property(x => x.ErrorDetails).HasMaxLength(2000);
            e.Property(x => x.ParsedTitle).HasMaxLength(400);
            e.Property(x => x.CategoryName).HasMaxLength(100);
            e.Property(x => x.TargetPath).HasMaxLength(1000);
            e.Property(x => x.Note).HasMaxLength(1000);
            e.Property(x => x.ChosenFolder).HasMaxLength(1000);

            // History survives the deletion of the linked title.
            e.HasOne(x => x.MediaTitle)
                .WithMany()
                .HasForeignKey(x => x.MediaTitleId)
                .OnDelete(DeleteBehavior.SetNull);

            e.HasIndex(x => x.Status);
            e.HasIndex(x => x.Timestamp);
            // One import log per source path: re-detection must never create
            // duplicate pending entries for the same Watch Folder file.
            e.HasIndex(x => x.SourcePath).IsUnique();
        });

        b.Entity<CopyJob>(e =>
        {
            e.ToTable("CopyJobs");
            e.Property(x => x.SourcePath).IsRequired().HasMaxLength(1000);
            e.Property(x => x.RelativeTargetPath).IsRequired().HasMaxLength(600);
            e.Property(x => x.FileName).IsRequired().HasMaxLength(300);
            e.Property(x => x.TitleDisplay).HasMaxLength(400);
            e.Property(x => x.Status).IsRequired().HasMaxLength(30);
            e.Property(x => x.ErrorMessage).HasMaxLength(1000);
            e.Property(x => x.ConflictNote).HasMaxLength(300);
            e.Property(x => x.DestinationRoot).HasMaxLength(300);

            e.HasOne(x => x.MediaFile)
                .WithMany()
                .HasForeignKey(x => x.MediaFileId)
                .OnDelete(DeleteBehavior.SetNull);

            e.HasIndex(x => x.Status);
            e.HasIndex(x => x.MediaFileId);
            e.HasIndex(x => x.QueuedAt);
        });
    }

    public override int SaveChanges()
    {
        TouchTimestamps();
        return base.SaveChanges();
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        TouchTimestamps();
        return base.SaveChangesAsync(cancellationToken);
    }

    private void TouchTimestamps()
    {
        var now = DateTime.UtcNow;
        foreach (var entry in ChangeTracker.Entries<MediaTitle>())
        {
            if (entry.State == EntityState.Added)
            {
                entry.Entity.CreatedAt = now;
                entry.Entity.UpdatedAt = now;
            }
            else if (entry.State == EntityState.Modified)
            {
                entry.Entity.UpdatedAt = now;
            }
        }

        foreach (var entry in ChangeTracker.Entries<MediaFile>())
        {
            if (entry.State == EntityState.Added && entry.Entity.AddedAt == default)
            {
                entry.Entity.AddedAt = now;
            }
        }
    }
}
