namespace MergePool.Core.Volumes;

/// <summary>
/// Enumerates the machine's volumes and maps a stable <see cref="VolumeId"/> back to wherever it is
/// mounted right now. Abstracted so the pool logic is testable without real volumes.
/// </summary>
public interface IVolumeProvider
{
    IReadOnlyList<VolumeInfo> GetVolumes();

    /// <summary>Returns the volume if it is currently present, otherwise <c>null</c> (degraded pool).</summary>
    VolumeInfo? TryGetVolume(VolumeId id);
}
