using DigitalCenter.Core.Configuration;
using DigitalCenter.Core.Data;
using DigitalCenter.Core.Logging;
using DigitalCenter.Core.Models;
using DigitalCenter.Core.Tmdb;
using Microsoft.EntityFrameworkCore;

namespace DigitalCenter.Core.Services;

/// <summary>
/// ImportService, part 3 of 5: destinations. Resolves the folder a matched file
/// belongs to (library root plus its language), previews the planned target and
/// files everything that is already matched.
/// </summary>
public sealed partial class ImportService
{
    // ------------------------------------------------------------ destination

    /// <summary>
    /// Default destination folder for a matched file: the movies or series root,
    /// plus the sub-folder of the detected language when the library is organized
    /// by language. A language folder that does not exist yet is created, so a
    /// new language simply appears in the library root the first time it is seen.
    /// Without a known language the root itself is the destination.
    /// </summary>
    public string? GetDefaultDestinationFolder(bool isSeries, AppSettings settings, string? languageCode = null)
    {
        var root = isSeries ? settings.SeriesRoot : settings.MoviesRoot;
        if (string.IsNullOrWhiteSpace(root))
        {
            return null;
        }

        if (!settings.OrganizeLibraryByLanguage)
        {
            return root;
        }

        var folder = LanguageFolder.Resolve(root, languageCode);
        if (!string.Equals(folder, root, StringComparison.Ordinal))
        {
            Directory.CreateDirectory(folder);
            _logger?.Info($"Library folder for language '{languageCode}' ready: '{folder}'.");
        }

        return folder;
    }

    /// <summary>Records the operator's destination choice (step 6 of the workflow).</summary>
    public async Task ChooseDestinationAsync(int logId, string destinationFolder, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(destinationFolder) || !Directory.Exists(destinationFolder))
        {
            throw new InvalidOperationException($"The destination folder does not exist: '{destinationFolder}'.");
        }

        await using var db = await _factory.CreateDbContextAsync(ct);
        var log = await db.MetadataImportLogs.FirstOrDefaultAsync(l => l.Id == logId, ct)
                  ?? throw new InvalidOperationException("The selected import no longer exists.");

        if (log.Status != ImportStatuses.Matched)
        {
            throw new InvalidOperationException($"Only matched imports can receive a destination (current status: {log.Status}).");
        }

        log.ChosenFolder = Path.GetFullPath(destinationFolder);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Builds the pre-move preview: source, matched title, planned target and collision state.</summary>
    public async Task<ImportPreview> PreviewImportAsync(MetadataImportLog log, string? destinationFolder = null, CancellationToken ct = default)
    {
        var settings = _settings.Load();
        var folder = destinationFolder ?? log.ChosenFolder ?? GetDefaultDestinationFolder(log.IsSeries, settings)
                     ?? throw new InvalidOperationException("Choose a destination folder first.");

        var cleanName = settings.CleanFilenames
            ? PathPlanner.BuildImportFileName(
                settings.MovieFilenameTemplate, settings.SeriesFilenameTemplate,
                log.IsSeries, log.ParsedTitle ?? "Unknown", log.ParsedYear, log.ParsedSeason, log.ParsedEpisode, log.SourceFile)
            : log.SourceFile;

        var targetPath = PathPlanner.PlanImportTargetPath(folder, cleanName);
        var targetExists = File.Exists(targetPath);
        string? note = null;
        if (targetExists)
        {
            note = settings.UsbConflictAction.Equals("Overwrite", StringComparison.OrdinalIgnoreCase)
                ? "A file with this name exists; it will be replaced after your confirmation."
                : "A file with this name exists; the import will be saved under a numbered name.";
        }

        return new ImportPreview(
            log.Id,
            log.SourceFile,
            log.SourcePath ?? string.Empty,
            log.FileSize,
            log.IsSeries,
            log.ParsedTitle ?? "Unknown",
            log.ParsedYear,
            log.TmdbId,
            log.Confidence,
            folder,
            targetPath,
            targetExists,
            note);
    }

    /// <summary>
    /// Moves every matched Watch Folder file into the library without waiting for
    /// the operator — the second half of the self-organizing Watch Folder: files
    /// are detected, their metadata is fetched, and each one is filed under the
    /// language it was detected in (the folder is created when it is missing).
    /// Unmatched rows are left exactly as they are, and one failing file never
    /// stops the rest of the pass.
    /// </summary>
    public async Task<AutoOrganizeResult> AutoOrganizeAsync(CancellationToken ct = default)
    {
        var settings = _settings.Load();
        if (!settings.AutoImportMatchedFiles)
        {
            return new AutoOrganizeResult(0, 0, 0);
        }

        List<int> matched;
        await using (var db = await _factory.CreateDbContextAsync(ct))
        {
            matched = await db.MetadataImportLogs
                .Where(l => l.Status == ImportStatuses.Matched)
                .OrderBy(l => l.Id)
                .Select(l => l.Id)
                .ToListAsync(ct);
        }

        if (matched.Count == 0)
        {
            return new AutoOrganizeResult(0, 0, 0);
        }

        ActivityReported?.Invoke($"Moving file to Library: {matched.Count} matched file(s) …");

        var moved = 0;
        var failed = 0;
        foreach (var logId in matched)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var outcome = await ConfirmImportAsync(logId, null, ct);
                if (outcome.Success)
                {
                    moved++;
                }
                else
                {
                    failed++;
                }

                ActivityReported?.Invoke(outcome.Success
                    ? $"Moved into the library: {outcome.Message}"
                    : $"Automatic move failed: {outcome.Message}");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                _logger?.Error($"Automatic import of log {logId} failed.", ex);
                ActivityReported?.Invoke($"Automatic move failed: {ex.Message}");
            }
        }

        ActivityReported?.Invoke(
            $"Automatic organization finished: {moved} file(s) moved into the library, {failed} failed.");

        return new AutoOrganizeResult(moved, failed, matched.Count - moved - failed);
    }
}
