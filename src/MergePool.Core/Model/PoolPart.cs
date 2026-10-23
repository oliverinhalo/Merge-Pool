using MergePool.Core.Volumes;

namespace MergePool.Core.Model;

public enum PoolPartState
{
    /// <summary>
    /// The volume is definitely not present — Windows did not list it at all. The pool runs
    /// degraded and the drive rejoins automatically when it comes back.
    /// </summary>
    Offline = 0,

    /// <summary>Present and taking new files.</summary>
    Online = 1,

    /// <summary>Present and readable, but excluded from new placement (throttled, full, read-only, draining).</summary>
    ReadOnly = 2,

    /// <summary>
    /// We could not determine this drive's state right now: the volume query failed or timed out, or
    /// its pool part folder could not be read. The drive has probably not gone anywhere.
    /// </summary>
    /// <remarks>
    /// This state exists because of a reported near-miss. A drive that hiccups for a moment used to
    /// resolve as <see cref="Offline"/>, exactly like a drive someone had unplugged. Its files
    /// dropped out of the merged view, and a folder whose content lived on it listed as <em>empty
    /// and fine</em> — the pool said "this folder has nothing in it", not "I cannot read this
    /// folder". An application checking for a marker file reads that as deletion, and acts on it.
    /// <para>So the two cases are now told apart. A drive that is merely unreachable leaves the pool
    /// <em>incomplete</em> rather than degraded: listings and "it does not exist" answers from an
    /// incomplete pool are refused instead of being served as fact. Only once the drive has been
    /// definitively absent for the grace period does it become <see cref="Offline"/> and the pool
    /// settle into ordinary degraded service, where those answers are true again.</para>
    /// </remarks>
    Unavailable = 3,
}

/// <summary>A resolved pool part: a <c>.PoolPart-{GUID}</c> folder on a currently known volume.</summary>
public sealed record PoolPart
{
    public required Guid PartId { get; init; }

    public required VolumeId Volume { get; init; }

    /// <summary>Absolute host path of the pool part folder, or <c>null</c> when offline.</summary>
    public string? RootPath { get; init; }

    public PoolPartState State { get; init; } = PoolPartState.Offline;

    public long TotalBytes { get; init; }

    public long FreeBytes { get; init; }

    /// <summary>
    /// Bytes the pool itself holds on this drive, or <c>null</c> until it has been measured. This is
    /// not <c>TotalBytes - FreeBytes</c>: the difference is everything on the drive that is not
    /// pooled.
    /// </summary>
    public long? PoolBytes { get; init; }

    /// <summary>Files the pool holds on this drive, or <c>null</c> until measured.</summary>
    public int? PoolFileCount { get; init; }

    /// <summary>Relative speed of this drive, 1.0 being the nominal baseline. Feeds placement scoring.</summary>
    public double SpeedFactor { get; init; } = 1.0;

    /// <summary>Set when the throttle detector has parked this drive; it still serves reads.</summary>
    public bool IsThrottled { get; init; }

    public string Label { get; init; } = string.Empty;

    public char? DriveLetter { get; init; }

    /// <summary>What the drive is using in total, pooled or not.</summary>
    public long DriveUsedBytes => Math.Max(0, TotalBytes - FreeBytes);

    /// <summary>What is on the drive but outside the pool. Null while the pool's own usage is unknown.</summary>
    public long? ForeignBytes => PoolBytes is { } pooled ? Math.Max(0, DriveUsedBytes - pooled) : null;

    /// <summary>
    /// The space this drive contributes to the pool: what the pool already holds here, plus what is
    /// still free. Space taken by files outside the pool is not the pool's to offer.
    /// </summary>
    public long PoolCapacityBytes => PoolBytes is { } pooled ? pooled + FreeBytes : TotalBytes;

    /// <summary>
    /// Readable right now, so it contributes to the merged view. An unavailable part is not online:
    /// we cannot read it, and pretending we can is what hid files.
    /// </summary>
    public bool IsOnline => State is (PoolPartState.Online or PoolPartState.ReadOnly) && RootPath is not null;

    public bool AcceptsNewFiles => State is PoolPartState.Online && RootPath is not null;

    /// <summary>
    /// Whether we know what this drive is doing. False means the merged view is missing a part whose
    /// content we cannot account for, so a listing or a miss from it is not evidence of anything.
    /// </summary>
    public bool IsPresenceKnown => State is not PoolPartState.Unavailable;

    /// <summary>Host root, or a throw when the caller forgot to check <see cref="IsOnline"/>.</summary>
    public string RequireRootPath() =>
        RootPath ?? throw new InvalidOperationException($"Pool part {PartId:D} is offline.");
}
