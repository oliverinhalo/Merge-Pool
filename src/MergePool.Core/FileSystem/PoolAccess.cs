namespace MergePool.Core.FileSystem;

[Flags]
public enum PoolAccess
{
    None = 0,
    Read = 1,
    Write = 2,
    Delete = 4,
    ReadWrite = Read | Write,
}

[Flags]
public enum PoolShare
{
    None = 0,
    Read = 1,
    Write = 2,
    Delete = 4,
    All = Read | Write | Delete,
}

/// <summary>How an open request should behave when the target does or does not exist.</summary>
public enum PoolCreateDisposition
{
    /// <summary>Fail if missing.</summary>
    Open = 0,

    /// <summary>Create, fail if it exists.</summary>
    Create = 1,

    /// <summary>Open, creating when missing.</summary>
    OpenOrCreate = 2,

    /// <summary>Open and truncate, fail if missing.</summary>
    Overwrite = 3,

    /// <summary>Create or truncate.</summary>
    OverwriteOrCreate = 4,
}

public sealed record PoolOpenRequest
{
    public required string PoolPath { get; init; }

    public PoolCreateDisposition Disposition { get; init; } = PoolCreateDisposition.Open;

    public PoolAccess Access { get; init; } = PoolAccess.Read;

    public PoolShare Share { get; init; } = PoolShare.Read;

    public bool IsDirectory { get; init; }

    /// <summary>Best known final size, used to choose a drive that will not need a mid-write move.</summary>
    public long AllocationSize { get; init; }

    public FileAttributes Attributes { get; init; } = FileAttributes.Normal;

    public bool DeleteOnClose { get; init; }
}
