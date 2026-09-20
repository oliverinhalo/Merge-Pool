using System.Collections.ObjectModel;
using System.Globalization;
using MergePool.Ipc.Protocol;

namespace MergePool.Ui.ViewModels;

/// <summary>One drive inside a pool, with its live status.</summary>
public sealed class PoolPartViewModel(PoolPartDto dto) : ViewModelBase
{
    private PoolPartDto _dto = dto;

    public Guid PartId => _dto.PartId;

    public string Label => string.IsNullOrWhiteSpace(_dto.Label) ? "(no label)" : _dto.Label;

    public string DriveLetter => string.IsNullOrEmpty(_dto.DriveLetter) ? "—" : _dto.DriveLetter + ":";

    public long TotalBytes => _dto.TotalBytes;

    public long FreeBytes => _dto.FreeBytes;

    public double UsedFraction => _dto.TotalBytes <= 0 ? 0 : 1 - (_dto.FreeBytes / (double)_dto.TotalBytes);

    public bool IsOnline => _dto.State != "Offline";

    public bool IsThrottled => _dto.IsThrottled;

    /// <summary>What the user sees at a glance: missing, throttled, or fine.</summary>
    public string Status => _dto.State switch
    {
        "Offline" => "Missing",
        "ReadOnly" => "Read-only",
        _ when _dto.IsThrottled => _dto.ThrottleReason switch
        {
            "LatencySpike" => "Throttled (latency)",
            _ => "Throttled (slow writes)",
        },
        _ => "Online",
    };

    public string Throughput => _dto.ThroughputBytesPerSecond <= 0
        ? "—"
        : string.Create(CultureInfo.CurrentCulture, $"{_dto.ThroughputBytesPerSecond / (1024 * 1024):N1} MB/s");

    /// <summary>Speed against this drive's own baseline, which is what throttling is judged on.</summary>
    public string HealthRatio => _dto.BaselineBytesPerSecond <= 0
        ? "—"
        : string.Create(
            CultureInfo.CurrentCulture,
            $"{_dto.ThroughputBytesPerSecond / _dto.BaselineBytesPerSecond:P0} of its normal");

    public string Latency => _dto.LatencyMilliseconds <= 0
        ? "—"
        : string.Create(CultureInfo.CurrentCulture, $"{_dto.LatencyMilliseconds:N1} ms");

    public void Update(PoolPartDto dto)
    {
        _dto = dto;

        Raise(nameof(Label));
        Raise(nameof(DriveLetter));
        Raise(nameof(TotalBytes));
        Raise(nameof(FreeBytes));
        Raise(nameof(UsedFraction));
        Raise(nameof(IsOnline));
        Raise(nameof(IsThrottled));
        Raise(nameof(Status));
        Raise(nameof(Throughput));
        Raise(nameof(HealthRatio));
        Raise(nameof(Latency));
    }
}

public sealed class PoolViewModel : ViewModelBase
{
    private PoolDto _dto;

    public PoolViewModel(PoolDto dto)
    {
        _dto = dto;
        PoolId = dto.PoolId;
        Update(dto);
    }

    public Guid PoolId { get; }

    public ObservableCollection<PoolPartViewModel> Parts { get; } = [];

    public string Name => _dto.Name;

    public string MountPoint => string.IsNullOrEmpty(_dto.MountPoint) ? "(not mounted)" : _dto.MountPoint;

    public bool IsMounted => _dto.IsMounted;

    public long TotalBytes => _dto.TotalBytes;

    public long FreeBytes => _dto.FreeBytes;

    public double UsedFraction => _dto.TotalBytes <= 0 ? 0 : 1 - (_dto.FreeBytes / (double)_dto.TotalBytes);

    public string Health => _dto.Health;

    /// <summary>A degraded pool still works; the wording has to say so rather than alarm.</summary>
    public string HealthSummary => _dto.Health switch
    {
        "Healthy" => "All drives present",
        "Degraded" => string.Create(
            CultureInfo.CurrentCulture,
            $"{_dto.Parts.Count(p => p.State == "Offline")} of {_dto.Parts.Count} drives missing — the rest keep serving"),
        _ => "No drives available",
    };

    public bool IsDegraded => _dto.Health == "Degraded";

    public bool IsOffline => _dto.Health == "Offline";

    public void Update(PoolDto dto)
    {
        _dto = dto;

        // Merge in place so the list does not flicker on every poll.
        foreach (var part in dto.Parts)
        {
            var existing = Parts.FirstOrDefault(p => p.PartId == part.PartId);
            if (existing is null)
            {
                Parts.Add(new PoolPartViewModel(part));
            }
            else
            {
                existing.Update(part);
            }
        }

        for (var i = Parts.Count - 1; i >= 0; i--)
        {
            if (dto.Parts.All(p => p.PartId != Parts[i].PartId))
            {
                Parts.RemoveAt(i);
            }
        }

        Raise(nameof(Name));
        Raise(nameof(MountPoint));
        Raise(nameof(IsMounted));
        Raise(nameof(TotalBytes));
        Raise(nameof(FreeBytes));
        Raise(nameof(UsedFraction));
        Raise(nameof(Health));
        Raise(nameof(HealthSummary));
        Raise(nameof(IsDegraded));
        Raise(nameof(IsOffline));
    }
}
