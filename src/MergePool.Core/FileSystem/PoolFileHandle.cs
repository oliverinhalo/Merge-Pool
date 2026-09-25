namespace MergePool.Core.FileSystem;

/// <summary>
/// An open pool file or directory. The handle survives the file being relocated to another drive
/// mid-write: only <see cref="PartId"/>, <see cref="HostPath"/> and the stream change.
/// </summary>
public sealed class PoolFileHandle : IDisposable
{
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

    /// <summary>Swaps in the stream and location after the file has been moved to another drive.</summary>
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
        CloseStream();
    }
}
