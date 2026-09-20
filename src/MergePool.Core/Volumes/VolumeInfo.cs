namespace MergePool.Core.Volumes;

public enum VolumeKind
{
    Unknown = 0,
    Fixed = 1,
    Removable = 2,
    Network = 3,
    Optical = 4,
    Ram = 5,
}

/// <summary>A snapshot of one volume as the machine currently sees it.</summary>
public sealed record VolumeInfo
{
    public required VolumeId Id { get; init; }

    /// <summary>Current mount root (e.g. <c>D:\</c>), or <c>null</c> when the volume has no mount point.</summary>
    public string? RootPath { get; init; }

    /// <summary>Drive letter without the colon, or <c>null</c> when unmounted / mounted as a folder.</summary>
    public char? DriveLetter { get; init; }

    public string Label { get; init; } = string.Empty;

    public string FileSystem { get; init; } = string.Empty;

    public VolumeKind Kind { get; init; } = VolumeKind.Unknown;

    public long TotalBytes { get; init; }

    public long FreeBytes { get; init; }

    public bool IsReady { get; init; }

    /// <summary>True when the volume can host a pool part (ready, writable kind, has a mount root).</summary>
    public bool IsPoolable =>
        IsReady
        && RootPath is not null
        && Kind is VolumeKind.Fixed or VolumeKind.Removable
        && TotalBytes > 0;
}
