using MergePool.Core.Config;
using MergePool.Core.FileSystem;
using MergePool.Core.Model;
using MergePool.Core.Placement;
using MergePool.Core.Volumes;

namespace MergePool.Integration.Tests;

/// <summary>
/// A pool built on fake drives. Each drive is a temp folder, or a mounted VHDX when the test run
/// has the rights to create one (see <see cref="VhdxDrive"/>), and the pool code cannot tell the
/// difference: it only ever sees a volume identity and a root path.
/// </summary>
public sealed class FakeDriveSet : IDisposable, IPoolTopology
{
    private readonly string _root;
    private readonly DirectoryVolumeProvider _volumes = new();
    private readonly PoolPartManager _manager;
    private readonly PoolDefinition _definition;
    private readonly List<IDisposable> _backing = [];

    public FakeDriveSet(string name = "pool")
    {
        _root = Path.Combine(Path.GetTempPath(), "mergepool-integration", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);

        _manager = new PoolPartManager(_volumes);
        _definition = new PoolDefinition { Name = name, MountPoint = "P:" };

        Options = new PlacementOptions { MinFreeBytes = 0, WriteHeadroomBytes = 0 };
        Placement = new FreeSpaceSpeedPlacement(Options);
        Engine = new PoolFileSystemEngine(
            this,
            Placement,
            new FileRelocator(),
            new PoolFileSystemOptions { RelocationReserveBytes = 0 });
    }

    public string Root => _root;

    public PlacementOptions Options { get; }

    public IPlacementStrategy Placement { get; }

    public PoolFileSystemEngine Engine { get; }

    public PoolDefinition Definition => _definition;

    public DirectoryVolumeProvider Volumes => _volumes;

    public PoolSnapshot Current => _manager.Resolve(_definition);

    public (VolumeId Volume, Guid Part) AddDrive(
        string name,
        long totalBytes = 10_000_000,
        long freeBytes = 10_000_000,
        double speedFactor = 1.0)
    {
        var path = Path.Combine(_root, name);
        var volumeId = _volumes.Add(path, label: name, totalBytes: totalBytes, freeBytes: freeBytes);
        var part = _manager.CreatePart(_volumes.TryGetVolume(volumeId)!, _definition.Id, _definition.Name);

        _definition.Drives.Add(new PoolDriveDefinition
        {
            VolumeId = volumeId.ToVolumePath(),
            PartId = part.PartId,
            Label = name,
            SpeedFactorOverride = speedFactor,
        });

        return (volumeId, part.PartId);
    }

    /// <summary>The host path of a drive's root, i.e. what a user sees with MergePool uninstalled.</summary>
    public string DriveRoot(string name) => Path.Combine(_root, name);

    public string PartRoot(Guid partId) =>
        Current.FindPart(partId)?.RootPath ?? throw new InvalidOperationException("Part is offline.");

    internal void Track(IDisposable disposable) => _backing.Add(disposable);

    public void Dispose()
    {
        foreach (var disposable in _backing)
        {
            disposable.Dispose();
        }

        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }
}
