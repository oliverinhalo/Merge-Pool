using System.Globalization;
using System.Runtime.Versioning;
using Fsp;
using MergePool.Core.FileSystem;

namespace MergePool.Fs.WinFsp;

public sealed record MountRequest
{
    /// <summary>A drive letter (<c>P:</c>) or a directory to mount the pool on.</summary>
    public required string MountPoint { get; init; }

    public string VolumeLabel { get; init; } = "MergePool";

    /// <summary>Shown in Explorer as the network-style prefix. Empty mounts as a local disk.</summary>
    public string Prefix { get; init; } = string.Empty;

    public uint DebugLogFlags { get; init; }
}

/// <summary>
/// Owns the WinFsp host for one pool. Mount and unmount are idempotent so a service restart or a
/// fast remount during an upgrade cannot leave a half-mounted volume behind.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PoolMounter : IDisposable
{
    private readonly object _gate = new();
    private FileSystemHost? _host;
    private bool _disposed;

    public string? MountPoint { get; private set; }

    public bool IsMounted => _host is not null;

    /// <summary>Mounts the pool. Throws <see cref="IOException"/> with the WinFsp status on failure.</summary>
    public void Mount(PoolFileSystemEngine engine, MountRequest request)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(request);

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_host is not null)
            {
                throw new InvalidOperationException($"Already mounted at '{MountPoint}'.");
            }

            var fileSystem = new PoolWinFspFileSystem(engine, request.VolumeLabel);
            var host = new FileSystemHost(fileSystem);

            if (!string.IsNullOrEmpty(request.Prefix))
            {
                host.Prefix = request.Prefix;
            }

            var status = host.Mount(request.MountPoint, null, false, request.DebugLogFlags);
            if (status != NtStatus.Success)
            {
                host.Dispose();
                throw new IOException(string.Create(
                    CultureInfo.InvariantCulture,
                    $"WinFsp could not mount the pool at '{request.MountPoint}' (NTSTATUS 0x{status:X8})."));
            }

            _host = host;
            MountPoint = request.MountPoint;
        }
    }

    /// <summary>Unmounts if mounted. Safe to call repeatedly; used by the upgrade drain step.</summary>
    public void Unmount()
    {
        lock (_gate)
        {
            if (_host is null)
            {
                return;
            }

            try
            {
                _host.Unmount();
            }
            finally
            {
                _host.Dispose();
                _host = null;
                MountPoint = null;
            }
        }
    }

    /// <summary>Unmount and mount again in one step, as a version upgrade does.</summary>
    public void Remount(PoolFileSystemEngine engine, MountRequest request)
    {
        Unmount();
        Mount(engine, request);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        Unmount();
    }
}

/// <summary>Checks whether WinFsp is present, so the UI and installer can say so plainly.</summary>
[SupportedOSPlatform("windows")]
public static class WinFspProbe
{
    /// <summary>True when the WinFsp runtime can be loaded.</summary>
    public static bool IsInstalled(out string? version)
    {
        version = null;
        try
        {
            var assembly = typeof(FileSystemHost).Assembly;
            version = assembly.GetName().Version?.ToString();
            return true;
        }
        catch (Exception exception) when (exception is FileNotFoundException or BadImageFormatException or TypeLoadException)
        {
            return false;
        }
    }
}
