using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace MergePool.Core.Volumes;

/// <summary>
/// Stable identity of a physical volume. Drive letters move; the volume GUID does not, so every
/// persisted reference to a pooled drive uses this.
/// </summary>
public readonly struct VolumeId : IEquatable<VolumeId>
{
    private readonly Guid _guid;

    private VolumeId(Guid guid) => _guid = guid;

    public Guid Guid => _guid;

    public bool IsEmpty => _guid == Guid.Empty;

    public static VolumeId Empty => default;

    public static VolumeId FromGuid(Guid guid) => new(guid);

    /// <summary>The canonical Windows volume path, e.g. <c>\\?\Volume{GUID}\</c>.</summary>
    public string ToVolumePath() =>
        string.Create(CultureInfo.InvariantCulture, $@"\\?\Volume{{{_guid:D}}}\");

    /// <summary>
    /// Parses either a bare GUID, <c>{GUID}</c>, or a full <c>\\?\Volume{GUID}\</c> path. This is the
    /// only accepted persisted form, so config written by any app version parses the same way.
    /// </summary>
    public static bool TryParse(string? text, out VolumeId volumeId)
    {
        volumeId = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var span = text.AsSpan().Trim();

        const string prefix = @"\\?\Volume";
        if (span.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            span = span[prefix.Length..];
        }

        span = span.TrimEnd('\\').TrimEnd('/');
        span = span.TrimStart('{').TrimEnd('}');

        if (!Guid.TryParseExact(span, "D", out var guid))
        {
            return false;
        }

        volumeId = new VolumeId(guid);
        return true;
    }

    public static VolumeId Parse(string text) =>
        TryParse(text, out var id) ? id : throw new FormatException($"'{text}' is not a volume identifier.");

    public bool Equals(VolumeId other) => _guid.Equals(other._guid);

    public override bool Equals([NotNullWhen(true)] object? obj) => obj is VolumeId other && Equals(other);

    public override int GetHashCode() => _guid.GetHashCode();

    public override string ToString() => ToVolumePath();

    public static bool operator ==(VolumeId left, VolumeId right) => left.Equals(right);

    public static bool operator !=(VolumeId left, VolumeId right) => !left.Equals(right);
}
