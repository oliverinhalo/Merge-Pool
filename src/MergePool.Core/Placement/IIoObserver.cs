namespace MergePool.Core.Placement;

/// <summary>
/// Receives completed IO operations. Implemented by <see cref="DriveMetricsTracker"/>; the engine
/// takes the interface so metrics stay optional and the file system stays testable on its own.
/// </summary>
public interface IIoObserver
{
    void Record(IoSample sample);
}
