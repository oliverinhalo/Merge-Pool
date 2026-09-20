using System.Runtime.Versioning;
using System.Security.AccessControl;
using Fsp;
using MergePool.Core.FileSystem;
using MergePool.Core.Paths;
using VolumeInfo = Fsp.Interop.VolumeInfo;
using WinFspFileInfo = Fsp.Interop.FileInfo;

namespace MergePool.Fs.WinFsp;

/// <summary>
/// Translates WinFsp callbacks onto <see cref="PoolFileSystemEngine"/>. Deliberately contains no
/// pool logic of its own: everything here is type and status translation, so pool behaviour stays
/// testable without a mount.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PoolWinFspFileSystem(PoolFileSystemEngine engine, string volumeLabel = "MergePool") : FileSystemBase
{
    private const uint FileDirectoryFile = 0x00000001;
    private const uint FileDeleteOnClose = 0x00001000;

    private const uint CleanupDelete = 0x01;

    private readonly PoolFileSystemEngine _engine = engine ?? throw new ArgumentNullException(nameof(engine));

    private string _volumeLabel = volumeLabel;

    public override int Init(object host)
    {
        if (host is FileSystemHost fileSystemHost)
        {
            fileSystemHost.SectorSize = 4096;
            fileSystemHost.SectorsPerAllocationUnit = 1;
            fileSystemHost.MaxComponentLength = 255;
            fileSystemHost.FileInfoTimeout = 1000;
            fileSystemHost.CaseSensitiveSearch = false;
            fileSystemHost.CasePreservedNames = true;
            fileSystemHost.UnicodeOnDisk = true;
            fileSystemHost.PersistentAcls = true;
            fileSystemHost.PostCleanupWhenModifiedOnly = true;
            fileSystemHost.VolumeCreationTime = 0;
            fileSystemHost.VolumeSerialNumber = 0;
            fileSystemHost.FileSystemName = "MergePool";
        }

        return NtStatus.Success;
    }

    public override int GetVolumeInfo(out VolumeInfo volumeInfo)
    {
        volumeInfo = default;
        var info = _engine.GetVolumeInfo();
        volumeInfo.TotalSize = (ulong)Math.Max(0, info.TotalBytes);
        volumeInfo.FreeSize = (ulong)Math.Max(0, info.FreeBytes);
        volumeInfo.SetVolumeLabel(_volumeLabel);
        return NtStatus.Success;
    }

    public override int SetVolumeLabel(string volumeLabel, out VolumeInfo volumeInfo)
    {
        _volumeLabel = volumeLabel;
        return GetVolumeInfo(out volumeInfo);
    }

    public override int GetSecurityByName(
        string fileName,
        out uint fileAttributes,
        ref byte[] securityDescriptor)
    {
        fileAttributes = 0;

        var status = _engine.GetFileInfo(ToPoolPath(fileName), out var info);
        if (status != PoolFsStatus.Success || info is null)
        {
            return NtStatus.From(status);
        }

        fileAttributes = (uint)info.Attributes;
        if (securityDescriptor is not null)
        {
            securityDescriptor = SecurityDescriptorInterop.TryGetDescriptor(info.HostPath) ?? securityDescriptor;
        }

        return NtStatus.Success;
    }

    public override int Create(
        string fileName,
        uint createOptions,
        uint grantedAccess,
        uint fileAttributes,
        byte[] securityDescriptor,
        ulong allocationSize,
        out object fileNode,
        out object fileDesc,
        out WinFspFileInfo fileInfo,
        out string normalizedName)
    {
        fileNode = null!;
        fileDesc = null!;
        fileInfo = default;
        normalizedName = null!;

        var isDirectory = (createOptions & FileDirectoryFile) != 0;
        var request = new PoolOpenRequest
        {
            PoolPath = ToPoolPath(fileName),
            Disposition = PoolCreateDisposition.Create,
            Access = PoolAccess.ReadWrite,
            Share = PoolShare.All,
            IsDirectory = isDirectory,
            AllocationSize = (long)Math.Min(allocationSize, long.MaxValue),
            Attributes = (FileAttributes)fileAttributes,
            DeleteOnClose = (createOptions & FileDeleteOnClose) != 0,
        };

        var status = _engine.Open(request, out var handle, out var info);
        if (status != PoolFsStatus.Success || handle is null || info is null)
        {
            return NtStatus.From(status);
        }

        SecurityDescriptorInterop.ApplyOnCreate(handle.HostPath, securityDescriptor);

        fileDesc = handle;
        fileInfo = ToWinFsp(info);
        return NtStatus.Success;
    }

    public override int Open(
        string fileName,
        uint createOptions,
        uint grantedAccess,
        out object fileNode,
        out object fileDesc,
        out WinFspFileInfo fileInfo,
        out string normalizedName)
    {
        fileNode = null!;
        fileDesc = null!;
        fileInfo = default;
        normalizedName = null!;

        var poolPath = ToPoolPath(fileName);
        var probe = _engine.GetFileInfo(poolPath, out var existing);
        if (probe != PoolFsStatus.Success || existing is null)
        {
            return NtStatus.From(probe);
        }

        var request = new PoolOpenRequest
        {
            PoolPath = poolPath,
            Disposition = PoolCreateDisposition.Open,
            Access = PoolAccess.ReadWrite,
            Share = PoolShare.All,
            IsDirectory = existing.IsDirectory,
            DeleteOnClose = (createOptions & FileDeleteOnClose) != 0,
        };

        var status = _engine.Open(request, out var handle, out var info);
        if (status != PoolFsStatus.Success || handle is null || info is null)
        {
            return NtStatus.From(status);
        }

        fileDesc = handle;
        fileInfo = ToWinFsp(info);
        return NtStatus.Success;
    }

    public override int Overwrite(
        object fileNode,
        object fileDesc,
        uint fileAttributes,
        bool replaceFileAttributes,
        ulong allocationSize,
        out WinFspFileInfo fileInfo)
    {
        fileInfo = default;
        var handle = (PoolFileHandle)fileDesc;

        var status = _engine.SetFileSize(handle, 0, setAllocationSize: false, out var info);
        if (status != PoolFsStatus.Success || info is null)
        {
            return NtStatus.From(status);
        }

        if (fileAttributes != 0)
        {
            var attributes = replaceFileAttributes
                ? (FileAttributes)fileAttributes
                : info.Attributes | (FileAttributes)fileAttributes;

            status = _engine.SetBasicInfo(handle, attributes, null, null, null, out info);
            if (status != PoolFsStatus.Success || info is null)
            {
                return NtStatus.From(status);
            }
        }

        fileInfo = ToWinFsp(info);
        return NtStatus.Success;
    }

    public override void Cleanup(object fileNode, object fileDesc, string fileName, uint flags)
    {
        if (fileDesc is PoolFileHandle handle)
        {
            _engine.Cleanup(handle, delete: (flags & CleanupDelete) != 0);
        }
    }

    public override void Close(object fileNode, object fileDesc)
    {
        if (fileDesc is PoolFileHandle handle)
        {
            _engine.Close(handle);
        }
    }

    public override int Read(
        object fileNode,
        object fileDesc,
        IntPtr buffer,
        ulong offset,
        uint length,
        out uint bytesTransferred)
    {
        bytesTransferred = 0;
        var handle = (PoolFileHandle)fileDesc;

        unsafe
        {
            var span = new Span<byte>(buffer.ToPointer(), (int)length);
            var status = _engine.Read(handle, span, (long)offset, out var read);
            bytesTransferred = (uint)Math.Max(0, read);
            return NtStatus.From(status);
        }
    }

    public override int Write(
        object fileNode,
        object fileDesc,
        IntPtr buffer,
        ulong offset,
        uint length,
        bool writeToEndOfFile,
        bool constrainedIo,
        out uint bytesTransferred,
        out WinFspFileInfo fileInfo)
    {
        bytesTransferred = 0;
        fileInfo = default;
        var handle = (PoolFileHandle)fileDesc;

        unsafe
        {
            var span = new ReadOnlySpan<byte>(buffer.ToPointer(), (int)length);
            var status = _engine.Write(
                handle, span, (long)offset, writeToEndOfFile, constrainedIo, out var written, out var info);

            if (status != PoolFsStatus.Success || info is null)
            {
                return NtStatus.From(status);
            }

            bytesTransferred = (uint)Math.Max(0, written);
            fileInfo = ToWinFsp(info);
            return NtStatus.Success;
        }
    }

    public override int Flush(object fileNode, object fileDesc, out WinFspFileInfo fileInfo)
    {
        fileInfo = default;
        var handle = fileDesc as PoolFileHandle;

        var status = _engine.Flush(handle, out var info);
        if (status != PoolFsStatus.Success)
        {
            return NtStatus.From(status);
        }

        if (info is not null)
        {
            fileInfo = ToWinFsp(info);
        }

        return NtStatus.Success;
    }

    public override int GetFileInfo(object fileNode, object fileDesc, out WinFspFileInfo fileInfo)
    {
        fileInfo = default;
        var handle = (PoolFileHandle)fileDesc;

        var status = _engine.GetFileInfo(handle.PoolPath, out var info);
        if (status != PoolFsStatus.Success || info is null)
        {
            return NtStatus.From(status);
        }

        fileInfo = ToWinFsp(info);
        return NtStatus.Success;
    }

    public override int SetBasicInfo(
        object fileNode,
        object fileDesc,
        uint fileAttributes,
        ulong creationTime,
        ulong lastAccessTime,
        ulong lastWriteTime,
        ulong changeTime,
        out WinFspFileInfo fileInfo)
    {
        fileInfo = default;
        var handle = (PoolFileHandle)fileDesc;

        var status = _engine.SetBasicInfo(
            handle,
            fileAttributes == unchecked((uint)-1) ? null : (FileAttributes)fileAttributes,
            FromFileTime(creationTime),
            FromFileTime(lastAccessTime),
            FromFileTime(lastWriteTime),
            out var info);

        if (status != PoolFsStatus.Success || info is null)
        {
            return NtStatus.From(status);
        }

        fileInfo = ToWinFsp(info);
        return NtStatus.Success;
    }

    public override int SetFileSize(
        object fileNode,
        object fileDesc,
        ulong newSize,
        bool setAllocationSize,
        out WinFspFileInfo fileInfo)
    {
        fileInfo = default;
        var handle = (PoolFileHandle)fileDesc;

        var status = _engine.SetFileSize(handle, (long)Math.Min(newSize, long.MaxValue), setAllocationSize, out var info);
        if (status != PoolFsStatus.Success || info is null)
        {
            return NtStatus.From(status);
        }

        fileInfo = ToWinFsp(info);
        return NtStatus.Success;
    }

    public override int CanDelete(object fileNode, object fileDesc, string fileName) =>
        NtStatus.From(_engine.CanDelete((PoolFileHandle)fileDesc));

    public override int Rename(object fileNode, object fileDesc, string fileName, string newFileName, bool replaceIfExists) =>
        NtStatus.From(_engine.Rename((PoolFileHandle)fileDesc, ToPoolPath(newFileName), replaceIfExists));

    public override int GetSecurity(object fileNode, object fileDesc, ref byte[] securityDescriptor)
    {
        var handle = (PoolFileHandle)fileDesc;
        var descriptor = SecurityDescriptorInterop.TryGetDescriptor(handle.HostPath);
        if (descriptor is null)
        {
            return NtStatus.AccessDenied;
        }

        securityDescriptor = descriptor;
        return NtStatus.Success;
    }

    public override int SetSecurity(object fileNode, object fileDesc, AccessControlSections sections, byte[] securityDescriptor)
    {
        var handle = (PoolFileHandle)fileDesc;
        return SecurityDescriptorInterop.TrySetDescriptor(handle.HostPath, securityDescriptor, sections)
            ? NtStatus.Success
            : NtStatus.AccessDenied;
    }

    public override bool ReadDirectoryEntry(
        object fileNode,
        object fileDesc,
        string pattern,
        string marker,
        ref object context,
        out string fileName,
        out WinFspFileInfo fileInfo)
    {
        fileName = null!;
        fileInfo = default;

        var handle = (PoolFileHandle)fileDesc;

        if (context is not DirectoryCursor cursor)
        {
            cursor = new DirectoryCursor(_engine.ReadDirectory(handle, pattern, marker));
            context = cursor;
        }

        if (!cursor.TryNext(out var entry) || entry is null)
        {
            return false;
        }

        fileName = PoolPath.GetName(entry.PoolPath);
        fileInfo = ToWinFsp(entry);
        return true;
    }

    public override int GetDirInfoByName(object fileNode, object fileDesc, string fileName, out WinFspFileInfo fileInfo)
    {
        fileInfo = default;
        var handle = (PoolFileHandle)fileDesc;
        var poolPath = PoolPath.Combine(handle.PoolPath, fileName);

        var status = _engine.GetFileInfo(poolPath, out var info);
        if (status != PoolFsStatus.Success || info is null)
        {
            return NtStatus.From(status);
        }

        fileInfo = ToWinFsp(info);
        return NtStatus.Success;
    }

    public override int ExceptionHandler(Exception exception) =>
        NtStatus.From(PoolFsStatusExtensions.FromException(exception));

    /// <summary>Holds the merged listing while WinFsp pages through it.</summary>
    private sealed class DirectoryCursor(IReadOnlyList<PoolFileInfo> entries)
    {
        private int _index;

        public bool TryNext(out PoolFileInfo? entry)
        {
            if (_index >= entries.Count)
            {
                entry = null;
                return false;
            }

            entry = entries[_index++];
            return true;
        }
    }

    private static string ToPoolPath(string winFspPath) => PoolPath.Normalize(winFspPath);

    private static DateTimeOffset? FromFileTime(ulong fileTime) =>
        fileTime == 0 ? null : DateTimeOffset.FromFileTime((long)fileTime).ToUniversalTime();

    private static ulong ToFileTime(DateTimeOffset value)
    {
        try
        {
            return (ulong)value.UtcDateTime.ToFileTimeUtc();
        }
        catch (ArgumentOutOfRangeException)
        {
            return 0;
        }
    }

    private static WinFspFileInfo ToWinFsp(PoolFileInfo info) => new()
    {
        FileAttributes = (uint)info.Attributes,
        ReparseTag = 0,
        AllocationSize = (ulong)Math.Max(0, info.AllocationSize),
        FileSize = (ulong)Math.Max(0, info.Length),
        CreationTime = ToFileTime(info.CreationTimeUtc),
        LastAccessTime = ToFileTime(info.LastAccessTimeUtc),
        LastWriteTime = ToFileTime(info.LastWriteTimeUtc),
        ChangeTime = ToFileTime(info.ChangeTimeUtc),
        IndexNumber = 0,
        HardLinks = 0,
    };
}
