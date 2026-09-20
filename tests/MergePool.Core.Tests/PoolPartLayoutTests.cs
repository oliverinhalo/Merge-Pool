using MergePool.Core.Model;
using Xunit;

namespace MergePool.Core.Tests;

public sealed class PoolPartLayoutTests
{
    [Fact]
    public void FolderName_is_the_stable_on_disk_contract()
    {
        var id = Guid.Parse("3f2504e0-4f89-41d3-9a0c-0305e82c3301");
        Assert.Equal(".PoolPart-3f2504e0-4f89-41d3-9a0c-0305e82c3301", PoolPartLayout.FolderName(id));
    }

    [Fact]
    public void TryParseFolderName_round_trips()
    {
        var id = Guid.NewGuid();
        Assert.True(PoolPartLayout.TryParseFolderName(PoolPartLayout.FolderName(id), out var parsed));
        Assert.Equal(id, parsed);
    }

    [Theory]
    [InlineData("PoolPart-abc")]
    [InlineData(".PoolPart-")]
    [InlineData(".PoolPart-not-a-guid")]
    [InlineData("Documents")]
    public void TryParseFolderName_rejects_other_folders(string name) =>
        Assert.False(PoolPartLayout.TryParseFolderName(name, out _));

    [Fact]
    public void IsInsidePart_guards_the_part_boundary()
    {
        var part = Path.Combine(Path.GetTempPath(), "vol", ".PoolPart-x");

        Assert.True(PoolPartLayout.IsInsidePart(part, Path.Combine(part, "a", "b.txt")));
        Assert.True(PoolPartLayout.IsInsidePart(part, part));
        Assert.False(PoolPartLayout.IsInsidePart(part, Path.Combine(Path.GetTempPath(), "vol", "other.txt")));
        Assert.False(PoolPartLayout.IsInsidePart(part, Path.Combine(part, "..", "escape.txt")));
        Assert.False(PoolPartLayout.IsInsidePart(part, part + "-sibling"));
    }
}
