using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using MergePool.Core.Volumes;
using Xunit;

namespace MergePool.Web.Tests;

/// <summary>
/// The web interface can do what the window can do. These tests are the claim: anything the window
/// offers has an endpoint here, and the ones that change something refuse when the interface is set
/// to look but not touch.
/// </summary>
public sealed class WebParityTests : IDisposable
{
    private readonly WebServerFixture _fixture = new();

    private async Task<JsonElement> Get(string path)
    {
        var response = await _fixture.Client.SendAsync(_fixture.Authorised(HttpMethod.Get, path));
        response.EnsureSuccessStatusCode();

        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private async Task<HttpResponseMessage> Send(HttpMethod method, string path, object? body = null)
    {
        var request = _fixture.Authorised(method, path);

        if (body is not null)
        {
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        }

        return await _fixture.Client.SendAsync(request);
    }

    private async Task<JsonElement> Post(string path, object? body = null)
    {
        var response = await Send(HttpMethod.Post, path, body);
        response.EnsureSuccessStatusCode();

        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private async Task<Guid> CreatePool(string name = "Media")
    {
        var volume = _fixture.AddDrive(name + "-one");
        var created = await Post("/api/pools", new
        {
            name,
            mountPoint = "P",
            volumeIds = new[] { volume.ToVolumePath() },
        });

        return created.GetProperty("created").GetProperty("poolId").GetGuid();
    }

    [Fact]
    public async Task A_pool_can_be_made_from_the_browser()
    {
        // The thing the window could do and this page could not: a pool had to be created on the
        // computer itself before the web interface was any use at all.
        var volume = _fixture.AddDrive("one");

        var result = await Post("/api/pools", new
        {
            name = "Media",
            mountPoint = "P",
            volumeIds = new[] { volume.ToVolumePath() },
        });

        var created = result.GetProperty("created");
        Assert.Equal("Media", created.GetProperty("name").GetString());
        Assert.Equal("P:", created.GetProperty("mountPoint").GetString());
        Assert.Single(result.GetProperty("pools").EnumerateArray());
    }

    [Fact]
    public async Task Making_a_pool_says_what_is_missing_rather_than_failing_vaguely()
    {
        var volume = _fixture.AddDrive("one");

        var noName = await Send(HttpMethod.Post, "/api/pools", new
        {
            name = "",
            mountPoint = "P",
            volumeIds = new[] { volume.ToVolumePath() },
        });

        Assert.Equal(HttpStatusCode.BadRequest, noName.StatusCode);
        Assert.Contains("name", await noName.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

        var noDrives = await Send(HttpMethod.Post, "/api/pools", new
        {
            name = "Media",
            mountPoint = "P",
            volumeIds = Array.Empty<string>(),
        });

        Assert.Equal(HttpStatusCode.BadRequest, noDrives.StatusCode);
        Assert.Contains("drive", await noDrives.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_pool_can_be_removed_and_its_files_stay_put()
    {
        var poolId = await CreatePool();

        var response = await Send(HttpMethod.Delete, $"/api/pools/{poolId}");
        response.EnsureSuccessStatusCode();

        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.True(body.GetProperty("removed").GetBoolean());
        Assert.Empty(body.GetProperty("pools").EnumerateArray());

        // Said plainly, because "remove" is the word people worry about.
        Assert.Contains("still on its drive", body.GetProperty("message").GetString()!);
    }

    [Fact]
    public async Task Every_engine_tunable_can_be_read_and_changed()
    {
        var before = await Get("/api/settings");
        var placement = before.GetProperty("placement");

        // A sample across the groups, including ones that were only reachable by hand-editing
        // config.json before the window grew its Advanced switch.
        Assert.True(placement.TryGetProperty("speedWeight", out _));
        Assert.True(placement.TryGetProperty("idleProbeBytes", out _));
        Assert.True(placement.TryGetProperty("rebalanceTargetImbalance", out _));
        Assert.True(placement.TryGetProperty("latencyThrottleRatio", out _));

        var after = await Post("/api/settings", new
        {
            speedWeight = 2.5,
            minFreeBytes = 4L * 1024 * 1024 * 1024,
            latencyThrottleRatio = 4.5,
            rebalanceTriggerImbalance = 0.25,
        });

        var saved = after.GetProperty("placement");
        Assert.Equal(2.5, saved.GetProperty("speedWeight").GetDouble());
        Assert.Equal(4L * 1024 * 1024 * 1024, saved.GetProperty("minFreeBytes").GetInt64());
        Assert.Equal(4.5, saved.GetProperty("latencyThrottleRatio").GetDouble());
        Assert.Equal(0.25, saved.GetProperty("rebalanceTriggerImbalance").GetDouble());
    }

    [Fact]
    public async Task A_setting_that_would_wedge_the_engine_comes_back_clamped()
    {
        // The same clamp the pipe applies. A browser is no more trustworthy than a text editor.
        var after = await Post("/api/settings", new
        {
            throttleEnterRatio = 0.85,
            throttleExitRatio = 0.2,
            minSpeedFactor = 0.9,
            maxSpeedFactor = 0.2,
        });

        var placement = after.GetProperty("placement");

        Assert.True(placement.GetProperty("throttleExitRatio").GetDouble()
            > placement.GetProperty("throttleEnterRatio").GetDouble());
        Assert.True(placement.GetProperty("maxSpeedFactor").GetDouble()
            > placement.GetProperty("minSpeedFactor").GetDouble());
    }

    [Fact]
    public async Task Copy_pacing_and_update_preferences_can_be_changed()
    {
        var duplication = await Post("/api/settings/duplication", new
        {
            maxBytesPerSecond = 64L * 1024 * 1024,
            scanIntervalMinutes = 45,
            pauseWhileBusy = false,
        });

        var copying = duplication.GetProperty("duplication");
        Assert.Equal(64L * 1024 * 1024, copying.GetProperty("maxBytesPerSecond").GetInt64());
        Assert.Equal(45, copying.GetProperty("scanIntervalMinutes").GetDouble());
        Assert.False(copying.GetProperty("pauseWhileBusy").GetBoolean());

        var updates = await Post("/api/settings/updates", new
        {
            automaticInstall = false,
            includePrereleases = true,
            checkIntervalHours = 12,
        });

        var updating = updates.GetProperty("updates");
        Assert.False(updating.GetProperty("automaticInstall").GetBoolean());
        Assert.True(updating.GetProperty("includePrereleases").GetBoolean());
        Assert.Equal(12, updating.GetProperty("checkIntervalHours").GetDouble());
    }

    [Fact]
    public async Task The_web_interfaces_own_settings_are_read_only_here()
    {
        // Deliberate. Turning changes off, or moving the port, from this page would cut the page off
        // from the setting that undoes it — a one-way door with no handle on this side.
        var settings = await Get("/api/settings");
        var web = settings.GetProperty("webInterface");

        Assert.True(web.GetProperty("readOnlyHere").GetBoolean());
        Assert.Contains("window", web.GetProperty("why").GetString()!);
        Assert.True(web.TryGetProperty("port", out _));
    }

    [Fact]
    public async Task Sharing_a_pool_can_be_set_up_and_refuses_until_an_account_is_named()
    {
        var poolId = await CreatePool();

        var fresh = await Get($"/api/pools/{poolId}/share");
        Assert.False(fresh.GetProperty("enabled").GetBoolean());

        // On, but with nobody allowed: the page is told why rather than being left to wonder why no
        // share appeared.
        var halfDone = await Post($"/api/pools/{poolId}/share", new { enabled = true });
        Assert.True(halfDone.GetProperty("enabled").GetBoolean());
        Assert.Contains("No account", halfDone.GetProperty("problem").GetString()!);

        var ready = await Post($"/api/pools/{poolId}/share", new
        {
            name = "Media (2026)",
            readOnly = true,
            allowedAccounts = new[] { "MACHINE\\poolsvc", "  ", "MACHINE\\poolsvc" },
        });

        Assert.True(ready.GetProperty("readOnly").GetBoolean());
        Assert.Equal(JsonValueKind.Null, ready.GetProperty("problem").ValueKind);
        Assert.Equal("Media 2026", ready.GetProperty("effectiveName").GetString());

        // Blanks and duplicates are dropped on the way in.
        Assert.Single(ready.GetProperty("allowedAccounts").EnumerateArray());
    }

    [Fact]
    public async Task A_pool_can_be_remounted_from_the_browser()
    {
        // The recovery action for a pool that has stopped answering.
        var poolId = await CreatePool();

        var result = await Post($"/api/pools/{poolId}/remount");
        Assert.Equal(poolId, result.GetProperty("poolId").GetGuid());
    }

    [Fact]
    public async Task The_page_is_told_whether_each_mount_is_actually_answering()
    {
        await CreatePool();

        var health = await Get("/api/health");
        var pool = Assert.Single(health.GetProperty("pools").EnumerateArray());

        Assert.True(pool.TryGetProperty("state", out _));
        Assert.True(pool.TryGetProperty("summary", out _));
    }

    [Fact]
    public async Task The_page_is_told_which_drive_letters_are_free()
    {
        // The create-a-pool form needs them, and the browser cannot see the machine's drives.
        var drives = await Get("/api/drives");

        Assert.True(drives.TryGetProperty("letters", out var letters));
        Assert.NotEmpty(letters.EnumerateArray());
    }

    [Fact]
    public async Task The_page_is_told_when_it_may_not_change_anything()
    {
        var status = await Get("/api/status");

        // False here, because this fixture allows changes. The point is that the field reflects the
        // setting at all: it used to be hard-coded false, so a look-but-not-touch interface still
        // drew every button and only said no once one was pressed.
        Assert.False(status.GetProperty("readOnly").GetBoolean());
    }

    public void Dispose() => _fixture.Dispose();
}

/// <summary>
/// With changes turned off, everything that would change something is refused — including the
/// endpoints added for parity with the window.
/// </summary>
public sealed class WebReadOnlyParityTests : IDisposable
{
    private readonly WebServerFixture _fixture = new(allowChanges: false);

    [Fact]
    public async Task The_page_is_told_it_is_read_only()
    {
        var response = await _fixture.Client.SendAsync(_fixture.Authorised(HttpMethod.Get, "/api/status"));
        response.EnsureSuccessStatusCode();

        var status = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.True(status.GetProperty("readOnly").GetBoolean());
    }

    [Theory]
    [InlineData("POST", "/api/pools")]
    [InlineData("POST", "/api/settings")]
    [InlineData("POST", "/api/settings/duplication")]
    [InlineData("POST", "/api/settings/updates")]
    public async Task Nothing_that_changes_something_is_allowed(string method, string path)
    {
        var request = _fixture.Authorised(new HttpMethod(method), path);
        request.Content = new StringContent("{}", Encoding.UTF8, "application/json");

        var response = await _fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Reading_still_works()
    {
        var response = await _fixture.Client.SendAsync(_fixture.Authorised(HttpMethod.Get, "/api/settings"));

        response.EnsureSuccessStatusCode();
    }

    public void Dispose() => _fixture.Dispose();
}
