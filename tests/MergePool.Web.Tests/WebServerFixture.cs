using System.Net;
using System.Net.Sockets;
using MergePool.Core.Config;
using MergePool.Core.Volumes;
using MergePool.Engine;
using MergePool.Web;

namespace MergePool.Web.Tests;

/// <summary>
/// A real engine behind a real listener on a real loopback port. Nothing here is mocked: these
/// tests exercise the same path a browser takes, including the token check.
/// </summary>
public sealed class WebServerFixture : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "mergepool-web", Guid.NewGuid().ToString("N"));

    private readonly DirectoryVolumeProvider _volumes = new();

    public WebServerFixture(bool allowChanges = true)
    {
        Directory.CreateDirectory(_root);

        Engine = new PoolEngine(new PoolEngineOptions
        {
            ConfigStore = new ConfigStore(Path.Combine(_root, "config.json")),
            VolumeProvider = _volumes,
            MountService = new InMemoryMountService(),
        });

        Options = new WebOptions
        {
            Enabled = true,
            Port = FreePort(),
            Scope = nameof(WebAccessScope.ThisComputer),
            AccessToken = WebAccessToken.Generate(),
            AllowChanges = allowChanges,
        };

        Server = new WebControlServer(new WebApi(Engine), () => Options);
        Server.Apply();

        Client = new HttpClient { BaseAddress = new Uri($"http://localhost:{Options.Port}/") };
    }

    public PoolEngine Engine { get; }

    public WebOptions Options { get; }

    public WebControlServer Server { get; }

    public HttpClient Client { get; }

    public VolumeId AddDrive(string name, long total = 10_000, long free = 9_000)
    {
        var path = Path.Combine(_root, name);
        return _volumes.Add(path, label: name, totalBytes: total, freeBytes: free);
    }

    public string PartRoot(string driveName, Guid partId) =>
        Path.Combine(_root, driveName, ".PoolPart-" + partId.ToString("D"));

    public HttpRequestMessage Authorised(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("Authorization", "Bearer " + Options.AccessToken);
        return request;
    }

    /// <summary>Asks the OS for a port nothing is using, so parallel test runs cannot collide.</summary>
    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    public void Dispose()
    {
        Client.Dispose();
        Server.Dispose();
        Engine.Dispose();

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
