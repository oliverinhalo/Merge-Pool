namespace MergePool.Core.FileSystem;

/// <summary>
/// Outcome of a pool file system operation. Deliberately mirrors the NTSTATUS values WinFsp
/// expects, so the WinFsp adapter is a lookup table and carries no logic of its own.
/// </summary>
public enum PoolFsStatus
{
    Success = 0,
    EndOfFile,
    ObjectNameNotFound,
    ObjectPathNotFound,
    ObjectNameCollision,
    AccessDenied,
    SharingViolation,
    DiskFull,
    DirectoryNotEmpty,
    NotADirectory,
    FileIsADirectory,
    MediaWriteProtected,
    InvalidParameter,
    DeviceNotReady,
    IoError,
    CannotDelete,
    NoSuchDevice,
}

public static class PoolFsStatusExtensions
{
    public static bool IsSuccess(this PoolFsStatus status) => status == PoolFsStatus.Success;

    /// <summary>Maps a host IO exception onto the status the pool reports.</summary>
    public static PoolFsStatus FromException(Exception exception) => exception switch
    {
        FileNotFoundException => PoolFsStatus.ObjectNameNotFound,
        DirectoryNotFoundException => PoolFsStatus.ObjectPathNotFound,
        UnauthorizedAccessException => PoolFsStatus.AccessDenied,
        PathTooLongException => PoolFsStatus.InvalidParameter,
        ArgumentException => PoolFsStatus.InvalidParameter,
        NotSupportedException => PoolFsStatus.InvalidParameter,
        IOException io => FromIoException(io),
        _ => PoolFsStatus.IoError,
    };

    private static PoolFsStatus FromIoException(IOException exception)
    {
        // HRESULT low word is the Win32 error code.
        const int ErrorDiskFull = 0x70;
        const int ErrorHandleDiskFull = 0x27;
        const int ErrorSharingViolation = 0x20;
        const int ErrorLockViolation = 0x21;
        const int ErrorDirNotEmpty = 0x91;
        const int ErrorFileExists = 0x50;
        const int ErrorAlreadyExists = 0xB7;
        const int ErrorWriteProtect = 0x13;
        const int ErrorNotReady = 0x15;

        return (exception.HResult & 0xFFFF) switch
        {
            ErrorDiskFull or ErrorHandleDiskFull => PoolFsStatus.DiskFull,
            ErrorSharingViolation or ErrorLockViolation => PoolFsStatus.SharingViolation,
            ErrorDirNotEmpty => PoolFsStatus.DirectoryNotEmpty,
            ErrorFileExists or ErrorAlreadyExists => PoolFsStatus.ObjectNameCollision,
            ErrorWriteProtect => PoolFsStatus.MediaWriteProtected,
            ErrorNotReady => PoolFsStatus.DeviceNotReady,
            _ => PoolFsStatus.IoError,
        };
    }
}
