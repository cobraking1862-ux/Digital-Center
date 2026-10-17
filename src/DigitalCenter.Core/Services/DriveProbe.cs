using System.IO;

namespace DigitalCenter.Core.Services;

/// <summary>One candidate copy destination (typically a removable USB drive).</summary>
public sealed record UsbDriveOption(string Root, string Name, string DriveTypeDisplay, long FreeSpace, long TotalSpace);

/// <summary>
/// Drive abstraction so the copy workflow can be tested without real USB
/// hardware. The default implementation prefers removable drives but can
/// list all ready drives (useful for development machines).
/// </summary>
public interface IDriveProbe
{
    /// <summary>Candidate drives; removable first, optionally including fixed drives.</summary>
    IReadOnlyList<UsbDriveOption> GetCandidateDrives(bool includeFixedDrives = false);

    long GetFreeSpace(string rootPath);

    bool IsReady(string rootPath);
}

public sealed class DriveProbe : IDriveProbe
{
    public IReadOnlyList<UsbDriveOption> GetCandidateDrives(bool includeFixedDrives = false)
    {
        var options = new List<UsbDriveOption>();
        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (!drive.IsReady || drive.DriveType is DriveType.CDRom or DriveType.NoRootDirectory or DriveType.Unknown)
                {
                    continue;
                }

                if (drive.DriveType != DriveType.Removable && !includeFixedDrives)
                {
                    continue;
                }

                long free = 0, total = 0;
                try
                {
                    free = drive.AvailableFreeSpace;
                    total = drive.TotalSize;
                }
                catch (IOException)
                {
                    // Drive information vanished between IsReady and the size
                    // queries; still list the drive with unknown sizes.
                }

                options.Add(new UsbDriveOption(
                    drive.RootDirectory.FullName,
                    drive.VolumeLabel,
                    drive.DriveType.ToString(),
                    free,
                    total));
            }
        }
        catch (IOException)
        {
            // Enumerating drives failed entirely; report none instead of crashing.
        }

        return options.OrderByDescending(d => d.DriveTypeDisplay == "Removable").ThenBy(d => d.Root).ToList();
    }

    public long GetFreeSpace(string rootPath)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(rootPath)) ?? rootPath;
            var drive = DriveInfo.GetDrives()
                .FirstOrDefault(d => string.Equals(d.RootDirectory.FullName, root, StringComparison.OrdinalIgnoreCase));
            return drive is { IsReady: true } ? drive.AvailableFreeSpace : 0;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    public bool IsReady(string rootPath)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(rootPath));
            if (string.IsNullOrEmpty(root))
            {
                return false;
            }

            var drive = DriveInfo.GetDrives()
                .FirstOrDefault(d => string.Equals(d.RootDirectory.FullName, root, StringComparison.OrdinalIgnoreCase));
            return drive is { IsReady: true };
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
