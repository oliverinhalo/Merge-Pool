using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace MergePool.Core.Volumes;

/// <summary>
/// Enumerates real Windows volumes by volume GUID. Drive letters are read for display only.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsVolumeProvider : IVolumeProvider
{
    public IReadOnlyList<VolumeInfo> GetVolumes()
    {
        var volumes = new List<VolumeInfo>();
        var buffer = new StringBuilder(260);

        var handle = NativeMethods.FindFirstVolumeW(buffer, buffer.Capacity);
        if (handle == NativeMethods.InvalidHandle)
        {
            throw new IOException(
                $"Windows would not start a volume enumeration (error {Marshal.GetLastWin32Error()}).");
        }

        try
        {
            do
            {
                var volumeName = buffer.ToString();

                // A machine always has volumes we cannot describe — an unformatted partition, a
                // BitLocker volume that is still locked, a card reader with no card. Skipping the
                // one that fails keeps every other drive visible.
                try
                {
                    if (VolumeId.TryParse(volumeName, out var id))
                    {
                        volumes.Add(Describe(id, volumeName));
                    }
                }
                catch (Exception exception) when (exception is IOException
                    or UnauthorizedAccessException
                    or ArgumentException
                    or NotSupportedException)
                {
                }

                buffer.Clear();
                buffer.EnsureCapacity(260);
            }
            while (NativeMethods.FindNextVolumeW(handle, buffer, buffer.Capacity));
        }
        finally
        {
            NativeMethods.FindVolumeClose(handle);
        }

        return volumes;
    }

    public VolumeInfo? TryGetVolume(VolumeId id) => GetVolumes().FirstOrDefault(v => v.Id == id);

    private static VolumeInfo Describe(VolumeId id, string volumeName)
    {
        var root = GetFirstMountPoint(volumeName);
        var kind = VolumeKind.Unknown;
        var label = string.Empty;
        var fileSystem = string.Empty;
        long total = 0;
        long free = 0;
        var ready = false;
        char? letter = null;

        if (root is not null)
        {
            kind = MapDriveType(NativeMethods.GetDriveTypeW(root));
            if (root.Length >= 2 && root[1] == ':' && char.IsLetter(root[0]))
            {
                letter = char.ToUpperInvariant(root[0]);
            }

            try
            {
                var drive = new DriveInfo(root);
                ready = drive.IsReady;
                if (ready)
                {
                    label = drive.VolumeLabel;
                    fileSystem = drive.DriveFormat;
                    total = drive.TotalSize;
                    free = drive.AvailableFreeSpace;
                }
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException
                or ArgumentException
                or NotSupportedException)
            {
                // Present but not describable: a locked or unformatted volume, or one mounted
                // somewhere DriveInfo will not accept. It still belongs in the list, just not as
                // something poolable.
                ready = false;
            }
        }

        return new VolumeInfo
        {
            Id = id,
            RootPath = root,
            DriveLetter = letter,
            Label = label,
            FileSystem = fileSystem,
            Kind = kind,
            TotalBytes = total,
            FreeBytes = free,
            IsReady = ready,
        };
    }

    /// <summary>A volume can have several mount points; the first is the one we show and use.</summary>
    private static string? GetFirstMountPoint(string volumeName)
    {
        uint needed = 0;
        if (!NativeMethods.GetVolumePathNamesForVolumeNameW(volumeName, null, 0, ref needed)
            && Marshal.GetLastWin32Error() != NativeMethods.ErrorMoreData)
        {
            return null;
        }

        if (needed == 0)
        {
            return null;
        }

        var buffer = new char[needed];
        if (!NativeMethods.GetVolumePathNamesForVolumeNameW(volumeName, buffer, needed, ref needed))
        {
            return null;
        }

        return VolumeMountPoints.First(buffer);
    }

    private static VolumeKind MapDriveType(uint driveType) => driveType switch
    {
        2 => VolumeKind.Removable,
        3 => VolumeKind.Fixed,
        4 => VolumeKind.Network,
        5 => VolumeKind.Optical,
        6 => VolumeKind.Ram,
        _ => VolumeKind.Unknown,
    };

    private static class NativeMethods
    {
        public const int ErrorMoreData = 234;

        public static readonly IntPtr InvalidHandle = new(-1);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr FindFirstVolumeW(StringBuilder volumeName, int bufferLength);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool FindNextVolumeW(IntPtr findVolume, StringBuilder volumeName, int bufferLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool FindVolumeClose(IntPtr findVolume);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetVolumePathNamesForVolumeNameW(
            string volumeName,
            char[]? buffer,
            uint bufferLength,
            ref uint returnLength);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        public static extern uint GetDriveTypeW(string rootPathName);
    }
}
