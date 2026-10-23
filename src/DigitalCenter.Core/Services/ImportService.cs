using DigitalCenter.Core.Configuration;
using DigitalCenter.Core.Data;
using DigitalCenter.Core.Logging;
using DigitalCenter.Core.Models;
using DigitalCenter.Core.Tmdb;
using Microsoft.EntityFrameworkCore;

namespace DigitalCenter.Core.Services;

/// <summary>Result of a completed (or failed) import confirmation.</summary>
public sealed record ImportOutcome(bool Success, int? MediaTitleId, string? TargetPath, string Message);

/// <summary>What one automatic organization pass did (see ImportService.AutoOrganizeAsync).</summary>
public sealed record AutoOrganizeResult(int Moved, int Failed, int LeftWaiting);

/// <summary>Everything the Management screen shows before the operator confirms a move.</summary>
public sealed record ImportPreview(
    int LogId,
    string FileName,
    string SourcePath,
    long? FileSize,
    bool IsSeries,
    string MatchedTitle,
    int? MatchedYear,
    long? TmdbId,
    double? Confidence,
    string DestinationFolder,
    string PlannedTargetPath,
    bool TargetExists,
    string? ExistingNote);

/// <summary>Import activity statistics for the Management dashboard.</summary>
public sealed record ImportStats(int Total, int Imported, int Skipped, int Failed, int NoMatch, int Unsupported, int AwaitingDestination, int Matching);

/// <summary>
/// The Watch Folder import workflow (DigitalCenter 5.0). Files are matched
/// automatically — external identifiers first, then title/year — and nothing
/// is moved before the operator has chosen a destination folder and confirmed
/// the move. Files without a reliable match are marked No Match and stay in
/// place. Because a file move cannot be made atomic with database writes, the
/// import records its progress in <see cref="MetadataImportLog.Stage"/>
/// checkpoints: Approved → MoveStarted → MovedToLibrary → RegisterStarted →
/// Imported. <see cref="RecoverInterruptedAsync"/> completes or safely fails
/// imports that died between two checkpoints.
/// </summary>
public sealed partial class ImportService
{
    private readonly IDbContextFactory<DigitalCenterDbContext> _factory;
    private readonly ITmdbMetadataProvider _tmdb;
    private readonly SettingsService _settings;
    private readonly PosterCache _posters;
    private readonly IAppLogger? _logger;

    public ImportService(
        IDbContextFactory<DigitalCenterDbContext> factory,
        ITmdbMetadataProvider tmdb,
        SettingsService settings,
        PosterCache posters,
        IAppLogger? logger = null)
    {
        _factory = factory;
        _tmdb = tmdb;
        _settings = settings;
        _posters = posters;
        _logger = logger;
    }

    // ---------------------------------------------------------------- queries

    public async Task<IReadOnlyList<MetadataImportLog>> GetActivityAsync(int take = 200, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.MetadataImportLogs
            .AsNoTracking()
            .OrderByDescending(l => l.Timestamp)
            .ThenByDescending(l => l.Id)
            .Take(take)
            .ToListAsync(ct);
    }

    public async Task<MetadataImportLog?> GetLogAsync(int logId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.MetadataImportLogs.AsNoTracking().FirstOrDefaultAsync(l => l.Id == logId, ct);
    }

    public async Task<ImportStats> GetStatsAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var statuses = await db.MetadataImportLogs
            .GroupBy(l => l.Status)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var total = statuses.Sum(s => s.Count);
        int Count(string status) => statuses.FirstOrDefault(s => s.Key == status)?.Count ?? 0;

        return new ImportStats(
            Total: total,
            Imported: Count(ImportStatuses.Imported),
            Skipped: Count(ImportStatuses.Rejected),
            Failed: Count(ImportStatuses.Failed),
            NoMatch: Count(ImportStatuses.NoMatch),
            Unsupported: Count(ImportStatuses.Unsupported),
            AwaitingDestination: Count(ImportStatuses.Matched),
            Matching: Count(ImportStatuses.Pending) + Count(ImportStatuses.Approved));
    }

    // --------------------------------------------------- activity reporting

    /// <summary>
    /// Raised while background import work runs, so the shell can report it in
    /// the ONE consolidated activity feed. Core stays UI-free: subscribers
    /// decide where a message is shown.
    /// </summary>
    public event Action<string>? ActivityReported;
}
