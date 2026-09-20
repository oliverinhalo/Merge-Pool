using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using MergePool.Core.FileSystem;

namespace MergePool.Fs.WinFsp;

/// <summary>
/// Reads and writes raw self-relative security descriptors. WinFsp hands us descriptors in exactly
/// this form, and going straight to the Win32 API keeps the pool free of the managed ACL types.
/// </summary>
[SupportedOSPlatform("windows")]
public static class SecurityDescriptorInterop
{
    private const int SeFileObject = 1;

    private const uint OwnerInformation = 0x00000001;
    private const uint GroupInformation = 0x00000002;
    private const uint DaclInformation = 0x00000004;
    private const uint SaclInformation = 0x00000008;

    private const uint DefaultSections = OwnerInformation | GroupInformation | DaclInformation;

    /// <summary>Returns the descriptor bytes for a host path, or <c>null</c> when it cannot be read.</summary>
    public static byte[]? TryGetDescriptor(string hostPath)
    {
        var error = NativeMethods.GetNamedSecurityInfoW(
            hostPath,
            SeFileObject,
            DefaultSections,
            out _,
            out _,
            out _,
            out _,
            out var descriptor);

        if (error != 0 || descriptor == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var length = NativeMethods.GetSecurityDescriptorLength(descriptor);
            if (length == 0)
            {
                return null;
            }

            var bytes = new byte[length];
            Marshal.Copy(descriptor, bytes, 0, (int)length);
            return bytes;
        }
        finally
        {
            NativeMethods.LocalFree(descriptor);
        }
    }

    public static bool TrySetDescriptor(string hostPath, byte[] descriptor, AccessControlSections sections)
    {
        if (descriptor.Length == 0)
        {
            return false;
        }

        var information = ToSecurityInformation(sections);
        if (information == 0)
        {
            return true;
        }

        var handle = GCHandle.Alloc(descriptor, GCHandleType.Pinned);
        try
        {
            return NativeMethods.SetFileSecurityW(hostPath, information, handle.AddrOfPinnedObject());
        }
        finally
        {
            handle.Free();
        }
    }

    /// <summary>Applies a descriptor supplied at create time to a freshly created entry.</summary>
    public static void ApplyOnCreate(string hostPath, byte[]? descriptor)
    {
        if (descriptor is { Length: > 0 })
        {
            TrySetDescriptor(hostPath, descriptor, AccessControlSections.Access | AccessControlSections.Owner | AccessControlSections.Group);
        }
    }

    private static uint ToSecurityInformation(AccessControlSections sections)
    {
        uint information = 0;
        if (sections.HasFlag(AccessControlSections.Owner))
        {
            information |= OwnerInformation;
        }

        if (sections.HasFlag(AccessControlSections.Group))
        {
            information |= GroupInformation;
        }

        if (sections.HasFlag(AccessControlSections.Access))
        {
            information |= DaclInformation;
        }

        if (sections.HasFlag(AccessControlSections.Audit))
        {
            information |= SaclInformation;
        }

        return information;
    }

    private static class NativeMethods
    {
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern uint GetNamedSecurityInfoW(
            string objectName,
            int objectType,
            uint securityInformation,
            out IntPtr owner,
            out IntPtr group,
            out IntPtr dacl,
            out IntPtr sacl,
            out IntPtr securityDescriptor);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetFileSecurityW(
            string fileName,
            uint securityInformation,
            IntPtr securityDescriptor);

        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern uint GetSecurityDescriptorLength(IntPtr securityDescriptor);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr LocalFree(IntPtr handle);
    }
}

/// <summary>
/// Carries NTFS security descriptors and alternate data streams along when a file moves between
/// drives, so a relocation is invisible to anything reading the pool.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsFileSecurityCopier : IFileSecurityCopier
{
    public void CopySecurity(string sourceHostPath, string targetHostPath)
    {
        var descriptor = SecurityDescriptorInterop.TryGetDescriptor(sourceHostPath);
        if (descriptor is not null)
        {
            SecurityDescriptorInterop.TrySetDescriptor(
                targetHostPath,
                descriptor,
                AccessControlSections.Access | AccessControlSections.Owner | AccessControlSections.Group);
        }
    }

    public void CopyAlternateStreams(string sourceHostPath, string targetHostPath)
    {
        foreach (var stream in EnumerateStreamNames(sourceHostPath))
        {
            // The unnamed data stream is the file itself and has already been copied.
            if (string.Equals(stream, "::$DATA", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            CopyStream(sourceHostPath + stream, targetHostPath + stream);
        }
    }

    private static void CopyStream(string source, string target)
    {
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None);
        input.CopyTo(output);
        output.Flush(flushToDisk: true);
    }

    private static IEnumerable<string> EnumerateStreamNames(string path)
    {
        var data = new NativeMethods.Win32FindStreamData();
        var handle = NativeMethods.FindFirstStreamW(path, NativeMethods.StreamInfoLevelStandard, data, 0);
        if (handle == NativeMethods.InvalidHandle)
        {
            yield break;
        }

        try
        {
            do
            {
                yield return data.StreamName;
            }
            while (NativeMethods.FindNextStreamW(handle, data));
        }
        finally
        {
            NativeMethods.FindClose(handle);
        }
    }

    private static class NativeMethods
    {
        public const int StreamInfoLevelStandard = 0;

        public static readonly IntPtr InvalidHandle = new(-1);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public sealed class Win32FindStreamData
        {
            public long StreamSize;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 296)]
            public string StreamName = string.Empty;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr FindFirstStreamW(
            string fileName,
            int infoLevel,
            [In, Out] Win32FindStreamData data,
            uint flags);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool FindNextStreamW(IntPtr handle, [In, Out] Win32FindStreamData data);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool FindClose(IntPtr handle);
    }
}
