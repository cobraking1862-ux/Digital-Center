using DigitalCenter.Core.Logging;
using DigitalCenter.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace DigitalCenter.Core.Data;

/// <summary>
/// Applies pending EF Core migrations, installs the FTS5 index and seeds
/// default reference data. Called once at application startup and safe to
/// call repeatedly (all steps are idempotent).
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

        _logger?.Info("Installing full-text search index (if supported)...");
        FullTextSearchEnabled = await FtsSupport.EnsureInstalledAsync(db, ct);
        if (!FullTextSearchEnabled)
        {
            _logger?.Warn("FTS5 is not available in this SQLite build; text search will fall back to LIKE.");
        }

        _logger?.Info("Database initialization complete.");
    }
}
