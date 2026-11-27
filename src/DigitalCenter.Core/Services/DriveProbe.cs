using System.IO;

namespace DigitalCenter.Core.Services;

/// <summary>One candidate copy destination (removable USB drive, SD card, or Android/MTP phone).</summary>
public sealed record UsbDriveOption(string Root, string Name, string DriveTypeDisplay, long FreeSpace, long TotalSpace);

/// <summary>
/// Platform-independent abstraction for Windows Portable Devices (MTP/Android phones)
/// that do not receive a Windows drive letter. Implemented in DigitalCenter.App on Windows.
/// </summary>
public interface IPortableDeviceTransfer
{
    bool IsPortablePath(string path);

    IReadOnlyList<UsbDriveOption> GetPortableDrives();

    long GetFreeSpace(string rootPath);

    bool IsReady(string rootPath);

    string CombineSubfolder(string rootPath, string? subfolder);

    void EnsureDirectory(string destinationDir);

    bool FileExists(string destinationDir, string fileName);

    void DeleteFile(string destinationDir, string fileName);

    Task UploadFileAsync(
        string sourcePath,
        string destinationDir,
        string targetFileName,
        Action<long>? onBytesCopied,
        CancellationToken ct);
}

/// <summary>
/// Drive abstraction so the copy workflow can be tested without real USB
/// hardware. The default implementation prefers removable drives and connected
/// portable/MTP devices, and can optionally include fixed drives.
/// </summary>
public interface IDriveProbe
{
    /// <summary>Candidate drives; removable USB/SD and Android/MTP first, optionally including fixed drives.</summary>
    IReadOnlyList<UsbDriveOption> GetCandidateDrives(bool includeFixedDrives = false);

    long GetFreeSpace(string rootPath);

    bool IsReady(string rootPath);
}

public sealed class DriveProbe : IDriveProbe
{
    private readonly IPortableDeviceTransfer? _portable;

    public DriveProbe(IPortableDeviceTransfer? portable = null)
    {
        _portable = portable;
    }

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

        if (_portable is not null)
        {
            try
            {
                options.AddRange(_portable.GetPortableDrives());
            }
            catch (Exception)
            {
                // Portable device enumeration must never break standard USB drive detection.
            }
        }

        return options
            .OrderByDescending(d => d.DriveTypeDisplay == "Removable" || d.DriveTypeDisplay == "Android / MTP")
            .ThenBy(d => d.DriveTypeDisplay == "Android / MTP" ? 1 : 0)
            .ThenBy(d => d.Root)
            .ToList();
    }

    public long GetFreeSpace(string rootPath)
    {
        if (_portable is not null && _portable.IsPortablePath(rootPath))
        {
            return _portable.GetFreeSpace(rootPath);
        }

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
        if (_portable is not null && _portable.IsPortablePath(rootPath))
        {
            return _portable.IsReady(rootPath);
        }

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
