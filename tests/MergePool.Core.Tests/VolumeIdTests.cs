using MergePool.Core.Volumes;
using Xunit;

namespace MergePool.Core.Tests;

public sealed class VolumeIdTests
{
    private const string Guid1 = "3f2504e0-4f89-41d3-9a0c-0305e82c3301";

    [Theory]
    [InlineData(Guid1)]
    [InlineData("{" + Guid1 + "}")]
    [InlineData("\\\\?\\Volume{" + Guid1 + "}\\")]
    [InlineData("\\\\?\\VOLUME{" + Guid1 + "}")]
    public void TryParse_accepts_every_persisted_form(string text)
    {
        Assert.True(VolumeId.TryParse(text, out var id));
        Assert.Equal(Guid.Parse(Guid1), id.Guid);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("D:")]
    [InlineData("not-a-guid")]
    public void TryParse_rejects_non_identities(string? text) =>
        Assert.False(VolumeId.TryParse(text, out _));

    [Fact]
    public void ToVolumePath_round_trips()
    {
        var id = VolumeId.Parse(Guid1);
        Assert.Equal($"\\\\?\\Volume{{{Guid1}}}\\", id.ToVolumePath());
        Assert.True(VolumeId.TryParse(id.ToVolumePath(), out var again));
        Assert.Equal(id, again);
    }

    [Fact]
    public void Empty_is_the_default() => Assert.True(default(VolumeId).IsEmpty);
}
