namespace MergePool.Core.Sharing;

/// <summary>What a pool's share is actually doing, for the window and the web interface.</summary>
public sealed record PoolShareStatus
{
    public required Guid PoolId { get; init; }

    public required bool Enabled { get; init; }

    /// <summary>The share's UNC path once it exists, e.g. <c>\\DESKTOP-ABC\Media</c>.</summary>
    public string? UncPath { get; init; }

    public bool IsShared { get; init; }

    /// <summary>Why it is not shared, when it should be.</summary>
    public string? Problem { get; init; }

    /// <summary>
    /// Whether Windows' File and Printer Sharing rules are on. Without them the share exists and
    /// nothing on the network can reach it, which is a confusing way to fail.
    /// </summary>
    public bool? FileSharingEnabled { get; init; }
}
