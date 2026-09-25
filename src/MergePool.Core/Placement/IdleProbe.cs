using System.Diagnostics;
using MergePool.Core.Config;
using MergePool.Core.Model;
using MergePool.Core.Paths;

namespace MergePool.Core.Placement;

/// <summary>
/// Keeps drive metrics fresh while nothing is writing. Without it a drive that went quiet while
/// throttled would never be measured again, so it could never be shown as recovered.
/// </summary>
/// <remarks>
/// The probe is deliberately tiny and lands inside the pool part, so it costs an SMR drive almost
/// nothing and never touches anything outside MergePool's own folder.
/// </remarks>
public sealed class IdleProbe(
    DriveMetricsTracker tracker,
    PlacementOptions options,
    TimeProvider? timeProvider = null)
{
    private const string ProbeFileName = ".mergepool-probe.tmp";

    private readonly DriveMetricsTracker _tracker = tracker ?? throw new ArgumentNullException(nameof(tracker));
    private readonly PlacementOptions _options = options ?? throw new ArgumentNullException(nameof(options));
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    /// <summary>Probes every online drive that has been idle longer than the configured interval.</summary>
    public int ProbeIdleDrives(PoolSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var now = _timeProvider.GetUtcNow();
        var probed = 0;

        foreach (var part in snapshot.OnlineParts)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var metrics = _tracker.GetSnapshot(part.PartId);
            if (metrics.LastSampleUtc is { } last
                && (now - last).TotalSeconds < _options.IdleProbeIntervalSeconds)
            {
                continue;
            }

            if (Probe(part, cancellationToken))
            {
                probed++;
            }
        }

        return probed;
    }

    /// <summary>Measures one drive. Returns false when the drive could not be probed.</summary>
    public bool Probe(PoolPart part, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(part);

        if (!part.IsOnline)
        {
            return false;
        }

        var path = PoolPath.ToHostPath(part.RequireRootPath(), ProbeFileName);
        var bytes = Math.Max(4096, _options.IdleProbeBytes);
        var payload = new byte[bytes];

        try
        {
            var start = Stopwatch.GetTimestamp();

            using (var stream = new FileStream(
                       path,
                       FileMode.Create,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 0x10000,
                       FileOptions.WriteThrough | FileOptions.SequentialScan))
            {
                stream.Write(payload, 0, payload.Length);
                stream.Flush(flushToDisk: true);
            }

            var elapsed = Stopwatch.GetElapsedTime(start);
            _tracker.RecordProbe(part.PartId, bytes, elapsed);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A drive that cannot even take the probe is reported through pool health, not here.
            return false;
        }
        finally
        {
            TryDelete(path);
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
