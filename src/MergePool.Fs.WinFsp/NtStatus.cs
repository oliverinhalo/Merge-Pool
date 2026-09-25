using MergePool.Core.FileSystem;

namespace MergePool.Fs.WinFsp;

/// <summary>
/// NTSTATUS values MergePool returns to WinFsp. Declared here rather than taken from the WinFsp
/// assembly so the mapping is explicit and stays valid across WinFsp releases.
/// </summary>
internal static class NtStatus
{
    public const int Success = 0;
    public const int EndOfFile = unchecked((int)0xC0000011);
    public const int AccessDenied = unchecked((int)0xC0000022);
    public const int ObjectNameNotFound = unchecked((int)0xC0000034);
    public const int ObjectNameCollision = unchecked((int)0xC0000035);
    public const int ObjectPathNotFound = unchecked((int)0xC000003A);
    public const int SharingViolation = unchecked((int)0xC0000043);
    public const int DiskFull = unchecked((int)0xC000007F);
    public const int FileIsADirectory = unchecked((int)0xC00000BA);
    public const int NotADirectory = unchecked((int)0xC0000103);
    public const int DirectoryNotEmpty = unchecked((int)0xC0000101);
    public const int MediaWriteProtected = unchecked((int)0xC00000A2);
    public const int InvalidParameter = unchecked((int)0xC000000D);
    public const int DeviceNotReady = unchecked((int)0xC00000A3);
    public const int NoSuchDevice = unchecked((int)0xC000000E);
    public const int DataError = unchecked((int)0xC000003E);
    public const int CannotDelete = unchecked((int)0xC0000121);

    public static int From(PoolFsStatus status) => status switch
    {
        PoolFsStatus.Success => Success,
        PoolFsStatus.EndOfFile => EndOfFile,
        PoolFsStatus.ObjectNameNotFound => ObjectNameNotFound,
        PoolFsStatus.ObjectPathNotFound => ObjectPathNotFound,
        PoolFsStatus.ObjectNameCollision => ObjectNameCollision,
        PoolFsStatus.AccessDenied => AccessDenied,
        PoolFsStatus.SharingViolation => SharingViolation,
        PoolFsStatus.DiskFull => DiskFull,
        PoolFsStatus.DirectoryNotEmpty => DirectoryNotEmpty,
        PoolFsStatus.NotADirectory => NotADirectory,
        PoolFsStatus.FileIsADirectory => FileIsADirectory,
        PoolFsStatus.MediaWriteProtected => MediaWriteProtected,
        PoolFsStatus.InvalidParameter => InvalidParameter,
        PoolFsStatus.DeviceNotReady => DeviceNotReady,
        PoolFsStatus.NoSuchDevice => NoSuchDevice,
        PoolFsStatus.CannotDelete => CannotDelete,
        _ => DataError,
    };
}
