namespace MergePool.Update.Tests;

/// <summary>A side-by-side install on disk, with scriptable service and engine behaviour.</summary>
public sealed class FakeInstall : IDisposable
{
    public FakeInstall(params string[] versions)
    {
        Root = Path.Combine(Path.GetTempPath(), "mergepool-upgrade", Guid.NewGuid().ToString("N"));
        Layout = new InstallLayout(Root);

        foreach (var version in versions)
        {
            Install(version);
        }

        Link = new FakeLink(Layout);
        Service = new FakeService();
        Engine = new FakeEngine();
    }

    public string Root { get; }

    public InstallLayout Layout { get; }

    public FakeLink Link { get; }

    public FakeService Service { get; }

    public FakeEngine Engine { get; }

    /// <summary>Lays down a version directory with a file in it, as a real install would.</summary>
    public void Install(string version)
    {
        var path = Layout.VersionPath(version);
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "MergePool.Service.dll"), version);
    }

    public UpgradeCoordinator Coordinator(UpgradeOptions? options = null) =>
        new(Layout, Link, Service, Engine, options ?? new UpgradeOptions { KeepVersions = 2 });

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    public sealed class FakeLink(InstallLayout layout) : ICurrentLinkManager
    {
        private string? _version;

        public int PointCount { get; private set; }

        public Func<string, bool>? FailOn { get; set; }

        public string? ReadCurrentVersion() => _version;

        public void PointAt(string version)
        {
            if (FailOn?.Invoke(version) == true)
            {
                throw new IOException($"Refusing to point at {version}.");
            }

            if (!layout.VersionExists(version))
            {
                throw new DirectoryNotFoundException(version);
            }

            _version = version;
            PointCount++;
        }
    }

    public sealed class FakeService : IServiceControl
    {
        public ServiceState State { get; private set; } = ServiceState.Running;

        public int StopCount { get; private set; }

        public int StartCount { get; private set; }

        public Exception? StartFailure { get; set; }

        public ServiceState GetState() => State;

        public Task StopAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            StopCount++;
            State = ServiceState.Stopped;
            return Task.CompletedTask;
        }

        public Task StartAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            StartCount++;
            if (StartFailure is not null)
            {
                var failure = StartFailure;
                StartFailure = null;
                throw failure;
            }

            State = ServiceState.Running;
            return Task.CompletedTask;
        }
    }

    public sealed class FakeEngine : IEngineControl
    {
        private readonly Queue<EngineHealth?> _healthResponses = new();

        public bool DrainSucceeds { get; set; } = true;

        public int DrainCount { get; private set; }

        public int HealthCheckCount { get; private set; }

        /// <summary>Health results served in order; the last one repeats.</summary>
        public void QueueHealth(params EngineHealth?[] responses)
        {
            foreach (var response in responses)
            {
                _healthResponses.Enqueue(response);
            }
        }

        public Task<bool> DrainAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            DrainCount++;
            return Task.FromResult(DrainSucceeds);
        }

        public Task<EngineHealth?> ResumeAndCheckAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            HealthCheckCount++;

            if (_healthResponses.Count == 0)
            {
                return Task.FromResult<EngineHealth?>(new EngineHealth(true, [], "unknown"));
            }

            var response = _healthResponses.Count == 1
                ? _healthResponses.Peek()
                : _healthResponses.Dequeue();

            return Task.FromResult(response);
        }
    }
}
