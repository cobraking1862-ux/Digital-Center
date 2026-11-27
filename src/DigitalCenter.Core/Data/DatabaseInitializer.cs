using DigitalCenter.Core.Logging;
using DigitalCenter.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace DigitalCenter.Core.Data;

/// <summary>
/// Applies pending EF Core migrations, installs the FTS5 index, ticket tables,
/// and drama catalog tables, and seeds default reference data. Called once at
/// application startup and safe to call repeatedly (all steps are idempotent).
/// </summary>
public sealed class DatabaseInitializer
{
    private readonly IDbContextFactory<DigitalCenterDbContext> _factory;
    private readonly IAppLogger? _logger;

    public DatabaseInitializer(IDbContextFactory<DigitalCenterDbContext> factory, IAppLogger? logger = null)
    {
        _factory = factory;
        _logger = logger;
    }

    /// <summary>True when the FTS5 index could be installed (false = LIKE fallback active).</summary>
    public bool FullTextSearchEnabled { get; private set; }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);

        _logger?.Info("Applying database migrations...");
        await db.Database.MigrateAsync(ct);

        // Enable SQLite WAL (Write-Ahead Logging) mode and busy_timeout so
        // concurrent background Watch Folder / Poster / Drama / Lookup writes
        // never block or starve catalog read queries on startup.
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=15000;", ct);

        _logger?.Info("Installing full-text search index (if supported)...");
        FullTextSearchEnabled = await FtsSupport.EnsureInstalledAsync(db, ct);
        if (!FullTextSearchEnabled)
        {
            _logger?.Warn("FTS5 is not available in this SQLite build; text search will fall back to LIKE.");
        }

        _logger?.Info("Ensuring Lookup Tickets table...");
        await TicketSchemaInitializer.EnsureInstalledAsync(db, ct);

        _logger?.Info("Ensuring Drama Catalog tables...");
        await DramaSchemaInitializer.EnsureInstalledAsync(db, ct);

        await SeedDefaultCategoriesAsync(db, ct);

        _logger?.Info("Database initialization complete.");
    }

    /// <summary>
    /// Seeds the documented default categories on first start (idempotent:
    /// existing names of the same type are never duplicated).
    /// </summary>
    private static async Task SeedDefaultCategoriesAsync(DigitalCenterDbContext db, CancellationToken ct)
    {
        var defaults = new (MediaKind Type, string Name)[]
        {
            (MediaKind.Movie, "Hollywood"),
            (MediaKind.Movie, "Bollywood"),
            (MediaKind.Movie, "Chinese"),
            (MediaKind.Movie, "Madrasi"),
            (MediaKind.Series, "English"),
            (MediaKind.Series, "Indian"),
            (MediaKind.Series, "Pakistani")
        };

        var existing = await db.Categories.ToListAsync(ct);
        var added = 0;
        foreach (var (type, name) in defaults)
        {
            if (existing.Any(c => c.Type == type && string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            db.Categories.Add(new Category { Type = type, Name = name });
            added++;
        }

        if (added > 0)
        {
            await db.SaveChangesAsync(ct);
        }
    }
}
