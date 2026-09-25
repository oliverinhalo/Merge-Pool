using System.Collections.Concurrent;
using MergePool.Core.Config;

namespace MergePool.Core.Placement;

/// <summary>
/// Tracks rolling write throughput and latency per drive and decides when a drive is throttled.
/// </summary>
/// <remarks>
/// Throttling is always judged against the drive's <em>own</em> baseline, never against the other
/// drives: a slow archive disk is not throttled, and a fast SMR disk that has just exhausted its
/// cache is, even while it still beats the archive disk. The baseline is never pulled down while a
/// drive is parked, otherwise it would quietly adapt to the throttled rate and the drive would look
/// healthy again without recovering.
/// </remarks>
public sealed class DriveMetricsTracker(PlacementOptions options, TimeProvider? timeProvider = null)
    : ISpeedFactorSource, IIoObserver
{
    private readonly PlacementOptions _options = options ?? throw new ArgumentNullException(nameof(options));
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly ConcurrentDictionary<Guid, DriveState> _states = new();

    /// <summary>Raised when a drive is parked or comes back, so the UI can show it live.</summary>
    public event EventHandler<DriveMetricsSnapshot>? ThrottleStateChanged;

    public void Record(IoSample sample)
    {
        if (sample.PartId == Guid.Empty || sample.Elapsed <= TimeSpan.Zero)
        {
            return;
        }

        var state = _states.GetOrAdd(sample.PartId, static id => new DriveState(id));
        DriveMetricsSnapshot? changed = null;

        lock (state.Gate)
        {
            state.Apply(sample, _options, _timeProvider.GetUtcNow(), out var throttleChanged);
            if (throttleChanged)
            {
                changed = state.ToSnapshot(_options);
            }
        }

        if (changed is not null)
        {
            ThrottleStateChanged?.Invoke(this, changed);
        }
    }

    public void RecordWrite(Guid partId, long bytes, TimeSpan elapsed) =>
        Record(new IoSample(partId, IoKind.Write, bytes, elapsed));

    public void RecordRead(Guid partId, long bytes, TimeSpan elapsed) =>
        Record(new IoSample(partId, IoKind.Read, bytes, elapsed));

    public void RecordProbe(Guid partId, long bytes, TimeSpan elapsed) =>
        Record(new IoSample(partId, IoKind.Probe, bytes, elapsed));

    /// <summary>
    /// Re-evaluates recovery without a new sample. The cooldown is a wall-clock condition, so a
    /// drive that has gone quiet still needs this to be un-parked.
    /// </summary>
    public void Tick()
    {
        foreach (var state in _states.Values)
        {
            DriveMetricsSnapshot? changed = null;
            lock (state.Gate)
            {
                if (state.TryRecover(_options, _timeProvider.GetUtcNow()))
                {
                    changed = state.ToSnapshot(_options);
                }
            }

            if (changed is not null)
            {
                ThrottleStateChanged?.Invoke(this, changed);
            }
        }
    }

    public DriveMetricsSnapshot GetSnapshot(Guid partId)
    {
        if (!_states.TryGetValue(partId, out var state))
        {
            return new DriveMetricsSnapshot { PartId = partId };
        }

        lock (state.Gate)
        {
            return state.ToSnapshot(_options);
        }
    }

    public IReadOnlyList<DriveMetricsSnapshot> GetAll() =>
        _states.Values.Select(state =>
        {
            lock (state.Gate)
            {
                return state.ToSnapshot(_options);
            }
        }).ToArray();

    public double GetSpeedFactor(Guid partId) => GetSnapshot(partId).SpeedFactor;

    public bool IsThrottled(Guid partId) =>
        _states.TryGetValue(partId, out var state) && Volatile.Read(ref state.ThrottledFlag) != 0;

    /// <summary>Drops everything known about a drive, e.g. after it is removed from the pool.</summary>
    public void Forget(Guid partId) => _states.TryRemove(partId, out _);

    private sealed class DriveState(Guid partId)
    {
        public readonly object Gate = new();

        public int ThrottledFlag;

        private double _throughput;
        private double _baseline;
        private double _latency;
        private double _latencyBaseline;
        private int _consecutiveBad;
        private int _consecutiveGood;
        private long _sampleCount;
        private long _bytes;
        private DateTimeOffset? _throttledSince;
        private DateTimeOffset? _lastSample;
        private ThrottleReason _reason;

        public void Apply(IoSample sample, PlacementOptions options, DateTimeOffset now, out bool throttleChanged)
        {
            throttleChanged = false;
            _lastSample = now;
            _bytes += sample.Bytes;

            var latency = sample.LatencyMilliseconds;
            _latency = Blend(_latency, latency, options.EwmaAlpha);

            // Small operations are dominated by seek and queueing; they say nothing useful about
            // throughput, so they update latency only.
            if (sample.Bytes < options.MinThroughputSampleBytes)
            {
                _latencyBaseline = _latencyBaseline <= 0
                    ? latency
                    : Blend(_latencyBaseline, latency, options.BaselineAlpha);
                return;
            }

            _sampleCount++;
            var throughput = sample.ThroughputBytesPerSecond;
            _throughput = _throughput <= 0 ? throughput : Blend(_throughput, throughput, options.EwmaAlpha);

            if (_baseline <= 0)
            {
                _baseline = throughput;
            }
            else if (throughput > _baseline)
            {
                // A drive that speeds up re-establishes its own normal quickly.
                _baseline = Blend(_baseline, throughput, options.BaselineRiseAlpha);
            }
            else if (ThrottledFlag == 0)
            {
                _baseline = Blend(_baseline, throughput, options.BaselineAlpha);
            }

            _latencyBaseline = _latencyBaseline <= 0
                ? latency
                : ThrottledFlag == 0
                    ? Blend(_latencyBaseline, latency, options.BaselineAlpha)
                    : _latencyBaseline;

            Evaluate(options, now, ref throttleChanged);
        }

        private void Evaluate(PlacementOptions options, DateTimeOffset now, ref bool throttleChanged)
        {
            if (_sampleCount < options.MinSamplesForThrottleDetection || _baseline <= 0)
            {
                return;
            }

            var throughputRatio = _throughput / _baseline;
            var latencyRatio = _latencyBaseline > 0 ? _latency / _latencyBaseline : 1.0;

            var throughputBad = throughputRatio < options.ThrottleEnterRatio;
            var latencyBad = latencyRatio > options.LatencyThrottleRatio;

            if (throughputBad || latencyBad)
            {
                _consecutiveGood = 0;
                _consecutiveBad++;

                if (ThrottledFlag == 0 && _consecutiveBad >= options.ThrottleEnterSamples)
                {
                    Volatile.Write(ref ThrottledFlag, 1);
                    _throttledSince = now;
                    _reason = throughputBad ? ThrottleReason.ThroughputCollapse : ThrottleReason.LatencySpike;
                    throttleChanged = true;
                }

                return;
            }

            // Hysteresis: recovering needs a clearly better ratio than the one that parked us.
            if (throughputRatio < options.ThrottleExitRatio)
            {
                _consecutiveGood = 0;
                return;
            }

            _consecutiveBad = 0;
            _consecutiveGood++;

            if (ThrottledFlag != 0 && CanLeaveCooldown(options, now) && _consecutiveGood >= options.ThrottleExitSamples)
            {
                Volatile.Write(ref ThrottledFlag, 0);
                _throttledSince = null;
                _reason = ThrottleReason.None;
                throttleChanged = true;
            }
        }

        /// <summary>Clears the throttle when the cooldown has passed and the drive already looks healthy.</summary>
        public bool TryRecover(PlacementOptions options, DateTimeOffset now)
        {
            if (ThrottledFlag == 0
                || !CanLeaveCooldown(options, now)
                || _consecutiveGood < options.ThrottleExitSamples)
            {
                return false;
            }

            Volatile.Write(ref ThrottledFlag, 0);
            _throttledSince = null;
            _reason = ThrottleReason.None;
            return true;
        }

        private bool CanLeaveCooldown(PlacementOptions options, DateTimeOffset now) =>
            _throttledSince is not { } since
            || (now - since).TotalSeconds >= options.ThrottleCooldownSeconds;

        public DriveMetricsSnapshot ToSnapshot(PlacementOptions options) => new()
        {
            PartId = partId,
            ThroughputBytesPerSecond = _throughput,
            BaselineBytesPerSecond = _baseline,
            LatencyMilliseconds = _latency,
            BaselineLatencyMilliseconds = _latencyBaseline,
            SpeedFactor = SpeedFactor(options),
            IsThrottled = ThrottledFlag != 0,
            ThrottleReason = _reason,
            ThrottledSince = _throttledSince,
            LastSampleUtc = _lastSample,
            SampleCount = _sampleCount,
            BytesObserved = _bytes,
        };

        private double SpeedFactor(PlacementOptions options)
        {
            if (_throughput <= 0 || options.ReferenceThroughputBytesPerSecond <= 0)
            {
                return 1.0;
            }

            var factor = _throughput / options.ReferenceThroughputBytesPerSecond;
            return Math.Clamp(factor, options.MinSpeedFactor, options.MaxSpeedFactor);
        }

        private static double Blend(double current, double sample, double alpha)
        {
            var a = Math.Clamp(alpha, 0.0001, 1.0);
            return (current * (1 - a)) + (sample * a);
        }
    }
}
