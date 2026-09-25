using System.Net;
using System.Text;
using MergePool.Update;
using Xunit;

namespace MergePool.Update.Tests;

/// <summary>Serves canned responses so the feed can be exercised without a network.</summary>
internal sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<Uri> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        Requests.Add(request.RequestUri!);
        return Task.FromResult(respond(request));
    }

    public static StubHandler Json(string body) => new(_ => new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    });
}

public sealed class ReleaseFeedTests
{
    private const string PackageUrl = "https://example.invalid/MergePool-0.3.0-windows-x64.zip";

    private static string Release(
        string tag,
        bool prerelease = false,
        bool draft = false,
        string assetName = "MergePool-0.3.0-windows-x64.zip",
        string? digest = "sha256:abc123") =>
        $$"""
        {
          "tag_name": "{{tag}}",
          "draft": {{(draft ? "true" : "false")}},
          "prerelease": {{(prerelease ? "true" : "false")}},
          "body": "Notes for {{tag}}",
          "html_url": "https://example.invalid/releases/{{tag}}",
          "assets": [
            {
              "name": "{{assetName}}",
              "size": 1234,
              "browser_download_url": "{{PackageUrl}}",
              "digest": {{(digest is null ? "null" : $"\"{digest}\"")}}
            }
          ]
        }
        """;

    private static GitHubReleaseFeed Feed(string body, ReleaseFeedOptions? options = null) =>
        new(new HttpClient(StubHandler.Json(body)), options);

    [Fact]
    public async Task The_newest_release_with_a_package_is_returned()
    {
        var feed = Feed($"[{Release("v0.2.0")},{Release("v0.4.0")},{Release("v0.3.0")}]");

        var release = await feed.GetLatestAsync(CancellationToken.None);

        Assert.NotNull(release);
        Assert.Equal("0.4.0", release.Version);
        Assert.Equal(PackageUrl, release.PackageUrl.ToString());
        Assert.Equal(1234, release.PackageBytes);
        Assert.Equal("abc123", release.Sha256);
    }

    [Fact]
    public async Task Drafts_and_prereleases_are_skipped_by_default()
    {
        var feed = Feed($"[{Release("v0.9.0", draft: true)},{Release("v0.8.0", prerelease: true)},{Release("v0.3.0")}]");

        var release = await feed.GetLatestAsync(CancellationToken.None);

        Assert.Equal("0.3.0", release!.Version);
    }

    [Fact]
    public async Task Prereleases_are_offered_when_asked_for()
    {
        var feed = Feed(
            $"[{Release("v0.8.0", prerelease: true)},{Release("v0.3.0")}]",
            new ReleaseFeedOptions { IncludePrereleases = true });

        var release = await feed.GetLatestAsync(CancellationToken.None);

        Assert.Equal("0.8.0", release!.Version);
    }

    [Fact]
    public async Task A_release_with_no_package_for_this_platform_is_ignored()
    {
        var feed = Feed($"[{Release("v0.9.0", assetName: "MergePool-0.9.0-setup.exe")},{Release("v0.3.0")}]");

        var release = await feed.GetLatestAsync(CancellationToken.None);

        Assert.Equal("0.3.0", release!.Version);
    }

    [Fact]
    public async Task An_empty_feed_simply_offers_nothing()
    {
        Assert.Null(await Feed("[]").GetLatestAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_malformed_tag_does_not_break_the_check()
    {
        var feed = Feed($"[{Release("not-a-version")},{Release("v0.3.0")}]");

        var release = await feed.GetLatestAsync(CancellationToken.None);

        Assert.Equal("0.3.0", release!.Version);
    }

    [Theory]
    [InlineData("0.3.0", "0.2.0", true)]
    [InlineData("v0.3.0", "0.3.0", false)]
    [InlineData("0.3.1", "0.3.0", true)]
    [InlineData("0.10.0", "0.9.0", true)]
    [InlineData("0.2.0", "0.3.0", false)]
    public void Version_ordering_handles_tags_and_double_digits(string candidate, string installed, bool newer)
    {
        var release = new ReleaseInfo
        {
            Version = candidate,
            PackageUrl = new Uri(PackageUrl),
        };

        Assert.Equal(newer, release.IsNewerThan(installed));
    }

    [Fact]
    public void A_prerelease_suffix_does_not_make_a_version_unparseable()
    {
        Assert.True(ReleaseInfo.TryParse("v1.2.3-beta.4", out var version));
        Assert.Equal(new Version(1, 2, 3), version);
    }
}
