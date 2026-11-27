using System.IO;
using DigitalCenter.Core.Logging;
using DigitalCenter.Core.Services;
using MediaDevices;

namespace DigitalCenter.App.Services;

/// <summary>
/// Windows Portable Devices (WPD / MTP) transfer provider so Android phones
/// connected via USB cable in File Transfer (MTP) mode appear in USB Cart
/// alongside standard drive-letter USB flash drives and SD cards.
/// </summary>
public sealed class WindowsMtpTransfer : IPortableDeviceTransfer
{
    private const string Prefix = "mtp://";
    private readonly IAppLogger? _logger;

    public WindowsMtpTransfer(IAppLogger? logger = null)
    {
        _logger = logger;
    }

    public bool IsPortablePath(string path) =>
        !string.IsNullOrWhiteSpace(path) &&
        path.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<UsbDriveOption> GetPortableDrives()
    {
        var results = new List<UsbDriveOption>();
        IEnumerable<MediaDevice> devices;
        try
        {
            devices = MediaDeviceManager.Instance.GetDevices() ?? [];
        }
        catch (Exception ex)
        {
            _logger?.Warn($"Portable device enumeration failed: {ex.Message}");
            return results;
        }

        foreach (var device in devices)
        {
            // Skip standard Windows drive-letter storage volumes (already enumerated by DriveInfo).
            if (IsDriveLetterVolume(device))
            {
                continue;
            }

            try
            {
                device.Connect();
                var deviceLabel = !string.IsNullOrWhiteSpace(device.FriendlyName)
                    ? device.FriendlyName.Trim()
                    : (!string.IsNullOrWhiteSpace(device.Description) ? device.Description.Trim() : "Android Device");

                var drives = device.GetDrives().ToList();
                foreach (var drive in drives)
                {
                    var storagePath = drive.RootDirectory?.FullName;
                    if (string.IsNullOrWhiteSpace(storagePath))
                    {
                        storagePath = "\\" + (drive.Name ?? "Internal shared storage").Trim('\\');
                    }

                    // Skip anything that looks like a Windows drive letter (e.g. "E:\").
                    if (storagePath.Length >= 2 && storagePath[1] == ':')
                    {
                        continue;
                    }

                    var storageName = !string.IsNullOrWhiteSpace(drive.Name)
                        ? drive.Name.Trim('\\')
                        : storagePath.Trim('\\');

                    var free = Math.Max(0L, drive.AvailableFreeSpace);
                    var total = Math.Max(0L, drive.TotalSize);
                    var root = BuildPortableUri(device.DeviceId, storagePath);

                    results.Add(new UsbDriveOption(
                        Root: root,
                        Name: $"{deviceLabel} — {storageName}",
                        DriveTypeDisplay: "Android / MTP",
                        FreeSpace: free,
                        TotalSpace: total));
                }
            }
            catch (Exception ex)
            {
                _logger?.Warn($"Skipping portable device '{device.Description}': {ex.Message}");
            }
            finally
            {
                try
                {
                    if (device.IsConnected)
                    {
                        device.Disconnect();
                    }
                }
                catch (Exception)
                {
                }
            }
        }

        return results;
    }

    public long GetFreeSpace(string rootPath)
    {
        if (!TryParsePortableUri(rootPath, out var deviceId, out var remotePath))
        {
            return 0;
        }

        return WithConnectedDevice(deviceId, device =>
        {
            var storageSegment = GetRootStorageSegment(remotePath);
            var drive = device.GetDrives().FirstOrDefault(d =>
                string.Equals((d.RootDirectory?.FullName ?? string.Empty).Trim('\\'), storageSegment, StringComparison.OrdinalIgnoreCase) ||
                string.Equals((d.Name ?? string.Empty).Trim('\\'), storageSegment, StringComparison.OrdinalIgnoreCase));
            return drive is not null ? Math.Max(0L, drive.AvailableFreeSpace) : 0L;
        }, fallback: 0L);
    }

    public bool IsReady(string rootPath)
    {
        if (!TryParsePortableUri(rootPath, out var deviceId, out _))
        {
            return false;
        }

        return WithConnectedDevice(deviceId, device => device.GetDrives().Any(), fallback: false);
    }

    public string CombineSubfolder(string rootPath, string? subfolder)
    {
        if (string.IsNullOrWhiteSpace(subfolder))
        {
            return rootPath;
        }

        return $"{rootPath.TrimEnd('\\')}\\{subfolder.Trim('\\')}";
    }

    public void EnsureDirectory(string destinationDir)
    {
        if (!TryParsePortableUri(destinationDir, out var deviceId, out var remotePath))
        {
            throw new InvalidOperationException($"Invalid portable device destination: '{destinationDir}'.");
        }

        WithConnectedDevice(deviceId, device =>
        {
            EnsureRemoteDirectory(device, remotePath);
            return true;
        });
    }

    public bool FileExists(string destinationDir, string fileName)
    {
        if (!TryParsePortableUri(destinationDir, out var deviceId, out var remoteDir))
        {
            return false;
        }

        return WithConnectedDevice(deviceId, device =>
        {
            if (!device.DirectoryExists(remoteDir))
            {
                return false;
            }

            var remoteFile = CombineRemotePath(remoteDir, fileName);
            return device.FileExists(remoteFile);
        }, fallback: false);
    }

    public void DeleteFile(string destinationDir, string fileName)
    {
        if (!TryParsePortableUri(destinationDir, out var deviceId, out var remoteDir))
        {
            return;
        }

        WithConnectedDevice(deviceId, device =>
        {
            var remoteFile = CombineRemotePath(remoteDir, fileName);
            if (device.FileExists(remoteFile))
            {
                device.DeleteFile(remoteFile);
            }

            return true;
        });
    }

    public Task UploadFileAsync(
        string sourcePath,
        string destinationDir,
        string targetFileName,
        Action<long>? onBytesCopied,
        CancellationToken ct)
    {
        if (!TryParsePortableUri(destinationDir, out var deviceId, out var remoteDir))
        {
            throw new InvalidOperationException($"Invalid portable device destination: '{destinationDir}'.");
        }

        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            WithConnectedDevice(deviceId, device =>
            {
                EnsureRemoteDirectory(device, remoteDir);
                var remoteFile = CombineRemotePath(remoteDir, targetFileName);

                using var fileStream = new FileStream(
                    sourcePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize: 1024 * 1024,
                    FileOptions.SequentialScan);
                using var progressStream = new ProgressReadStream(fileStream, onBytesCopied, ct);

                device.UploadFile(progressStream, remoteFile);
                return true;
            });
        }, ct);
    }

    private static void EnsureRemoteDirectory(MediaDevice device, string remotePath)
    {
        var normalized = NormalizeRemotePath(remotePath);
        if (device.DirectoryExists(normalized))
        {
            return;
        }

        var parts = normalized.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length <= 1)
        {
            return;
        }

        var current = "\\" + parts[0];
        for (var i = 1; i < parts.Length; i++)
        {
            current = current + "\\" + parts[i];
            if (!device.DirectoryExists(current))
            {
                device.CreateDirectory(current);
            }
        }
    }

    private static T WithConnectedDevice<T>(string deviceId, Func<MediaDevice, T> action, T fallback)
    {
        try
        {
            return WithConnectedDevice(deviceId, action);
        }
        catch (Exception)
        {
            return fallback;
        }
    }

    private static T WithConnectedDevice<T>(string deviceId, Func<MediaDevice, T> action)
    {
        var device = (MediaDeviceManager.Instance.GetDevices() ?? [])
            .FirstOrDefault(d => string.Equals(d.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase))
            ?? throw new IOException("The Android / MTP device is no longer connected.");

        try
        {
            device.Connect();
            return action(device);
        }
        finally
        {
            try
            {
                if (device.IsConnected)
                {
                    device.Disconnect();
                }
            }
            catch (Exception)
            {
            }
        }
    }

    private static bool IsDriveLetterVolume(MediaDevice device)
    {
        var id = device.DeviceId ?? string.Empty;
        return id.Contains("wpdbusenum", StringComparison.OrdinalIgnoreCase) ||
               id.Contains("storage#volume", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildPortableUri(string deviceId, string storagePath) =>
        $"{Prefix}{deviceId}|{NormalizeRemotePath(storagePath)}";

    private static bool TryParsePortableUri(string uri, out string deviceId, out string remotePath)
    {
        deviceId = string.Empty;
        remotePath = string.Empty;
        if (!uri.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var body = uri[Prefix.Length..];
        var sep = body.IndexOf('|');
        if (sep <= 0 || sep >= body.Length - 1)
        {
            return false;
        }

        deviceId = body[..sep];
        remotePath = NormalizeRemotePath(body[(sep + 1)..]);
        return true;
    }

    private static string NormalizeRemotePath(string path) =>
        "\\" + path.Trim().Replace('/', '\\').Trim('\\');

    private static string CombineRemotePath(string remoteDir, string fileName) =>
        NormalizeRemotePath(remoteDir).TrimEnd('\\') + "\\" + fileName.Trim('\\');

    private static string GetRootStorageSegment(string remotePath)
    {
        var parts = NormalizeRemotePath(remotePath).Split('\\', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 0 ? parts[0] : string.Empty;
    }

    private sealed class ProgressReadStream : Stream
    {
        private readonly Stream _inner;
        private readonly Action<long>? _onBytesRead;
        private readonly CancellationToken _ct;
        private long _totalRead;

        public ProgressReadStream(Stream inner, Action<long>? onBytesRead, CancellationToken ct)
        {
            _inner = inner;
            _onBytesRead = onBytesRead;
            _ct = ct;
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;

        public override long Position
        {
            get => _inner.Position;
            set => _inner.Position = value;
        }

        public override void Flush() => _inner.Flush();

        public override int Read(byte[] buffer, int offset, int count)
        {
            _ct.ThrowIfCancellationRequested();
            var read = _inner.Read(buffer, offset, count);
            if (read > 0)
            {
                _totalRead += read;
                _onBytesRead?.Invoke(_totalRead);
            }

            return read;
        }

        public override int Read(Span<byte> buffer)
        {
            _ct.ThrowIfCancellationRequested();
            var read = _inner.Read(buffer);
            if (read > 0)
            {
                _totalRead += read;
                _onBytesRead?.Invoke(_totalRead);
            }

            return read;
        }

        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
