using System.Globalization;

namespace MergePool.Core.Duplication;

/// <summary>What has to happen to one file to bring it in line with its rule.</summary>
public enum DuplicationActionKind
{
    /// <summary>A drive that should hold a copy does not. One is written there.</summary>
    AddCopy = 0,

    /// <summary>A copy differs from the newest one. It is written again from the newest.</summary>
    RefreshCopy = 1,

    /// <summary>A copy the rule no longer asks for. It is deleted; the rule's copies stay.</summary>
    RemoveCopy = 2,
}

/// <summary>One file, one drive, one thing to do about it.</summary>
public sealed record DuplicationAction
{
    public required DuplicationActionKind Kind { get; init; }

    public required Guid RuleId { get; init; }

    public required string PoolPath { get; init; }

    /// <summary>The copy being read. Null for <see cref="DuplicationActionKind.RemoveCopy"/>.</summary>
    public string? SourceHostPath { get; init; }

    public Guid SourcePartId { get; init; }

    /// <summary>The copy being written, or the one being deleted.</summary>
    public required string TargetHostPath { get; init; }

    public required Guid TargetPartId { get; init; }

    public long Bytes { get; init; }

    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture,
        $"{Kind} {PoolPath} on {TargetPartId:D} ({Bytes} bytes)");
}

/// <summary>A file the rule cannot be met for, and why — the thing worth telling someone about.</summary>
public sealed record DuplicationShortfall
{
    public required Guid RuleId { get; init; }

    public required string PoolPath { get; init; }

    /// <summary>Copies that exist now.</summary>
    public required int Copies { get; init; }

    public required int WantedCopies { get; init; }

    /// <summary>Drives this rule was allowed to use when the shortfall was found.</summary>
    public int Drives { get; init; }

    public required string Reason { get; init; }
}

/// <summary>How one rule stands: what is protected, what is not, and what would fix it.</summary>
public sealed record DuplicationRuleStatus
{
    public required Guid RuleId { get; init; }

    public required string Folder { get; init; }

    public required bool Enabled { get; init; }

    /// <summary>Copies the rule asks for.</summary>
    public required int RequestedCopies { get; init; }

    /// <summary>
    /// Copies that can exist, which is <see cref="RequestedCopies"/> unless the pool has fewer
    /// drives than that. Fewer means the rule cannot be kept as written.
    /// </summary>
    public required int WantedCopies { get; init; }

    public required int Files { get; init; }

    /// <summary>Files that already have every copy the rule asks for.</summary>
    public required int ProtectedFiles { get; init; }

    public required long Bytes { get; init; }

    /// <summary>Bytes still to be written to bring every file up to its copy count.</summary>
    public required long PendingBytes { get; init; }

    public required int DrivesAvailable { get; init; }

    public IReadOnlyList<DuplicationShortfall> Shortfalls { get; init; } = Array.Empty<DuplicationShortfall>();

    /// <summary>Nothing to copy, nothing short: the folder is as safe as the rule asks for.</summary>
    public bool IsSatisfied => Files == ProtectedFiles && Shortfalls.Count == 0;

    /// <summary>The pool does not have the drives this rule needs, however much room they have.</summary>
    public bool NeedsMoreDrives => WantedCopies < RequestedCopies;
}

/// <summary>Everything the duplicator would do right now, and what it cannot do.</summary>
public sealed record DuplicationPlan
{
    public static DuplicationPlan Empty { get; } = new()
    {
        Actions = Array.Empty<DuplicationAction>(),
        Rules = Array.Empty<DuplicationRuleStatus>(),
    };

    public required IReadOnlyList<DuplicationAction> Actions { get; init; }

    public required IReadOnlyList<DuplicationRuleStatus> Rules { get; init; }

    public long BytesToCopy => Actions
        .Where(static a => a.Kind is not DuplicationActionKind.RemoveCopy)
        .Sum(static a => a.Bytes);

    public int CopiesToAdd => Actions.Count(static a => a.Kind is DuplicationActionKind.AddCopy);

    public int CopiesToRefresh => Actions.Count(static a => a.Kind is DuplicationActionKind.RefreshCopy);

    public int CopiesToRemove => Actions.Count(static a => a.Kind is DuplicationActionKind.RemoveCopy);

    public bool IsEmpty => Actions.Count == 0;

    public IReadOnlyList<DuplicationShortfall> Shortfalls =>
        Rules.SelectMany(static r => r.Shortfalls).ToArray();
}
