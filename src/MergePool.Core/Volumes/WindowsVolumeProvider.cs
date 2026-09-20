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
            return volumes;
        }

        try
        {
            do
            {
                var volumeName = buffer.ToString();
                if (VolumeId.TryParse(volumeName, out var id))
                {
                    volumes.Add(Describe(id, volumeName));
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
            catch (IOException)
            {
                ready = false;
            }
            catch (UnauthorizedAccessException)
            {
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

        var first = new string(buffer);
        var end = first.IndexOf('\0', StringComparison.Ordinal);
        if (end > 0)
        {
            first = first[..end];
        }

        return string.IsNullOrWhiteSpace(first) ? null : first;
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
