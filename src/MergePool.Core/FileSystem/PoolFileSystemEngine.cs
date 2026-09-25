using System.Diagnostics;
using MergePool.Core.Model;
using MergePool.Core.Paths;
using MergePool.Core.Placement;
using MergePool.Core.Union;

namespace MergePool.Core.FileSystem;

/// <summary>
/// The pool's file system semantics, independent of WinFsp. Everything a mount does goes through
/// here, so the behaviour is unit tested on any OS and the WinFsp adapter stays a thin translation
/// of types and status codes.
/// </summary>
public sealed class PoolFileSystemEngine(
    IPoolTopology topology,
    IPlacementStrategy placement,
    FileRelocator? relocator = null,
    PoolFileSystemOptions? options = null,
    IIoObserver? ioObserver = null)
{
    private readonly IPoolTopology _topology = topology ?? throw new ArgumentNullException(nameof(topology));
    private readonly PoolOperations _operations = new(topology, placement);
    private readonly IPlacementStrategy _placement = placement ?? throw new ArgumentNullException(nameof(placement));
    private readonly FileRelocator _relocator = relocator ?? new FileRelocator();
    private readonly PoolFileSystemOptions _options = options ?? new PoolFileSystemOptions();
    private readonly IIoObserver? _ioObserver = ioObserver;

    public UnionView View => _operations.View;

    public PoolOperations Operations => _operations;

    /// <summary>Raised whenever a file is moved between drives, for status reporting.</summary>
    public event EventHandler<RelocationResult>? FileRelocated;

    public PoolVolumeInfo GetVolumeInfo()
    {
        var snapshot = _topology.Current;

        // Explorer is told the pool's own size, not the drives' size. The difference is whatever is
        // on those drives outside the pool, which the pool cannot offer and never could. Until the
        // parts have been measured there is nothing better than the drive totals to report.
        return new PoolVolumeInfo
        {
            TotalBytes = snapshot.IsUsageMeasured ? snapshot.PoolCapacityBytes : snapshot.TotalBytes,
            FreeBytes = snapshot.FreeBytes,
            Label = string.IsNullOrEmpty(snapshot.Name) ? "MergePool" : snapshot.Name,
        };
    }

    public PoolFsStatus GetFileInfo(string poolPath, out PoolFileInfo? info)
    {
        info = null;
        var normalized = PoolPath.Normalize(poolPath);

        var location = View.Find(normalized);
        if (location is null)
        {
            return _topology.Current.OnlineParts.Any()
                ? PoolFsStatus.ObjectNameNotFound
                : PoolFsStatus.DeviceNotReady;
        }

        info = Describe(location);
        return PoolFsStatus.Success;
    }

    /// <summary>Opens or creates a pool entry.</summary>
    public PoolFsStatus Open(PoolOpenRequest request, out PoolFileHandle? handle, out PoolFileInfo? info)
    {
        ArgumentNullException.ThrowIfNull(request);
        handle = null;
        info = null;

        var normalized = PoolPath.Normalize(request.PoolPath);

        if (!_topology.Current.OnlineParts.Any())
        {
            return PoolFsStatus.DeviceNotReady;
        }

        try
        {
            return request.IsDirectory
                ? OpenDirectory(normalized, request, out handle, out info)
                : OpenFile(normalized, request, out handle, out info);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return PoolFsStatusExtensions.FromException(exception);
        }
    }

    private PoolFsStatus OpenDirectory(
        string poolPath,
        PoolOpenRequest request,
        out PoolFileHandle? handle,
        out PoolFileInfo? info)
    {
        handle = null;
        info = null;

        var existing = View.FindDirectory(poolPath);
        var creating = request.Disposition is PoolCreateDisposition.Create
            or PoolCreateDisposition.OpenOrCreate
            or PoolCreateDisposition.OverwriteOrCreate;

        if (existing is null && View.FindFile(poolPath) is not null)
        {
            return PoolFsStatus.NotADirectory;
        }

        if (existing is not null)
        {
            if (request.Disposition == PoolCreateDisposition.Create)
            {
                return PoolFsStatus.ObjectNameCollision;
            }
        }
        else
        {
            if (!creating)
            {
                return PoolFsStatus.ObjectNameNotFound;
            }

            if (!PoolPath.IsRoot(poolPath) && !ParentExists(poolPath))
            {
                return PoolFsStatus.ObjectPathNotFound;
            }

            _operations.CreateDirectory(poolPath);
            existing = View.FindDirectory(poolPath);
            if (existing is null)
            {
                return PoolFsStatus.IoError;
            }
        }

        handle = new PoolFileHandle(poolPath, isDirectory: true, existing.Part.PartId, existing.HostPath, stream: null)
        {
            Access = request.Access,
            DeleteOnClose = request.DeleteOnClose,
        };

        info = Describe(existing);
        return PoolFsStatus.Success;
    }

    private PoolFsStatus OpenFile(
        string poolPath,
        PoolOpenRequest request,
        out PoolFileHandle? handle,
        out PoolFileInfo? info)
    {
        handle = null;
        info = null;

        if (PoolPath.IsRoot(poolPath))
        {
            return PoolFsStatus.FileIsADirectory;
        }

        if (View.FindDirectory(poolPath) is not null)
        {
            return PoolFsStatus.FileIsADirectory;
        }

        var existing = View.FindFile(poolPath);

        if (existing is null)
        {
            if (request.Disposition is PoolCreateDisposition.Open or PoolCreateDisposition.Overwrite)
            {
                return PoolFsStatus.ObjectNameNotFound;
            }

            if (!ParentExists(poolPath))
            {
                return PoolFsStatus.ObjectPathNotFound;
            }

            return CreateFile(poolPath, request, out handle, out info);
        }

        if (request.Disposition == PoolCreateDisposition.Create)
        {
            return PoolFsStatus.ObjectNameCollision;
        }

        var truncate = request.Disposition is PoolCreateDisposition.Overwrite
            or PoolCreateDisposition.OverwriteOrCreate;

        var stream = OpenStream(existing.HostPath, truncate ? FileMode.Truncate : FileMode.Open, request);

        handle = new PoolFileHandle(poolPath, isDirectory: false, existing.Part.PartId, existing.HostPath, stream)
        {
            Access = request.Access,
            DeleteOnClose = request.DeleteOnClose,
        };

        info = Describe(existing);
        return PoolFsStatus.Success;
    }

    private PoolFsStatus CreateFile(
        string poolPath,
        PoolOpenRequest request,
        out PoolFileHandle? handle,
        out PoolFileInfo? info)
    {
        handle = null;
        info = null;

        var location = _operations.PrepareNewFile(poolPath, request.AllocationSize);
        var stream = OpenStream(location.HostPath, FileMode.CreateNew, request);

        try
        {
            if (request.AllocationSize > 0 && request.Access.HasFlag(PoolAccess.Write))
            {
                // Reserving up front keeps the file whole on this drive and fails early if it
                // cannot fit after all.
                stream.SetLength(request.AllocationSize);
                stream.SetLength(0);
            }

            if (request.Attributes != FileAttributes.Normal && request.Attributes != 0)
            {
                File.SetAttributes(location.HostPath, request.Attributes & ~FileAttributes.Directory);
            }
        }
        catch
        {
            stream.Dispose();
            TryDeleteFile(location.HostPath);
            throw;
        }

        handle = new PoolFileHandle(poolPath, isDirectory: false, location.Part.PartId, location.HostPath, stream)
        {
            Access = request.Access,
            DeleteOnClose = request.DeleteOnClose,
        };

        info = Describe(location);
        return PoolFsStatus.Success;
    }

    public PoolFsStatus Read(PoolFileHandle handle, Span<byte> buffer, long offset, out int transferred)
    {
        ArgumentNullException.ThrowIfNull(handle);
        transferred = 0;

        if (handle.IsDirectory)
        {
            return PoolFsStatus.FileIsADirectory;
        }

        try
        {
            var stream = handle.RequireStream();
            if (offset >= stream.Length)
            {
                return PoolFsStatus.EndOfFile;
            }

            stream.Position = offset;

            var started = Stopwatch.GetTimestamp();
            transferred = stream.Read(buffer);
            Observe(handle.PartId, IoKind.Read, transferred, started);

            return transferred == 0 ? PoolFsStatus.EndOfFile : PoolFsStatus.Success;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return PoolFsStatusExtensions.FromException(exception);
        }
    }

    /// <summary>
    /// Writes into the file, moving it whole to another drive first if this write would outgrow the
    /// one it is on.
    /// </summary>
    public PoolFsStatus Write(
        PoolFileHandle handle,
        ReadOnlySpan<byte> buffer,
        long offset,
        bool writeToEndOfFile,
        bool constrainedIo,
        out int transferred,
        out PoolFileInfo? info)
    {
        ArgumentNullException.ThrowIfNull(handle);
        transferred = 0;
        info = null;

        if (handle.IsDirectory)
        {
            return PoolFsStatus.FileIsADirectory;
        }

        try
        {
            var stream = handle.RequireStream();
            var start = writeToEndOfFile ? stream.Length : offset;

            if (constrainedIo)
            {
                // Constrained IO must never extend the file (cached writes past EOF).
                if (start >= stream.Length)
                {
                    info = DescribeHandle(handle);
                    return PoolFsStatus.Success;
                }

                var room = (int)Math.Min(buffer.Length, stream.Length - start);
                buffer = buffer[..room];
            }

            var end = start + buffer.Length;
            var status = EnsureRoomFor(handle, end);
            if (status != PoolFsStatus.Success)
            {
                return status;
            }

            stream = handle.RequireStream();
            stream.Position = start;

            var started = Stopwatch.GetTimestamp();
            stream.Write(buffer);
            Observe(handle.PartId, IoKind.Write, buffer.Length, started);

            transferred = buffer.Length;

            info = DescribeHandle(handle);
            return PoolFsStatus.Success;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return PoolFsStatusExtensions.FromException(exception);
        }
    }

    public PoolFsStatus SetFileSize(PoolFileHandle handle, long newSize, bool setAllocationSize, out PoolFileInfo? info)
    {
        ArgumentNullException.ThrowIfNull(handle);
        info = null;

        if (handle.IsDirectory)
        {
            return PoolFsStatus.FileIsADirectory;
        }

        try
        {
            var stream = handle.RequireStream();

            if (setAllocationSize && newSize < stream.Length)
            {
                // Shrinking the allocation below the data length is a no-op for us.
                info = DescribeHandle(handle);
                return PoolFsStatus.Success;
            }

            var status = EnsureRoomFor(handle, newSize);
            if (status != PoolFsStatus.Success)
            {
                return status;
            }

            stream = handle.RequireStream();
            if (!setAllocationSize || newSize > stream.Length)
            {
                stream.SetLength(newSize);
            }

            info = DescribeHandle(handle);
            return PoolFsStatus.Success;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return PoolFsStatusExtensions.FromException(exception);
        }
    }

    public PoolFsStatus Flush(PoolFileHandle? handle, out PoolFileInfo? info)
    {
        info = null;
        if (handle is null)
        {
            // A null handle means "flush the whole volume"; our writes go straight to the drives.
            return PoolFsStatus.Success;
        }

        try
        {
            handle.Stream?.Flush(flushToDisk: true);
            info = DescribeHandle(handle);
            return PoolFsStatus.Success;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return PoolFsStatusExtensions.FromException(exception);
        }
    }

    public PoolFsStatus SetBasicInfo(
        PoolFileHandle handle,
        FileAttributes? attributes,
        DateTimeOffset? creationTimeUtc,
        DateTimeOffset? lastAccessTimeUtc,
        DateTimeOffset? lastWriteTimeUtc,
        out PoolFileInfo? info)
    {
        ArgumentNullException.ThrowIfNull(handle);
        info = null;

        try
        {
            var path = handle.HostPath;

            if (creationTimeUtc is { } created)
            {
                File.SetCreationTimeUtc(path, created.UtcDateTime);
            }

            if (lastAccessTimeUtc is { } accessed)
            {
                File.SetLastAccessTimeUtc(path, accessed.UtcDateTime);
            }

            if (lastWriteTimeUtc is { } written)
            {
                File.SetLastWriteTimeUtc(path, written.UtcDateTime);
            }

            if (attributes is { } value && value != 0)
            {
                var keepDirectory = handle.IsDirectory ? FileAttributes.Directory : 0;
                File.SetAttributes(path, (value & ~FileAttributes.Directory) | keepDirectory);
            }

            info = DescribeHandle(handle);
            return PoolFsStatus.Success;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return PoolFsStatusExtensions.FromException(exception);
        }
    }

    /// <summary>Renames within the pool. The file stays on the drive it is already on.</summary>
    public PoolFsStatus Rename(PoolFileHandle handle, string newPoolPath, bool replaceIfExists)
    {
        ArgumentNullException.ThrowIfNull(handle);

        var destination = PoolPath.Normalize(newPoolPath);
        if (PoolPath.Comparer.Equals(handle.PoolPath, destination))
        {
            return PoolFsStatus.Success;
        }

        try
        {
            if (!ParentExists(destination))
            {
                return PoolFsStatus.ObjectPathNotFound;
            }

            // The stream has to let go of the name before the host rename can happen.
            var hadStream = handle.Stream is not null;
            var position = handle.Stream?.Position ?? 0;
            handle.CloseStream();

            _operations.Rename(handle.PoolPath, destination, replaceIfExists);

            handle.PoolPath = destination;
            var part = _topology.Current.FindPart(handle.PartId);
            if (part is null)
            {
                return PoolFsStatus.DeviceNotReady;
            }

            handle.HostPath = PoolOperations.HostPathIn(part, destination);

            if (hadStream)
            {
                var stream = new FileStream(
                    handle.HostPath,
                    FileMode.Open,
                    handle.Access.HasFlag(PoolAccess.Write) ? FileAccess.ReadWrite : FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete)
                {
                    Position = position,
                };

                handle.Rebind(handle.PartId, handle.HostPath, stream);
                handle.RelocationCount--; // a rename is not a relocation
            }

            return PoolFsStatus.Success;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return PoolFsStatusExtensions.FromException(exception);
        }
    }

    public PoolFsStatus CanDelete(PoolFileHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);

        if (!handle.IsDirectory)
        {
            return PoolFsStatus.Success;
        }

        if (PoolPath.IsRoot(handle.PoolPath))
        {
            return PoolFsStatus.CannotDelete;
        }

        try
        {
            return View.EnumerateEntries(handle.PoolPath).Count == 0
                ? PoolFsStatus.Success
                : PoolFsStatus.DirectoryNotEmpty;
        }
        catch (DirectoryNotFoundException)
        {
            return PoolFsStatus.ObjectNameNotFound;
        }
    }

    /// <summary>Called when the last reference goes away. Performs the deferred delete if asked.</summary>
    public PoolFsStatus Cleanup(PoolFileHandle handle, bool delete)
    {
        ArgumentNullException.ThrowIfNull(handle);

        if (!delete && !handle.DeleteOnClose)
        {
            return PoolFsStatus.Success;
        }

        try
        {
            handle.CloseStream();

            if (handle.IsDirectory)
            {
                _operations.DeleteDirectory(handle.PoolPath, recursive: false);
            }
            else
            {
                _operations.DeleteFile(handle.PoolPath);
            }

            return PoolFsStatus.Success;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return PoolFsStatusExtensions.FromException(exception);
        }
    }

    public void Close(PoolFileHandle handle) => handle?.Dispose();

    /// <summary>
    /// Merged directory listing, optionally resumed after <paramref name="marker"/> as WinFsp's
    /// directory buffering expects.
    /// </summary>
    public IReadOnlyList<PoolFileInfo> ReadDirectory(PoolFileHandle handle, string? pattern = null, string? marker = null)
    {
        ArgumentNullException.ThrowIfNull(handle);

        if (!handle.IsDirectory)
        {
            throw new InvalidOperationException($"'{handle.PoolPath}' is not a directory.");
        }

        var entries = View.EnumerateEntries(handle.PoolPath, string.IsNullOrEmpty(pattern) ? "*" : pattern);

        IEnumerable<PoolEntry> sequence = entries;
        if (!string.IsNullOrEmpty(marker))
        {
            sequence = entries.SkipWhile(e => PoolPath.Comparer.Compare(e.Name, marker) <= 0);
        }

        return sequence.Select(entry => new PoolFileInfo
        {
            PoolPath = PoolPath.Combine(handle.PoolPath, entry.Name),
            IsDirectory = entry.IsDirectory,
            Length = entry.Length,
            AllocationSize = Align(entry.Length),
            Attributes = entry.Attributes,
            CreationTimeUtc = entry.CreationTimeUtc,
            LastAccessTimeUtc = entry.LastAccessTimeUtc,
            LastWriteTimeUtc = entry.LastWriteTimeUtc,
            ChangeTimeUtc = entry.LastWriteTimeUtc,
            PartId = entry.PartId,
            HostPath = entry.HostPath,
        }).ToArray();
    }

    /// <summary>
    /// Makes sure the file can grow to <paramref name="requiredLength"/> where it is; otherwise moves
    /// it whole to a drive that fits. Files are never split, so this is the only way a write can
    /// outgrow its drive and still succeed.
    /// </summary>
    private PoolFsStatus EnsureRoomFor(PoolFileHandle handle, long requiredLength)
    {
        var stream = handle.RequireStream();
        var growth = requiredLength - stream.Length;
        if (growth <= 0)
        {
            return PoolFsStatus.Success;
        }

        var snapshot = _topology.Current;
        var current = snapshot.FindPart(handle.PartId);
        if (current is null)
        {
            return PoolFsStatus.DeviceNotReady;
        }

        if (current.FreeBytes - growth >= _options.RelocationReserveBytes)
        {
            return PoolFsStatus.Success;
        }

        var decision = _placement.Select(snapshot, new PlacementRequest
        {
            PoolPath = handle.PoolPath,
            EstimatedSize = requiredLength,
            ExcludedParts = [handle.PartId],
        });

        if (decision is null)
        {
            return PoolFsStatus.DiskFull;
        }

        var target = snapshot.FindPart(decision.PartId);
        if (target is null)
        {
            return PoolFsStatus.DiskFull;
        }

        return RelocateOpenHandle(handle, current, target);
    }

    private PoolFsStatus RelocateOpenHandle(PoolFileHandle handle, PoolPart from, PoolPart target)
    {
        var position = handle.Stream?.Position ?? 0;
        var access = handle.Access.HasFlag(PoolAccess.Write) ? FileAccess.ReadWrite : FileAccess.Read;

        handle.CloseStream();

        try
        {
            var source = new PoolLocation
            {
                Part = from,
                PoolPath = handle.PoolPath,
                HostPath = handle.HostPath,
                IsDirectory = false,
            };

            var result = _relocator.Relocate(source, target);

            var stream = new FileStream(
                result.NewHostPath, FileMode.Open, access, FileShare.ReadWrite | FileShare.Delete)
            {
                Position = position,
            };

            handle.Rebind(target.PartId, result.NewHostPath, stream);
            FileRelocated?.Invoke(this, result);
            return PoolFsStatus.Success;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Put the handle back on the original file so the caller sees a failed write, not a
            // broken handle.
            TryReopen(handle, access, position);
            return PoolFsStatusExtensions.FromException(exception);
        }
    }

    private static void TryReopen(PoolFileHandle handle, FileAccess access, long position)
    {
        try
        {
            if (File.Exists(handle.HostPath))
            {
                var stream = new FileStream(
                    handle.HostPath, FileMode.Open, access, FileShare.ReadWrite | FileShare.Delete)
                {
                    Position = position,
                };

                handle.Rebind(handle.PartId, handle.HostPath, stream);
                handle.RelocationCount--;
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Feeds a completed operation to the metrics tracker, if one is attached.</summary>
    private void Observe(Guid partId, IoKind kind, int bytes, long startedTimestamp)
    {
        if (_ioObserver is null || bytes <= 0)
        {
            return;
        }

        _ioObserver.Record(new IoSample(partId, kind, bytes, Stopwatch.GetElapsedTime(startedTimestamp)));
    }

    private bool ParentExists(string poolPath)
    {
        var parent = PoolPath.GetParent(poolPath);
        return parent is null || PoolPath.IsRoot(parent) || View.FindDirectory(parent) is not null;
    }

    private FileStream OpenStream(string hostPath, FileMode mode, PoolOpenRequest request)
    {
        var access = request.Access.HasFlag(PoolAccess.Write) ? FileAccess.ReadWrite : FileAccess.Read;
        var share = ToFileShare(request.Share);
        var fileOptions = _options.WriteThrough ? FileOptions.WriteThrough : FileOptions.None;

        return new FileStream(hostPath, mode, access, share, _options.StreamBufferSize, fileOptions);
    }

    private static FileShare ToFileShare(PoolShare share)
    {
        var result = FileShare.None;
        if (share.HasFlag(PoolShare.Read))
        {
            result |= FileShare.Read;
        }

        if (share.HasFlag(PoolShare.Write))
        {
            result |= FileShare.Write;
        }

        if (share.HasFlag(PoolShare.Delete))
        {
            result |= FileShare.Delete;
        }

        return result;
    }

    private PoolFileInfo Describe(PoolLocation location)
    {
        var info = new FileInfo(location.HostPath);
        var isDirectory = location.IsDirectory;

        return new PoolFileInfo
        {
            PoolPath = location.PoolPath,
            IsDirectory = isDirectory,
            Length = isDirectory ? 0 : SafeLength(info),
            AllocationSize = isDirectory ? 0 : Align(SafeLength(info)),
            Attributes = SafeAttributes(info, isDirectory),
            CreationTimeUtc = info.CreationTimeUtc,
            LastAccessTimeUtc = info.LastAccessTimeUtc,
            LastWriteTimeUtc = info.LastWriteTimeUtc,
            ChangeTimeUtc = info.LastWriteTimeUtc,
            PartId = location.Part.PartId,
            HostPath = location.HostPath,
        };
    }

    private PoolFileInfo DescribeHandle(PoolFileHandle handle)
    {
        var part = _topology.Current.FindPart(handle.PartId);
        var length = handle.Stream?.Length ?? 0;
        var info = new FileInfo(handle.HostPath);

        return new PoolFileInfo
        {
            PoolPath = handle.PoolPath,
            IsDirectory = handle.IsDirectory,
            Length = handle.IsDirectory ? 0 : length,
            AllocationSize = handle.IsDirectory ? 0 : Align(length),
            Attributes = SafeAttributes(info, handle.IsDirectory),
            CreationTimeUtc = info.CreationTimeUtc,
            LastAccessTimeUtc = info.LastAccessTimeUtc,
            LastWriteTimeUtc = info.LastWriteTimeUtc,
            ChangeTimeUtc = info.LastWriteTimeUtc,
            PartId = part?.PartId ?? handle.PartId,
            HostPath = handle.HostPath,
        };
    }

    private static long SafeLength(FileInfo info)
    {
        try
        {
            return info.Exists ? info.Length : 0;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    private static FileAttributes SafeAttributes(FileInfo info, bool isDirectory)
    {
        try
        {
            if (info.Exists || Directory.Exists(info.FullName))
            {
                var attributes = File.GetAttributes(info.FullName);
                return isDirectory ? attributes | FileAttributes.Directory : attributes;
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return isDirectory ? FileAttributes.Directory : FileAttributes.Normal;
    }

    private static long Align(long length)
    {
        const long clusterSize = 4096;
        return (length + clusterSize - 1) / clusterSize * clusterSize;
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

public sealed class PoolFileSystemOptions
{
    /// <summary>Free space kept on a drive before a growing file is moved off it.</summary>
    public long RelocationReserveBytes { get; init; } = 64L * 1024 * 1024;

    public int StreamBufferSize { get; init; } = 0x10000;

    /// <summary>Bypasses the host cache. WinFsp does its own caching, so this stays on.</summary>
    public bool WriteThrough { get; init; }
}
