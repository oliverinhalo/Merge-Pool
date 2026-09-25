using MergePool.Core.Config;
using MergePool.Core.Model;
using MergePool.Core.Placement;
using MergePool.Core.Union;
using MergePool.Core.Volumes;

namespace MergePool.Core.Tests;

/// <summary>
/// A throwaway pool backed by temp folders standing in for drives. Everything the pool logic does
/// goes through the same code paths as a real install.
/// </summary>
public sealed class TempPool : IDisposable, IPoolTopology
{
    private readonly string _root;
    private readonly DirectoryVolumeProvider _volumes = new();
    private readonly PoolPartManager _manager;
    private readonly List<PoolDriveDefinition> _drives = [];
    private readonly PoolDefinition _definition;

    public TempPool(PlacementOptions? placementOptions = null)
    {
        _root = Path.Combine(Path.GetTempPath(), "mergepool-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _manager = new PoolPartManager(_volumes);
        _definition = new PoolDefinition { Name = "TestPool", MountPoint = "P:" };

        Options = placementOptions ?? new PlacementOptions
        {
            MinFreeBytes = 0,
            WriteHeadroomBytes = 0,
        };

        Placement = new FreeSpaceSpeedPlacement(Options);
        Operations = new PoolOperations(this, Placement);
    }

    public string Root => _root;

    public PlacementOptions Options { get; }

    public IPlacementStrategy Placement { get; }

    public PoolOperations Operations { get; }

    public UnionView View => Operations.View;

    public PoolDefinition Definition => _definition;

    public DirectoryVolumeProvider Volumes => _volumes;

    public PoolPartManager Manager => _manager;

    public PoolSnapshot Current => _manager.Resolve(_definition);

    /// <summary>Adds a fake drive with a pool part on it and returns (volumeId, partId).</summary>
    public (VolumeId Volume, Guid Part) AddDrive(
        string name,
        long totalBytes = 1_000_000_000,
        long freeBytes = 500_000_000,
        double speedFactor = 1.0)
    {
        var path = Path.Combine(_root, name);
        var volumeId = _volumes.Add(path, label: name, totalBytes: totalBytes, freeBytes: freeBytes);
        var volume = _volumes.TryGetVolume(volumeId)!;
        var part = _manager.CreatePart(volume, _definition.Id, _definition.Name);

        var drive = new PoolDriveDefinition
        {
            VolumeId = volumeId.ToVolumePath(),
            PartId = part.PartId,
            Label = name,
            SpeedFactorOverride = speedFactor,
        };

        _drives.Add(drive);
        _definition.Drives.Add(drive);
        return (volumeId, part.PartId);
    }

    public string PartRoot(Guid partId) =>
        Current.FindPart(partId)?.RootPath ?? throw new InvalidOperationException("Part is offline.");

    /// <summary>Writes a file directly into a part, bypassing placement (to set up scenarios).</summary>
    public string WriteInto(Guid partId, string poolPath, string content, DateTime? lastWriteUtc = null)
    {
        var host = Paths.PoolPath.ToHostPath(PartRoot(partId), poolPath);
        Directory.CreateDirectory(Path.GetDirectoryName(host)!);
        File.WriteAllText(host, content);
        if (lastWriteUtc is { } stamp)
        {
            File.SetLastWriteTimeUtc(host, stamp);
        }

        return host;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best effort: a leaked temp folder must not fail a test run.
        }
    }
}
