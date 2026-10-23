using DigitalCenter.Core.Configuration;
using DigitalCenter.Core.Data;
using DigitalCenter.Core.Logging;
using DigitalCenter.Core.Models;
using DigitalCenter.Core.Tmdb;
using Microsoft.EntityFrameworkCore;

namespace DigitalCenter.Core.Services;

/// <summary>
/// ImportService, part 4 of 5: confirming. Creates or reuses the catalog entry,
/// moves the file into the library and recovers imports interrupted mid-move.
/// </summary>
public sealed partial class ImportService
{
    // -------------------------------------------------------------- confirm

    /// <summary>
    /// Confirms the move of a matched file into the chosen destination folder
    /// (steps 7-10 of the workflow). Nothing runs before this call.
    /// </summary>
    public async Task<ImportOutcome> ConfirmImportAsync(int logId, string? destinationFolder = null, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var log = await db.MetadataImportLogs.FirstOrDefaultAsync(l => l.Id == logId, ct)
                  ?? throw new InvalidOperationException("The selected import no longer exists.");

        if (log.Status == ImportStatuses.Approved)
        {
            // A previous confirmation crashed before the move; resume it.
            return await ExecuteApprovedImportAsync(db, log, useTmdb: false, ct);
        }

        if (log.Status != ImportStatuses.Matched)
        {
            throw new InvalidOperationException(
                $"Only matched imports can be confirmed (current status: {ImportStatusLabels.For(log.Status)}).");
        }

        var settings = _settings.Load();
        var folder = destinationFolder ?? log.ChosenFolder ?? GetDefaultDestinationFolder(log.IsSeries, settings);
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            throw new InvalidOperationException("Choose an existing destination folder before confirming the move.");
        }

        log.ChosenFolder = Path.GetFullPath(folder);
        log.Status = ImportStatuses.Approved;
        log.Stage = ImportStages.Approved;
        log.ErrorDetails = null;
        await db.SaveChangesAsync(ct); // Checkpoint: crash here → recovery re-runs the whole pipeline.

        return await ExecuteApprovedImportAsync(db, log, useTmdb: true, ct);
    }

    /// <summary>Skips a matched file; the file stays in the Watch Folder untouched.</summary>
    public async Task SkipAsync(int logId, string? reason = null, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var log = await db.MetadataImportLogs.FirstOrDefaultAsync(l => l.Id == logId, ct)
                  ?? throw new InvalidOperationException("The selected import no longer exists.");

        if (log.Status != ImportStatuses.Matched && log.Status != ImportStatuses.Pending && log.Status != ImportStatuses.NoMatch)
        {
            throw new InvalidOperationException($"Only waiting imports can be skipped (current status: {ImportStatusLabels.For(log.Status)}).");
        }

        log.Status = ImportStatuses.Rejected;
        log.ErrorDetails = string.IsNullOrWhiteSpace(reason) ? "Skipped by the operator." : reason;
        await db.SaveChangesAsync(ct);
        _logger?.Info($"Import skipped: '{log.SourceFile}'.");
    }

    /// <summary>Executes (or resumes) an approved import. The log must be in Status=Approved.</summary>
    private async Task<ImportOutcome> ExecuteApprovedImportAsync(DigitalCenterDbContext db, MetadataImportLog log, bool useTmdb, CancellationToken ct)
    {
        var settings = _settings.Load();
        var isSeries = log.IsSeries;
        var sourcePath = log.SourcePath ?? string.Empty;
        var destinationFolder = log.ChosenFolder
                                ?? GetDefaultDestinationFolder(isSeries, settings)
                                ?? throw new InvalidOperationException("No destination folder is known for this import.");

        if (!Directory.Exists(destinationFolder))
        {
            const string message = "The destination folder no longer exists. Choose a destination again.";
            log.Status = ImportStatuses.Matched; // Back to the operator, the file was not touched.
            log.ErrorDetails = message;
            await db.SaveChangesAsync(ct);
            return new ImportOutcome(false, null, null, message);
        }

        if (isSeries && (!log.ParsedSeason.HasValue || !log.ParsedEpisode.HasValue))
        {
            const string message = "The import is marked as a series episode but has no season/episode number.";
            log.Status = ImportStatuses.Failed;
            log.ErrorDetails = message;
            await db.SaveChangesAsync(ct);
            return new ImportOutcome(false, null, null, message);
        }

        // ---------- 1. Metadata upsert (idempotent) ----------
        var (titleId, posterPath, metadata) = await UpsertTitleAsync(db, log, useTmdb, ct);

        // ---------- 2. Plan and record the destination ----------
        // When recovery resumes an import that already passed the MoveStarted
        // checkpoint, the recorded target is reused as-is (the interrupted
        // copy may have completed it). A fresh import resolves file name
        // collisions with a numeric suffix instead of ever overwriting.
        var resuming = log.Stage is ImportStages.MoveStarted or ImportStages.MovedToLibrary or ImportStages.RegisterStarted
                       && !string.IsNullOrEmpty(log.TargetPath);

        string targetPath;
        if (resuming)
        {
            targetPath = log.TargetPath!;
        }
        else
        {
            var cleanName = settings.CleanFilenames
                ? PathPlanner.BuildImportFileName(
                    settings.MovieFilenameTemplate, settings.SeriesFilenameTemplate,
                    isSeries, log.ParsedTitle ?? "Unknown", log.ParsedYear, log.ParsedSeason, log.ParsedEpisode, log.SourceFile)
                : log.SourceFile;

            var fileName = PathPlanner.ResolveFileNameConflict(destinationFolder, PathPlanner.SanitizeFileName(cleanName));
            targetPath = Path.Combine(destinationFolder, fileName);
        }

        if (!Path.GetFullPath(targetPath).StartsWith(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(destinationFolder)) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"The planned destination is outside the chosen folder: '{targetPath}'.");
        }

        log.TargetPath = targetPath;
        log.Stage = ImportStages.MoveStarted;
        await db.SaveChangesAsync(ct); // Checkpoint.

        // ---------- 3. Move the file (copy → verify → rename → delete source) ----------
        var moveResult = await MoveSourceIntoLibraryAsync(log, sourcePath, targetPath, ct);
        if (!moveResult.Success)
        {
            log.Status = ImportStatuses.Failed;
            log.ErrorDetails = moveResult.Message;
            await db.SaveChangesAsync(ct);
            _logger?.Error($"Import of '{log.SourceFile}' failed: {moveResult.Message}");
            return new ImportOutcome(false, titleId, targetPath, moveResult.Message!);
        }

        log.Note = moveResult.Note;
        log.Stage = ImportStages.MovedToLibrary;
        await db.SaveChangesAsync(ct); // Checkpoint: full copy exists in the library.

        // ---------- 4. Register the file (idempotent) ----------
        await RegisterFileAsync(db, log, targetPath, isSeries, titleId, ct);

        // ---------- 5. Artwork (best effort; a failure never fails the import) ----------
        await TryCacheArtworkAsync(db, log, titleId, posterPath, metadata, ct);

        log.Status = ImportStatuses.Imported;
        log.Stage = ImportStages.Imported;
        log.MediaTitleId = titleId;
        await db.SaveChangesAsync(ct);

        _logger?.Info($"Import complete: '{log.SourceFile}' → '{targetPath}' (title {titleId}).");
        return new ImportOutcome(true, titleId, targetPath, moveResult.Message ?? "Imported successfully.");
    }

    /// <summary>Creates or reuses title/season/episode rows and applies TMDb metadata when requested.</summary>
    private async Task<(int TitleId, string? PosterPath, TmdbTitleMetadata? Metadata)> UpsertTitleAsync(DigitalCenterDbContext db, MetadataImportLog log, bool useTmdb, CancellationToken ct)
    {
        var kind = log.IsSeries ? MediaKind.Series : MediaKind.Movie;

        // A previous partial run may have registered the TMDb id already.
        MediaTitle? title = null;
        if (log.TmdbId.HasValue)
        {
            title = await db.MediaTitles.FirstOrDefaultAsync(t => t.TmdbId == log.TmdbId.Value, ct);
        }

        title ??= await FindExistingTitleAsync(db, kind, log.ParsedTitle ?? string.Empty, log.ParsedYear, log.TmdbId, ct);

        TmdbTitleMetadata? metadata = null;
        string? posterPath = null;
        if (useTmdb && log.TmdbId.HasValue)
        {
            metadata = log.IsSeries
                ? await GetSeriesMetadataAsync(log.TmdbId.Value, log.ParsedSeason, ct)
                : await _tmdb.GetMovieAsync(log.TmdbId.Value, ct);

            // TMDb result wins over the parsed name when available.
            if (metadata is not null)
            {
                posterPath = metadata.PosterPath;
                if (!string.IsNullOrWhiteSpace(metadata.Title))
                {
                    log.ParsedTitle = metadata.Title;
                }

                if (metadata.Year.HasValue)
                {
                    log.ParsedYear = metadata.Year.Value;
                }
            }
        }

        if (title is null)
        {
            title = new MediaTitle
            {
                Type = kind,
                Title = log.ParsedTitle ?? "Unknown",
                ReleaseYear = log.ParsedYear
            };
            db.MediaTitles.Add(title);
            await db.SaveChangesAsync(ct); // Need the id.
        }

        // Stamp the TMDb id on titles found by name (unique index is safe:
        // the TMDb-id lookup above found no other holder).
        title.TmdbId ??= log.TmdbId;

        if (metadata is not null)
        {
            log.MetadataSource = "TMDb";
            MetadataMerge.Apply(db, title, metadata);
            await db.SaveChangesAsync(ct);
        }
        else if (log.MetadataSource is null or "WatchFolder")
        {
            // No online metadata was (or could be) applied: the catalog entry
            // comes from the matched/parsed file name.
            log.MetadataSource = "TMDb match (offline)";
        }

        log.MediaTitleId = title.Id;
        await db.SaveChangesAsync(ct);
        return (title.Id, posterPath, metadata);
    }

    /// <summary>
    /// Finds the catalog entry an import belongs to, so repeated scans never
    /// stack duplicate titles (and, for a series, duplicate season/episode
    /// trees). A TMDb id is the authoritative identity: when the import knows
    /// one and no title carries it, a same-name entry with a different id is a
    /// different work (remake, reboot) and is left alone.
    /// </summary>
    private static async Task<MediaTitle?> FindExistingTitleAsync(
        DigitalCenterDbContext db, MediaKind kind, string titleName, int? year, long? tmdbId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(titleName))
        {
            return null;
        }

        var candidates = await db.MediaTitles
            .Where(t => t.Type == kind && t.Title == titleName) // COLLATE NOCASE
            .ToListAsync(ct);

        if (candidates.Count == 0)
        {
            return null;
        }

        if (tmdbId.HasValue)
        {
            return candidates.FirstOrDefault(t => t.TmdbId == tmdbId.Value);
        }

        if (year.HasValue)
        {
            var byYear = candidates.FirstOrDefault(t => t.ReleaseYear == year.Value);
            if (byYear is not null)
            {
                return byYear;
            }
        }

        // Without an external identity, one unambiguous same-name entry is
        // reused (a differing or missing year is far more often a re-release or
        // rip year than another work). Several candidates stay ambiguous: a new
        // record is created rather than guessing.
        return candidates.Count == 1 ? candidates[0] : null;
    }

    private sealed record MoveOutcome(bool Success, string? Message, string? Note);

    /// <summary>
    /// Copies source → "{target}.dcenter-partial", verifies the size, renames
    /// to the final name and only then deletes the source. Every state is
    /// recoverable (see <see cref="RecoverInterruptedAsync"/>).
    /// </summary>
    private async Task<MoveOutcome> MoveSourceIntoLibraryAsync(MetadataImportLog log, string sourcePath, string targetPath, CancellationToken ct)
    {
        var targetDirectory = Path.GetDirectoryName(targetPath);
        if (string.IsNullOrEmpty(targetDirectory))
        {
            return new MoveOutcome(false, "The planned destination path has no directory part.", null);
        }

        if (File.Exists(targetPath))
        {
            // A previous run already completed the copy (crash between the
            // rename and the checkpoint). Treat the existing file as the
            // result; the operator is informed via the note.
            var existingSize = new FileInfo(targetPath).Length;
            if (log.FileSize.HasValue && existingSize != log.FileSize.Value)
            {
                return new MoveOutcome(false,
                    $"A different file already exists at the destination ({existingSize} bytes vs. expected {log.FileSize.Value}). " +
                    "The import was stopped to avoid overwriting data.", null);
            }

            TryDelete(sourcePath);
            return new MoveOutcome(true, null, "An identical file already existed at the destination and was kept.");
        }

        if (!File.Exists(sourcePath))
        {
            return new MoveOutcome(false,
                $"The source file is no longer present in the Watch Folder: '{sourcePath}'. The file was not imported.", null);
        }

        Directory.CreateDirectory(targetDirectory);
        var partialPath = targetPath + ".dcenter-partial";

        try
        {
            const int bufferSize = 1024 * 1024;
            await using (var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize, useAsync: true))
            await using (var destination = new FileStream(partialPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize, useAsync: true))
            {
                await source.CopyToAsync(destination, bufferSize, ct);
                await destination.FlushAsync(ct);
            }

            var copied = new FileInfo(partialPath).Length;
            var expected = new FileInfo(sourcePath).Length;
            if (copied != expected)
            {
                TryDelete(partialPath);
                return new MoveOutcome(false, $"The copied file size ({copied} bytes) differs from the source ({expected} bytes). The import was aborted.", null);
            }

            File.Move(partialPath, targetPath, overwrite: false);
            TryDelete(sourcePath);

            var note = File.Exists(sourcePath)
                ? "The copy succeeded but the original could not be removed from the Watch Folder; remove it manually."
                : null;
            return new MoveOutcome(true, null, note);
        }
        catch (OperationCanceledException)
        {
            TryDelete(partialPath);
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(partialPath);
            return new MoveOutcome(false, $"The file could not be moved into the library: {ex.Message}", null);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Source stays in place; the import note tells the operator.
        }
    }

    /// <summary>
    /// Startup recovery: completes or safely fails imports that were approved
    /// but interrupted. Deterministic: no network access during recovery.
    /// Returns a human-readable report for the operator.
    /// </summary>
    public async Task<IReadOnlyList<string>> RecoverInterruptedAsync(CancellationToken ct = default)
    {
        var report = new List<string>();
        await using var db = await _factory.CreateDbContextAsync(ct);
        var interrupted = await db.MetadataImportLogs
            .Where(l => l.Status == ImportStatuses.Approved)
            .OrderBy(l => l.Id)
            .ToListAsync(ct);

        if (interrupted.Count == 0)
        {
            return report;
        }

        _logger?.Warn($"Found {interrupted.Count} interrupted import(s); starting recovery.");

        foreach (var log in interrupted)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var outcome = await ExecuteApprovedImportAsync(db, log, useTmdb: false, ct);
                report.Add(outcome.Success
                    ? $"Recovered import '{log.SourceFile}' → {outcome.TargetPath}"
                    : $"Import '{log.SourceFile}' failed during recovery: {outcome.Message}");
            }
            catch (Exception ex)
            {
                log.Status = ImportStatuses.Failed;
                log.ErrorDetails = $"Recovery failed: {ex.Message}";
                await db.SaveChangesAsync(ct);
                report.Add($"Import '{log.SourceFile}' could not be recovered: {ex.Message}");
                _logger?.Error($"Recovery failed for import {log.Id} ('{log.SourceFile}').", ex);
            }
        }

        return report;
    }
}
