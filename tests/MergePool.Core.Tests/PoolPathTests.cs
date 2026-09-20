using MergePool.Core.Paths;
using Xunit;

namespace MergePool.Core.Tests;

public sealed class PoolPathTests
{
    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("\\", "")]
    [InlineData("/", "")]
    [InlineData("a", "a")]
    [InlineData("\\a\\b", "a\\b")]
    [InlineData("a/b/c", "a\\b\\c")]
    [InlineData("a\\\\b", "a\\b")]
    [InlineData("a\\b\\", "a\\b")]
    [InlineData("///a///b///", "a\\b")]
    public void Normalize_canonicalizes(string? input, string expected) =>
        Assert.Equal(expected, PoolPath.Normalize(input));

    [Fact]
    public void GetParent_returns_null_for_root() => Assert.Null(PoolPath.GetParent(""));

    [Fact]
    public void GetParent_of_top_level_is_root() => Assert.Equal("", PoolPath.GetParent("file.txt"));

    [Fact]
    public void GetParent_walks_up_one_level() => Assert.Equal("a\\b", PoolPath.GetParent("a/b/c.txt"));

    [Fact]
    public void GetName_returns_last_component() => Assert.Equal("c.txt", PoolPath.GetName("a/b/c.txt"));

    [Fact]
    public void Combine_joins_and_normalizes() => Assert.Equal("a\\b\\c", PoolPath.Combine("/a/", "b\\c"));

    [Fact]
    public void IsAtOrUnder_matches_only_on_component_boundaries()
    {
        Assert.True(PoolPath.IsAtOrUnder("a\\b\\c", "a\\b"));
        Assert.True(PoolPath.IsAtOrUnder("a\\b", "a\\b"));
        Assert.True(PoolPath.IsAtOrUnder("anything", ""));
        Assert.False(PoolPath.IsAtOrUnder("a\\bc", "a\\b"));
    }

    [Fact]
    public void ToHostPath_uses_the_native_separator()
    {
        var host = PoolPath.ToHostPath(Path.Combine("x", "root"), "a/b.txt");
        Assert.Equal(Path.Combine("x", "root", "a", "b.txt"), host);
    }

    [Fact]
    public void ToHostPath_of_root_is_the_host_root() =>
        Assert.Equal("/mnt/root", PoolPath.ToHostPath("/mnt/root", ""));

    [Theory]
    [InlineData("ok.txt", true)]
    [InlineData("a b", true)]
    [InlineData("", false)]
    [InlineData(".", false)]
    [InlineData("..", false)]
    [InlineData("a:b", false)]
    [InlineData("a\\b", false)]
    [InlineData("a*b", false)]
    public void IsValidName_rejects_unusable_names(string name, bool expected) =>
        Assert.Equal(expected, PoolPath.IsValidName(name));

    [Fact]
    public void Normalize_handles_paths_longer_than_the_stack_buffer()
    {
        var component = new string('x', 200);
        var input = string.Join('/', Enumerable.Repeat(component, 8));
        var normalized = PoolPath.Normalize(input);

        Assert.Equal(8, PoolPath.Split(normalized).Length);
        Assert.True(normalized.Length > 512);
    }
}
