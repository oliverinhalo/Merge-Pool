using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using MergePool.Core.Config;
using MergePool.Core.Volumes;
using MergePool.Web;
using Xunit;

namespace MergePool.Web.Tests;

public sealed class WebControlServerTests : IDisposable
{
    private readonly WebServerFixture _fixture = new();

    private async Task<JsonElement> GetAsync(string path)
    {
        var response = await _fixture.Client.SendAsync(_fixture.Authorised(HttpMethod.Get, path));
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private Guid CreatePool(params string[] driveNames)
    {
        var volumes = driveNames.Select(name => _fixture.AddDrive(name)).ToList();
        return _fixture.Engine.CreatePool("Media", "P:", volumes).PoolId;
    }

    [Fact]
    public void The_server_reports_where_it_is_listening()
    {
        var status = _fixture.Server.Status;

        Assert.Equal(WebServerState.Listening, status.State);
        Assert.Equal(_fixture.Options.Port, status.Port);
        Assert.Equal($"http://localhost:{_fixture.Options.Port}/", status.Url);
        Assert.Null(status.Error);
    }

    [Fact]
    public async Task The_page_is_served_without_a_token_because_it_is_only_the_sign_in_shell()
    {
        var response = await _fixture.Client.GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("MergePool", html, StringComparison.Ordinal);
        Assert.Contains("Access token", html, StringComparison.Ordinal);

        // The shell must carry no pool data: it is reachable by anyone who can reach the port.
        Assert.DoesNotContain("PoolPart", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_script_the_page_needs_is_served_too()
    {
        var response = await _fixture.Client.GetAsync("/app.js");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/javascript", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task The_page_takes_a_token_handed_to_it_in_the_url_fragment()
    {
        // This is how MergePool opens the page for you: the fragment never leaves the browser, so
        // the token is in no request, log or proxy on the way here, and the page clears it from the
        // address bar. Losing this silently would turn one click back into copy-and-paste.
        var script = await _fixture.Client.GetStringAsync("/app.js");

        Assert.Contains("token=", script, StringComparison.Ordinal);
        Assert.Contains("location.hash", script, StringComparison.Ordinal);
        Assert.Contains("history.replaceState", script, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Data_without_a_token_is_refused()
    {
        var response = await _fixture.Client.GetAsync("/api/pools");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Data_with_the_wrong_token_is_refused()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/pools");
        request.Headers.Add("Authorization", "Bearer " + WebAccessToken.Generate());

        var response = await _fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Changing_the_token_signs_a_browser_out_immediately()
    {
        var old = _fixture.Options.AccessToken;
        Assert.Equal(HttpStatusCode.OK, (await _fixture.Client.SendAsync(
            _fixture.Authorised(HttpMethod.Get, "/api/status"))).StatusCode);

        _fixture.Options.AccessToken = WebAccessToken.Generate();

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/status");
        request.Headers.Add("Authorization", "Bearer " + old);

        Assert.Equal(HttpStatusCode.Unauthorized, (await _fixture.Client.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task The_token_is_also_accepted_as_a_plain_header()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/status");
        request.Headers.Add("X-MergePool-Token", _fixture.Options.AccessToken);

        Assert.Equal(HttpStatusCode.OK, (await _fixture.Client.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task Status_reports_the_engine()
    {
        CreatePool("One");

        var status = await GetAsync("/api/status");

        Assert.Equal(1, status.GetProperty("poolCount").GetInt32());
        Assert.Equal(1, status.GetProperty("mountedCount").GetInt32());
        Assert.False(status.GetProperty("draining").GetBoolean());
    }

    [Fact]
    public async Task Pools_report_what_they_hold_separately_from_what_their_drives_hold()
    {
        var poolId = CreatePool("One");
        var runtime = _fixture.Engine.FindPool(poolId)!;
        var partId = runtime.Snapshot.Parts[0].PartId;

        File.WriteAllText(Path.Combine(_fixture.PartRoot("One", partId), "pooled.bin"), new string('x', 120));
        _fixture.Engine.Tick();

        var pools = await GetAsync("/api/pools");
        var pool = pools.GetProperty("pools")[0];

        Assert.Equal("Media", pool.GetProperty("name").GetString());
        Assert.True(pool.GetProperty("measured").GetBoolean());
        Assert.Equal(120, pool.GetProperty("poolUsedBytes").GetInt64());
        Assert.Equal(10_000, pool.GetProperty("totalBytes").GetInt64());

        // 1000 bytes of the drive are used, only 120 of them by the pool.
        Assert.Equal(9_120, pool.GetProperty("poolCapacityBytes").GetInt64());
    }

    [Fact]
    public async Task Drives_are_listed_with_which_pool_they_belong_to()
    {
        var poolId = CreatePool("One");
        _fixture.AddDrive("Spare");

        var drives = (await GetAsync("/api/drives")).GetProperty("drives").EnumerateArray().ToList();

        Assert.Equal(2, drives.Count);
        Assert.Contains(drives, drive => drive.GetProperty("poolId").GetGuid() == poolId);
        Assert.Contains(drives, drive => drive.GetProperty("poolId").ValueKind == JsonValueKind.Null);
    }

    [Fact]
    public async Task A_pool_can_be_unmounted_and_mounted_from_the_browser()
    {
        var poolId = CreatePool("One");

        var unmounted = await _fixture.Client.SendAsync(
            _fixture.Authorised(HttpMethod.Post, $"/api/pools/{poolId}/unmount"));
        unmounted.EnsureSuccessStatusCode();
        Assert.False(_fixture.Engine.IsMounted(poolId));

        var mounted = await _fixture.Client.SendAsync(
            _fixture.Authorised(HttpMethod.Post, $"/api/pools/{poolId}/mount"));
        mounted.EnsureSuccessStatusCode();
        Assert.True(_fixture.Engine.IsMounted(poolId));
    }

    [Fact]
    public async Task A_drive_can_be_added_to_a_pool_from_the_browser()
    {
        var poolId = CreatePool("One");
        var spare = _fixture.AddDrive("Spare");

        var request = _fixture.Authorised(HttpMethod.Post, $"/api/pools/{poolId}/drives");
        request.Content = new StringContent(
            JsonSerializer.Serialize(new { volumeIds = new[] { spare.ToVolumePath() } }),
            Encoding.UTF8,
            "application/json");

        var response = await _fixture.Client.SendAsync(request);
        response.EnsureSuccessStatusCode();

        Assert.Equal(2, _fixture.Engine.FindPool(poolId)!.FreshSnapshot.Parts.Count);
    }

    [Fact]
    public async Task A_drive_can_leave_a_pool_with_its_files_left_on_it()
    {
        var poolId = CreatePool("One", "Two");
        var runtime = _fixture.Engine.FindPool(poolId)!;
        var leaving = runtime.FreshSnapshot.Parts.Single(part => part.Label == "Two");
        var partRoot = _fixture.PartRoot("Two", leaving.PartId);

        File.WriteAllText(Path.Combine(partRoot, "keepme.txt"), "precious");

        var response = await _fixture.Client.SendAsync(_fixture.Authorised(
            HttpMethod.Delete,
            $"/api/pools/{poolId}/drives/{Uri.EscapeDataString(leaving.Volume.ToVolumePath())}"));

        response.EnsureSuccessStatusCode();

        Assert.Single(_fixture.Engine.FindPool(poolId)!.FreshSnapshot.Parts);
        Assert.Equal("precious", File.ReadAllText(Path.Combine(partRoot, "keepme.txt")));
    }

    [Fact]
    public async Task A_safe_removal_can_be_planned_before_it_is_run()
    {
        var poolId = CreatePool("One", "Two");
        var runtime = _fixture.Engine.FindPool(poolId)!;
        var leaving = runtime.FreshSnapshot.Parts.Single(part => part.Label == "Two");

        File.WriteAllText(
            Path.Combine(_fixture.PartRoot("Two", leaving.PartId), "film.mkv"), new string('x', 40));

        var response = await _fixture.Client.SendAsync(_fixture.Authorised(
            HttpMethod.Delete,
            $"/api/pools/{poolId}/drives/{Uri.EscapeDataString(leaving.Volume.ToVolumePath())}?move=true&plan=true"));

        response.EnsureSuccessStatusCode();
        var plan = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        Assert.True(plan.GetProperty("fits").GetBoolean());
        Assert.Equal(1, plan.GetProperty("fileCount").GetInt32());

        // Planning changes nothing.
        Assert.Equal(2, _fixture.Engine.FindPool(poolId)!.FreshSnapshot.Parts.Count);
    }

    [Fact]
    public async Task An_unknown_endpoint_is_a_not_found_rather_than_a_crash()
    {
        var response = await _fixture.Client.SendAsync(_fixture.Authorised(HttpMethod.Get, "/api/nonsense"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_read_endpoint_refuses_the_wrong_verb()
    {
        var poolId = CreatePool("One");

        var response = await _fixture.Client.SendAsync(
            _fixture.Authorised(HttpMethod.Get, $"/api/pools/{poolId}/mount"));

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    [Fact]
    public async Task A_bad_pool_identifier_is_a_bad_request()
    {
        var response = await _fixture.Client.SendAsync(_fixture.Authorised(HttpMethod.Get, "/api/pools/not-a-guid"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_pool_that_does_not_exist_is_a_not_found()
    {
        var response = await _fixture.Client.SendAsync(
            _fixture.Authorised(HttpMethod.Get, $"/api/pools/{Guid.NewGuid()}"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Turning_the_interface_off_stops_answering()
    {
        _fixture.Options.Enabled = false;
        _fixture.Server.Apply();

        Assert.Equal(WebServerState.Stopped, _fixture.Server.Status.State);

        await Assert.ThrowsAnyAsync<HttpRequestException>(() => _fixture.Client.GetAsync("/api/status"));
    }

    [Fact]
    public void A_port_outside_the_usable_range_is_refused_with_a_reason()
    {
        _fixture.Options.Port = 80;
        _fixture.Server.Apply();

        var status = _fixture.Server.Status;

        Assert.Equal(WebServerState.Failed, status.State);
        Assert.Contains("outside the usable range", status.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Turning_it_off_clears_an_earlier_port_failure()
    {
        _fixture.Options.Port = 80;
        _fixture.Server.Apply();
        Assert.Equal(WebServerState.Failed, _fixture.Server.Status.State);

        _fixture.Options.Enabled = false;
        _fixture.Server.Apply();

        var status = _fixture.Server.Status;

        Assert.Equal(WebServerState.Stopped, status.State);
        Assert.Null(status.Error);
    }

    [Fact]
    public void A_failure_gives_way_to_a_working_port()
    {
        _fixture.Options.Port = 80;
        _fixture.Server.Apply();
        Assert.Equal(WebServerState.Failed, _fixture.Server.Status.State);

        _fixture.Options.Port = _fixture.Options.Port + 9000;
        _fixture.Server.Apply();

        var status = _fixture.Server.Status;

        Assert.Equal(WebServerState.Listening, status.State);
        Assert.Null(status.Error);
    }

    [Fact]
    public async Task Moving_to_another_port_rebinds()
    {
        var first = _fixture.Options.Port;

        _fixture.Options.Port = first + 1;
        _fixture.Server.Apply();

        Assert.Equal(WebServerState.Listening, _fixture.Server.Status.State);
        Assert.Equal(first + 1, _fixture.Server.Status.Port);

        using var moved = new HttpClient { BaseAddress = new Uri($"http://localhost:{first + 1}/") };
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/status");
        request.Headers.Add("Authorization", "Bearer " + _fixture.Options.AccessToken);

        Assert.Equal(HttpStatusCode.OK, (await moved.SendAsync(request)).StatusCode);
    }

    public void Dispose() => _fixture.Dispose();
}

public sealed class ReadOnlyWebInterfaceTests : IDisposable
{
    private readonly WebServerFixture _fixture = new(allowChanges: false);

    [Fact]
    public async Task Reading_still_works()
    {
        var response = await _fixture.Client.SendAsync(_fixture.Authorised(HttpMethod.Get, "/api/status"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Anything_that_would_change_something_is_refused()
    {
        var volume = _fixture.AddDrive("One");
        var poolId = _fixture.Engine.CreatePool("Media", "P:", [volume]).PoolId;

        var response = await _fixture.Client.SendAsync(
            _fixture.Authorised(HttpMethod.Post, $"/api/pools/{poolId}/unmount"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.True(_fixture.Engine.IsMounted(poolId));
    }

    public void Dispose() => _fixture.Dispose();
}

public sealed class WebAccessTokenTests
{
    [Fact]
    public void Generated_tokens_are_long_and_never_repeat()
    {
        var tokens = Enumerable.Range(0, 50).Select(_ => WebAccessToken.Generate()).ToList();

        Assert.All(tokens, token => Assert.True(token.Length >= 30));
        Assert.Equal(tokens.Count, tokens.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Tokens_are_url_safe_so_they_survive_being_pasted_anywhere()
    {
        foreach (var token in Enumerable.Range(0, 20).Select(_ => WebAccessToken.Generate()))
        {
            Assert.DoesNotContain('+', token);
            Assert.DoesNotContain('/', token);
            Assert.DoesNotContain('=', token);
        }
    }

    [Theory]
    [InlineData(null, "anything", false)]
    [InlineData("", "anything", false)]
    [InlineData("secret", null, false)]
    [InlineData("secret", "", false)]
    [InlineData("secret", "Secret", false)]
    [InlineData("secret", "secret ", false)]
    [InlineData("secret", "secret", true)]
    public void Matching_is_exact(string? expected, string? presented, bool matches) =>
        Assert.Equal(matches, WebAccessToken.Matches(expected, presented));

    [Fact]
    public void An_unconfigured_token_never_matches_an_empty_one()
    {
        // Otherwise turning the interface on before a token exists would let anyone in.
        Assert.False(WebAccessToken.Matches(string.Empty, string.Empty));
    }
}

public sealed class WebOptionsTests
{
    [Theory]
    [InlineData(8787, true)]
    [InlineData(1024, true)]
    [InlineData(65535, true)]
    [InlineData(80, false)]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    [InlineData(65536, false)]
    public void Only_ports_outside_the_well_known_range_are_usable(int port, bool usable) =>
        Assert.Equal(usable, WebOptions.IsUsablePort(port));

    [Fact]
    public void The_interface_is_off_and_local_until_someone_says_otherwise()
    {
        var options = new WebOptions();

        Assert.False(options.Enabled);
        Assert.Equal(WebAccessScope.ThisComputer, options.AccessScope);
        Assert.Equal(WebOptions.DefaultPort, options.Port);
        Assert.Empty(options.AccessToken);
    }

    [Fact]
    public void An_unreadable_scope_falls_back_to_this_computer_only()
    {
        // A hand-edited or newer config must never widen access by accident.
        var options = new WebOptions { Scope = "EveryoneOnTheInternet" };

        Assert.Equal(WebAccessScope.ThisComputer, options.AccessScope);
    }
}
