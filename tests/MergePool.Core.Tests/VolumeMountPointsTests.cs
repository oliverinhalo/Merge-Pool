using MergePool.Core.Volumes;
using Xunit;

namespace MergePool.Core.Tests;

/// <summary>
/// Covers the parsing of what Windows writes into the mount-point buffer. The P/Invoke itself needs
/// Windows, but this is where the interpretation happens, and it is where getting it wrong made the
/// whole drive list disappear.
/// </summary>
public sealed class VolumeMountPointsTests
{
    [Fact]
    public void A_volume_with_no_mount_point_has_no_root_path()
    {
        // Windows writes just the terminator for the EFI system partition, the recovery partition
        // and anything else without a mount point. Reading that back as the one-character string
        // "\0" makes it look like a path, and every later attempt to use it throws.
        Assert.Null(VolumeMountPoints.First("\0"));
        Assert.Null(VolumeMountPoints.First("\0\0"));
        Assert.Null(VolumeMountPoints.First(ReadOnlySpan<char>.Empty));
    }

    [Fact]
    public void A_lettered_drive_comes_back_without_its_terminator()
    {
        Assert.Equal("C:\\", VolumeMountPoints.First("C:\\\0\0"));
    }

    [Fact]
    public void The_first_of_several_mount_points_wins()
    {
        // A volume can be mounted in more than one place; the list is double-null terminated.
        Assert.Equal("D:\\", VolumeMountPoints.First("D:\\\0C:\\Mount\\Data\\\0\0"));
    }

    [Fact]
    public void A_volume_mounted_as_a_folder_keeps_its_full_path()
    {
        Assert.Equal(
            "C:\\Mount\\Archive\\",
            VolumeMountPoints.First("C:\\Mount\\Archive\\\0\0"));
    }

    [Fact]
    public void A_blank_entry_is_treated_as_no_mount_point()
    {
        Assert.Null(VolumeMountPoints.First("   \0\0"));
    }
}
