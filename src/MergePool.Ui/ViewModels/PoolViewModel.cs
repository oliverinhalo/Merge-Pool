using System.Collections.ObjectModel;
using System.Globalization;
using MergePool.Ipc.Protocol;

namespace MergePool.Ui.ViewModels;

/// <summary>One drive inside a pool, with its live status.</summary>
public sealed class PoolPartViewModel(PoolPartDto dto) : ViewModelBase
{
    private PoolPartDto _dto = dto;

    public Guid PartId => _dto.PartId;

    public string VolumeId => _dto.VolumeId;

    public string Label => string.IsNullOrWhiteSpace(_dto.Label) ? "(no label)" : _dto.Label;

    public string DriveLetter => string.IsNullOrEmpty(_dto.DriveLetter) ? "—" : _dto.DriveLetter + ":";

    public long TotalBytes => _dto.TotalBytes;

    public long FreeBytes => _dto.FreeBytes;

    /// <summary>What the pool holds here, which is not the same as what the drive holds.</summary>
    public long PoolBytes => _dto.PoolBytes ?? 0;

    public bool IsMeasured => _dto.PoolBytes is not null;

    public long DriveUsedBytes => Math.Max(0, _dto.TotalBytes - _dto.FreeBytes);

    /// <summary>Everything on this drive that is not in the pool.</summary>
    public long ForeignBytes => Math.Max(0, DriveUsedBytes - PoolBytes);

    /// <summary>How full the drive is, pooled or not. This is the drive's own story.</summary>
    public double DriveUsedFraction => _dto.TotalBytes <= 0 ? 0 : DriveUsedBytes / (double)_dto.TotalBytes;

    /// <summary>The pool's share of the drive, as a fraction of the whole drive.</summary>
    public double PoolFraction => _dto.TotalBytes <= 0 ? 0 : PoolBytes / (double)_dto.TotalBytes;

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

    /// <summary>Spells out the split, since "used" on a pooled drive means two different things.</summary>
    public string UsageSummary => !IsMeasured
        ? "Measuring what the pool holds here…"
        : string.Create(
            CultureInfo.CurrentCulture,
            $"{Format(PoolBytes)} pooled · {Format(ForeignBytes)} other files · {Format(FreeBytes)} free");

    public void Update(PoolPartDto dto)
    {
        _dto = dto;

        Raise(nameof(Label));
        Raise(nameof(DriveLetter));
        Raise(nameof(TotalBytes));
        Raise(nameof(FreeBytes));
        Raise(nameof(PoolBytes));
        Raise(nameof(IsMeasured));
        Raise(nameof(DriveUsedBytes));
        Raise(nameof(ForeignBytes));
        Raise(nameof(DriveUsedFraction));
        Raise(nameof(PoolFraction));
        Raise(nameof(IsOnline));
        Raise(nameof(IsThrottled));
        Raise(nameof(Status));
        Raise(nameof(Throughput));
        Raise(nameof(HealthRatio));
        Raise(nameof(Latency));
        Raise(nameof(UsageSummary));
    }

    internal static string Format(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB", "PB"];
        if (bytes <= 0)
        {
            return "0 B";
        }

        double size = bytes;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return string.Create(CultureInfo.CurrentCulture, $"{size:N1} {units[unit]}");
    }
}

/// <summary>A pool as the window shows it.</summary>
public sealed class PoolViewModel(PoolDto dto) : ViewModelBase
{
    private PoolDto _dto = dto;

    public Guid PoolId => _dto.PoolId;

    public string Name => _dto.Name;

    public string MountPoint => string.IsNullOrWhiteSpace(_dto.MountPoint) ? "not mounted" : _dto.MountPoint;

    public bool IsMounted => _dto.IsMounted;

    public string Health => _dto.Health;

    /// <summary>The drives' combined size, including everything on them that is not pooled.</summary>
    public long TotalBytes => _dto.TotalBytes;

    public long FreeBytes => _dto.FreeBytes;

    /// <summary>What the pool itself holds.</summary>
    public long PoolUsedBytes => _dto.PoolUsedBytes;

    /// <summary>What the pool can hold: its own content plus free space on its drives.</summary>
    public long PoolCapacityBytes => _dto.PoolCapacityBytes > 0 ? _dto.PoolCapacityBytes : _dto.TotalBytes;

    public long ForeignBytes => _dto.ForeignBytes;

    public int PoolFileCount => _dto.PoolFileCount;

    public bool IsMeasured => _dto.IsUsageMeasured;

    /// <summary>
    /// How full the pool is against its own ceiling — not against the drives' raw size, which would
    /// count space that other files have already taken and the pool can never use.
    /// </summary>
    public double PoolUsedFraction =>
        PoolCapacityBytes <= 0 ? 0 : Math.Clamp(PoolUsedBytes / (double)PoolCapacityBytes, 0, 1);

    /// <summary>How full the pool's drives are overall, pooled content and everything else.</summary>
    public double DriveUsedFraction =>
        TotalBytes <= 0 ? 0 : Math.Clamp((TotalBytes - FreeBytes) / (double)TotalBytes, 0, 1);

    public string PoolUsageText => IsMeasured
        ? string.Create(
            CultureInfo.CurrentCulture,
            $"{PoolPartViewModel.Format(PoolUsedBytes)} of {PoolPartViewModel.Format(PoolCapacityBytes)} used by this pool")
        : "Measuring what the pool holds…";

    public string DriveUsageText => string.Create(
        CultureInfo.CurrentCulture,
        $"{PoolPartViewModel.Format(TotalBytes - FreeBytes)} of {PoolPartViewModel.Format(TotalBytes)} used on these drives in total");

    public string FileCountText => IsMeasured
        ? string.Create(CultureInfo.CurrentCulture, $"{PoolFileCount:N0} file(s) in the pool")
        : "—";

    public string HealthSummary
    {
        get
        {
            var missing = _dto.Parts.Count(part => part.State == "Offline");
            var throttled = _dto.Parts.Count(part => part.IsThrottled);

            if (missing > 0)
            {
                return string.Create(
                    CultureInfo.CurrentCulture,
                    $"Degraded — {missing} of {_dto.Parts.Count} drives missing. The rest keep serving, and a drive rejoins on its own.");
            }

            if (throttled > 0)
            {
                return string.Create(
                    CultureInfo.CurrentCulture,
                    $"Healthy — {throttled} drive(s) writing slower than usual, so new files are going elsewhere for now.");
            }

            return _dto.IsMounted
                ? string.Create(CultureInfo.CurrentCulture, $"Healthy — {_dto.Parts.Count} drive(s), mounted at {MountPoint}.")
                : "Healthy, but not mounted.";
        }
    }

    public ObservableCollection<PoolPartViewModel> Parts { get; } =
        [.. dto.Parts.Select(part => new PoolPartViewModel(part))];

    public void Update(PoolDto dto)
    {
        _dto = dto;

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
            if (dto.Parts.All(part => part.PartId != Parts[i].PartId))
            {
                Parts.RemoveAt(i);
            }
        }

        Raise(nameof(Name));
        Raise(nameof(MountPoint));
        Raise(nameof(IsMounted));
        Raise(nameof(Health));
        Raise(nameof(TotalBytes));
        Raise(nameof(FreeBytes));
        Raise(nameof(PoolUsedBytes));
        Raise(nameof(PoolCapacityBytes));
        Raise(nameof(ForeignBytes));
        Raise(nameof(PoolFileCount));
        Raise(nameof(IsMeasured));
        Raise(nameof(PoolUsedFraction));
        Raise(nameof(DriveUsedFraction));
        Raise(nameof(PoolUsageText));
        Raise(nameof(DriveUsageText));
        Raise(nameof(FileCountText));
        Raise(nameof(HealthSummary));
    }
}
