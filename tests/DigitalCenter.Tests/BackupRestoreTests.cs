using DigitalCenter.Core.Models;
using DigitalCenter.Core.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DigitalCenter.Tests;

/// <summary>Backup creation, validation and restore round-trip.</summary>
public class BackupRestoreTests : IDisposable
{
    private readonly TestEnvironment _env;

    public BackupRestoreTests()
    {
        _env = TestEnvironment.CreateAsync().GetAwaiter().GetResult();
    }

    [Fact]
    public async Task Backup_Then_Restore_Recovers_The_Catalog()
    {
        // Arrange: a small catalog.
        using (var db = _env.Factory.CreateDbContext())
        {
            var movie = new MediaTitle { Type = MediaKind.Movie, Title = "Backup Test Movie", ReleaseYear = 2001 };
            db.MediaTitles.Add(movie);
            db.SaveChanges();
            db.MediaFiles.Add(new MediaFile { MovieTitleId = movie.Id, FilePath = "/data/backup-test.mkv", FileSize = 12345 });
            db.SaveChanges();
        }

        var backupFile = await _env.BackupService.BackupAsync();
        Assert.True(File.Exists(backupFile));
        await _env.BackupService.ValidateBackupAsync(backupFile);

        // Act: damage the catalog, then restore.
        using (var db = _env.Factory.CreateDbContext())
        {
            await db.MediaTitles.ExecuteDeleteAsync();
        }

        using (var db = _env.Factory.CreateDbContext())
        {
            Assert.Empty(db.MediaTitles);
        }

        await _env.BackupService.RestoreAsync(backupFile);

        // Assert.
        using (var db = _env.Factory.CreateDbContext())
        {
            var restored = await db.MediaTitles.Include(t => t.Files).SingleAsync();
            Assert.Equal("Backup Test Movie", restored.Title);
            Assert.Single(restored.Files);
            Assert.Equal(12345, restored.Files.Single().FileSize);
        }

        Assert.Contains(_env.BackupService.ListBackups(), b => b.FilePath == backupFile);
    }

    [Fact]
    public async Task ValidateBackup_Rejects_Foreign_Database()
    {
        var bogus = Path.Combine(_env.Root, "not-a-backup.db");
        await File.WriteAllTextAsync(bogus, "this is not sqlite");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _env.BackupService.ValidateBackupAsync(bogus));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _env.BackupService.ValidateBackupAsync(Path.Combine(_env.Root, "missing.db")));
    }

    [Fact]
    public async Task Restore_Keeps_A_Safety_Copy()
    {
        var backupFile = await _env.BackupService.BackupAsync();
        await _env.BackupService.RestoreAsync(backupFile);

        var safetyCopies = Directory.GetFiles(_env.Paths.DataRoot, "*.pre-restore-*");
        Assert.NotEmpty(safetyCopies);
    }

    public void Dispose() => _env.Dispose();
}
