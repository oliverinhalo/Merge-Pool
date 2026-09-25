namespace MergePool.Core.Volumes;

/// <summary>
/// Reads the double-null-terminated path list Windows fills in for a volume's mount points.
/// </summary>
/// <remarks>
/// Only the buffer's shape is Windows specific, not this parsing, so it lives on its own where it
/// can be tested anywhere.
/// </remarks>
internal static class VolumeMountPoints
{
    /// <summary>The first path in the list, or <c>null</c> when the volume has no mount point.</summary>
    /// <remarks>
    /// A volume with no mount point — the EFI system partition and the recovery partition on every
    /// modern Windows install — yields just the terminator. That has to come back as "no mount
    /// point": NUL is not whitespace, so a <c>"\0"</c> string survives an
    /// <see cref="string.IsNullOrWhiteSpace"/> check and then blows up whatever treats it as a path.
    /// </remarks>
    public static string? First(ReadOnlySpan<char> buffer)
    {
        var end = buffer.IndexOf('\0');
        var first = end >= 0 ? buffer[..end] : buffer;

        return first.IsEmpty || first.IsWhiteSpace() ? null : new string(first);
    }
}
