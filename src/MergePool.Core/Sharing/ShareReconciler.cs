using System.Globalization;

namespace MergePool.Core.Sharing;

/// <summary>What a pool's share should look like right now.</summary>
public sealed record DesiredShare
{
    public required Guid PoolId { get; init; }

    public required string PoolName { get; init; }

    public required string ShareName { get; init; }

    /// <summary>The mount point to share, e.g. <c>P:\</c>.</summary>
    public required string Path { get; init; }

    public required bool ReadOnly { get; init; }

    public required IReadOnlyList<string> AllowedAccounts { get; init; }
}

/// <summary>A share that exists on the machine, as the OS reports it.</summary>
public sealed record ExistingShare
{
    public required string ShareName { get; init; }

    public required string Path { get; init; }
}

/// <summary>Something to do to the machine's shares to make it match the configuration.</summary>
public sealed record ShareAction
{
    public required ShareActionKind Kind { get; init; }

    public required string ShareName { get; init; }

    /// <summary>Present for <see cref="ShareActionKind.Create"/>.</summary>
    public DesiredShare? Share { get; init; }

    /// <summary>Why, for the log.</summary>
    public required string Reason { get; init; }
}

public enum ShareActionKind
{
    Create,
    Remove,
}

/// <summary>
/// Works out which SMB shares MergePool should create and remove, without touching the machine.
/// </summary>
/// <remarks>
/// <para>Separated from the code that runs <c>New-SmbShare</c> so the decisions can be tested on
/// any host. The decisions are the part with the bugs in: a share left behind pointing at an
/// unmounted pool, a share not recreated after a remount, a share someone else owns being removed
/// because its name happened to match.</para>
/// <para>The rule that keeps this safe: MergePool only ever removes a share whose path is one of
/// its own mount points, or one it is tracking by name because it created it. A share pointing
/// somewhere else with a name we would have chosen is left alone and reported.</para>
/// </remarks>
public static class ShareReconciler
{
    /// <summary>
    /// The difference between what should be shared and what is. Both lists are small — one entry
    /// per pool — so this is a plain comparison rather than anything clever.
    /// </summary>
    /// <param name="desired">Shares that should exist: mounted pools with sharing turned on.</param>
    /// <param name="existing">Shares the machine currently has.</param>
    /// <param name="ours">
    /// Share names MergePool has created in this session, so a share that should no longer exist is
    /// removed — but only if we were the one that made it.
    /// </param>
    /// <param name="ourMountPoints">
    /// Every mount point MergePool serves, mounted or not. A share pointing at one of these is ours
    /// to manage even if we did not create it in this session — which is how a share survives a
    /// service restart without being orphaned.
    /// </param>
    public static IReadOnlyList<ShareAction> Plan(
        IReadOnlyList<DesiredShare> desired,
        IReadOnlyList<ExistingShare> existing,
        IReadOnlySet<string> ours,
        IReadOnlySet<string> ourMountPoints)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(ours);
        ArgumentNullException.ThrowIfNull(ourMountPoints);

        var actions = new List<ShareAction>();
        var byName = existing.ToDictionary(share => share.ShareName, StringComparer.OrdinalIgnoreCase);

        foreach (var want in desired)
        {
            if (!byName.TryGetValue(want.ShareName, out var already))
            {
                actions.Add(new ShareAction
                {
                    Kind = ShareActionKind.Create,
                    ShareName = want.ShareName,
                    Share = want,
                    Reason = string.Create(
                        CultureInfo.InvariantCulture,
                        $"pool '{want.PoolName}' is mounted at {want.Path} and set to be shared"),
                });

                continue;
            }

            if (SamePath(already.Path, want.Path))
            {
                // Already shared from the right place. Access rights are re-applied by the caller
                // rather than compared here: reading them back is a separate round trip, and
                // granting an account that already has the grant is harmless.
                continue;
            }

            // A share of this name exists but points somewhere else. If it is one of ours — by name
            // or because it points at another of our mounts — it is stale, from before a drive
            // letter changed. If it is not, it belongs to someone else and we leave it alone.
            if (ours.Contains(already.ShareName) || IsOurs(already.Path, ourMountPoints))
            {
                actions.Add(new ShareAction
                {
                    Kind = ShareActionKind.Remove,
                    ShareName = already.ShareName,
                    Reason = string.Create(
                        CultureInfo.InvariantCulture,
                        $"it points at {already.Path} rather than {want.Path}"),
                });

                actions.Add(new ShareAction
                {
                    Kind = ShareActionKind.Create,
                    ShareName = want.ShareName,
                    Share = want,
                    Reason = string.Create(
                        CultureInfo.InvariantCulture,
                        $"pool '{want.PoolName}' moved to {want.Path}"),
                });
            }
        }

        var wanted = desired.Select(share => share.ShareName).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var share in existing)
        {
            if (wanted.Contains(share.ShareName))
            {
                continue;
            }

            // Nothing wants this share any more. Remove it only if it is ours: either we made it,
            // or it points at one of our mount points, which is what makes a share disappear when
            // its pool is unmounted or sharing is turned off.
            if (ours.Contains(share.ShareName) || IsOurs(share.Path, ourMountPoints))
            {
                actions.Add(new ShareAction
                {
                    Kind = ShareActionKind.Remove,
                    ShareName = share.ShareName,
                    Reason = "its pool is no longer mounted, or is no longer set to be shared",
                });
            }
        }

        return actions;
    }

    /// <summary>
    /// Whether a share's path is one of ours. Paths are compared the way the SMB server reports
    /// them against the way the configuration spells them, so <c>P:</c> and <c>P:\</c> match.
    /// </summary>
    private static bool IsOurs(string path, IReadOnlySet<string> ourMountPoints) =>
        ourMountPoints.Any(mount => SamePath(mount, path));

    private static bool SamePath(string left, string right) =>
        string.Equals(Normalise(left), Normalise(right), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// One canonical spelling of a mount point. Not <see cref="Path"/>-based: these are Windows
    /// paths whichever host is running the code, so the comparison must not change with the
    /// platform's separator.
    /// </summary>
    public static string Normalise(string? path) =>
        string.IsNullOrWhiteSpace(path) ? string.Empty : path.Trim().TrimEnd('\\', '/') + '\\';
}
