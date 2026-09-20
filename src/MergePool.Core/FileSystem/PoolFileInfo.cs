namespace MergePool.Core.FileSystem;

/// <summary>Metadata for one pool entry, passed through unchanged from the drive holding it.</summary>
public sealed record PoolFileInfo
{
    public required string PoolPath { get; init; }

    public required bool IsDirectory { get; init; }

    public long Length { get; init; }

    /// <summary>Allocated size on the drive. WinFsp reports it separately from the file length.</summary>
    public long AllocationSize { get; init; }

    public FileAttributes Attributes { get; init; }

    public DateTimeOffset CreationTimeUtc { get; init; }

    public DateTimeOffset LastAccessTimeUtc { get; init; }

    public DateTimeOffset LastWriteTimeUtc { get; init; }

    public DateTimeOffset ChangeTimeUtc { get; init; }

    /// <summary>The drive currently holding this entry.</summary>
    public Guid PartId { get; init; }

    /// <summary>The real path on that drive. Used for pass-through of security and streams.</summary>
    public required string HostPath { get; init; }
}

/// <summary>Capacity of the pool as a whole, i.e. the sum of the drives we can see.</summary>
public sealed record PoolVolumeInfo
{
    public required long TotalBytes { get; init; }

    public required long FreeBytes { get; init; }

    public required string Label { get; init; }
}
