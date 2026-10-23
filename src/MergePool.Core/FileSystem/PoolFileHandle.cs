using Microsoft.Win32.SafeHandles;

namespace MergePool.Core.FileSystem;

/// <summary>
/// An open pool file or directory. The handle survives the file being relocated to another drive
/// mid-write: only <see cref="PartId"/>, <see cref="HostPath"/> and the stream change.
/// </summary>
/// <remarks>
/// <para>WinFsp dispatches calls for one handle on as many threads as there is traffic, so
/// everything here has to be safe under concurrent use. Two rules make it so.</para>
/// <para>First, data is read and written by absolute offset, through
/// <see cref="System.IO.RandomAccess"/> on <see cref="FileHandle"/>, never by seeking a shared
/// stream. Seeking was a correctness bug, not just a slow one: two threads interleaving "set
/// position" and "read" hand each other's offsets back, so a read returns another read's bytes with
/// no error anywhere.</para>
/// <para>Second, relocation — moving the file to another drive mid-write — swaps the stream out from
/// under whoever is using it. Readers take <see cref="EnterIo"/> (shared) and the swap takes
/// <see cref="EnterExclusive"/>, so a relocation waits for in-flight I/O rather than disposing the
/// stream underneath it. Both are bounded: a wait that runs out is reported as a timeout, because an
/// I/O that never returns takes the whole volume down with it.</para>
/// </remarks>
public sealed class PoolFileHandle : IDisposable
{
    /// <summary>
    /// How long a read or write waits for a relocation to finish. Generous — a legitimately slow
    /// drive must not fail — but finite, which is the whole point.
    /// </summary>
    public static readonly TimeSpan DefaultIoWait = TimeSpan.FromSeconds(30);

    /// <summary>How long a relocation waits for in-flight reads and writes to drain.</summary>
    public static readonly TimeSpan DefaultExclusiveWait = TimeSpan.FromSeconds(60);

    private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.NoRecursion);
    private FileStream? _stream;

    internal PoolFileHandle(string poolPath, bool isDirectory, Guid partId, string hostPath, FileStream? stream)
    {
        PoolPath = poolPath;
        IsDirectory = isDirectory;
        PartId = partId;
        HostPath = hostPath;
        _stream = stream;
    }

    public Guid Id { get; } = Guid.NewGuid();

    /// <summary>Path inside the pool. Updated in place by a rename.</summary>
    public string PoolPath { get; internal set; }

    public bool IsDirectory { get; }

    /// <summary>Drive currently holding the file.</summary>
    public Guid PartId { get; internal set; }

    /// <summary>Real path on that drive, for pass-through of ACLs, streams and attributes.</summary>
    public string HostPath { get; internal set; }

    public PoolAccess Access { get; internal set; }

    public bool DeleteOnClose { get; internal set; }

    public bool IsClosed { get; private set; }

    /// <summary>Set when the handle has been relocated, so callers can log the move.</summary>
    public int RelocationCount { get; internal set; }

    internal FileStream? Stream => _stream;

    internal FileStream RequireStream() =>
        _stream ?? throw new InvalidOperationException($"'{PoolPath}' is not open for data access.");

    /// <summary>
    /// The OS handle, for positional reads and writes. Safe to use from several threads at once,
    /// which a <see cref="FileStream"/>'s position is not.
    /// </summary>
    internal SafeFileHandle FileHandle => RequireStream().SafeFileHandle;

    /// <summary>
    /// Takes the shared side of the handle lock for one read or write. Returns false when the wait
    /// ran out, which the caller reports as a timeout rather than waiting for ever.
    /// </summary>
    internal bool EnterIo(TimeSpan? timeout = null)
    {
        if (IsClosed)
        {
            return false;
        }

        return _lock.TryEnterReadLock(timeout ?? DefaultIoWait);
    }

    internal void ExitIo() => _lock.ExitReadLock();

    /// <summary>
    /// Takes the exclusive side, for the operations that change what the handle points at:
    /// relocation, truncation, closing.
    /// </summary>
    internal bool EnterExclusive(TimeSpan? timeout = null) =>
        _lock.TryEnterWriteLock(timeout ?? DefaultExclusiveWait);

    internal void ExitExclusive() => _lock.ExitWriteLock();

    /// <summary>Swaps in the stream and location after the file has been moved to another drive.</summary>
    /// <remarks>Callers hold the exclusive lock: this disposes a stream others may be reading.</remarks>
    internal void Rebind(Guid partId, string hostPath, FileStream? stream)
    {
        _stream?.Dispose();
        _stream = stream;
        PartId = partId;
        HostPath = hostPath;
        RelocationCount++;
    }

    internal void CloseStream()
    {
        _stream?.Dispose();
        _stream = null;
    }

    public void Dispose()
    {
        if (IsClosed)
        {
            return;
        }

        IsClosed = true;

        // Waits for in-flight I/O before tearing the stream down. A close that disposes a stream
        // another thread is reading is how a hang becomes an ObjectDisposedException in the kernel's
        // I/O path, which is worse.
        var exclusive = EnterExclusive();
        try
        {
            CloseStream();
        }
        finally
        {
            if (exclusive)
            {
                ExitExclusive();
            }
        }

        _lock.Dispose();
    }
}
