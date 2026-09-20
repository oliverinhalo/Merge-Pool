using MergePool.Core.Model;

namespace MergePool.Core.Union;

/// <summary>Where a pool path physically lives: which part, and the host path inside it.</summary>
public sealed record PoolLocation
{
    public required PoolPart Part { get; init; }

    public required string PoolPath { get; init; }

    public required string HostPath { get; init; }

    public required bool IsDirectory { get; init; }
}

/// <summary>One entry of a merged directory listing.</summary>
public sealed record PoolEntry
{
    public required string Name { get; init; }

    public required bool IsDirectory { get; init; }

    public long Length { get; init; }

    public FileAttributes Attributes { get; init; }

    public DateTimeOffset CreationTimeUtc { get; init; }

    public DateTimeOffset LastWriteTimeUtc { get; init; }

    public DateTimeOffset LastAccessTimeUtc { get; init; }

    /// <summary>The part that won the merge for this name.</summary>
    public required Guid PartId { get; init; }

    public required string HostPath { get; init; }
}
