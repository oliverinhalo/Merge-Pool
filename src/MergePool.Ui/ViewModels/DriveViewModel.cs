using MergePool.Ipc.Protocol;

namespace MergePool.Ui.ViewModels;

/// <summary>A drive as shown in the "pick drives" list.</summary>
public sealed class DriveViewModel : ViewModelBase
{
    private bool _isSelected;
    private DriveDto _dto;

    public DriveViewModel(DriveDto dto)
    {
        _dto = dto;
        VolumeId = dto.VolumeId;
    }

    public string VolumeId { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }

    public string DriveLetter => string.IsNullOrEmpty(_dto.DriveLetter) ? "—" : _dto.DriveLetter + ":";

    public string Label => string.IsNullOrWhiteSpace(_dto.Label) ? "(no label)" : _dto.Label;

    public string FileSystem => _dto.FileSystem;

    public string Kind => _dto.Kind;

    public long TotalBytes => _dto.TotalBytes;

    public long FreeBytes => _dto.FreeBytes;

    public double UsedFraction => _dto.TotalBytes <= 0 ? 0 : 1 - (_dto.FreeBytes / (double)_dto.TotalBytes);

    /// <summary>A drive already in a pool cannot be added to another one.</summary>
    public bool CanBePooled => _dto.IsPoolable && _dto.PoolId is null;

    public string Status =>
        !_dto.IsReady ? "Not ready"
        : _dto.PoolId is not null ? "Already pooled"
        : !_dto.IsPoolable ? "Cannot be pooled"
        : "Available";

    public void Update(DriveDto dto)
    {
        _dto = dto;

        Raise(nameof(DriveLetter));
        Raise(nameof(Label));
        Raise(nameof(FileSystem));
        Raise(nameof(Kind));
        Raise(nameof(TotalBytes));
        Raise(nameof(FreeBytes));
        Raise(nameof(UsedFraction));
        Raise(nameof(CanBePooled));
        Raise(nameof(Status));

        if (!CanBePooled)
        {
            IsSelected = false;
        }
    }
}
