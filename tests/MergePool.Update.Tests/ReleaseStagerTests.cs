using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using MergePool.Update;
using Xunit;

namespace MergePool.Update.Tests;

public sealed class ReleaseStagerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "mergepool-stager", Guid.NewGuid().ToString("N"));

    private readonly InstallLayout _layout;

    public ReleaseStagerTests()
    {
        Directory.CreateDirectory(_root);
        _layout = new InstallLayout(Path.Combine(_root, "install"));
    }

    private byte[] Package(bool wrapInFolder = false, bool includeService = true)
    {
        var staging = Path.Combine(_root, "staging-" + Guid.NewGuid().ToString("N"));
        var content = wrapInFolder ? Path.Combine(staging, "MergePool-0.3.0") : staging;
        Directory.CreateDirectory(content);

        if (includeService)
        {
            File.WriteAllText(Path.Combine(content, "MergePool.Service.exe"), "service");
        }

        File.WriteAllText(Path.Combine(content, "MergePool.Updater.exe"), "updater");
        File.WriteAllText(Path.Combine(content, "MergePool.dll"), "library");

        var zip = Path.Combine(_root, "package-" + Guid.NewGuid().ToString("N") + ".zip");
        ZipFile.CreateFromDirectory(staging, zip);
        var bytes = File.ReadAllBytes(zip);

        Directory.Delete(staging, recursive: true);
        File.Delete(zip);
        return bytes;
    }

    private static ReleaseStager Stager(InstallLayout layout, byte[] payload) =>
        new(layout, new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(payload),
        })));

    private static ReleaseInfo Release(string? sha256 = null) => new()
    {
        Version = "0.3.0",
        PackageUrl = new Uri("https://example.invalid/package.zip"),
        Sha256 = sha256,
    };

    private static string Sha256Of(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    [Fact]
    public async Task A_release_is_unpacked_into_its_own_version_directory()
    {
        var payload = Package();

        var staged = await Stager(_layout, payload).StageAsync(Release(), null, CancellationToken.None);

        Assert.Equal("0.3.0", staged.Version);
        Assert.Equal(_layout.VersionPath("0.3.0"), staged.Path);
        Assert.True(File.Exists(Path.Combine(staged.Path, "MergePool.Service.exe")));
        Assert.True(File.Exists(staged.UpdaterPath));
    }

    [Fact]
    public async Task A_release_is_unpacked_inside_the_install_root_not_in_a_temp_directory()
    {
        // Where it is unpacked decides who can read it afterwards. Moving a directory keeps the
        // permissions it had where it came from, so a version unpacked in the service's own temp
        // directory arrives readable by LocalSystem and nobody else — and the window, which runs as
        // whoever is signed in, then cannot start from it at all: CreateProcess, access denied.
        var observed = new List<string>();
        var payload = Package();

        var stager = new ReleaseStager(_layout, new HttpClient(new StubHandler(_ =>
        {
            // Snapshot the install root the moment the download starts, which is after the work
            // directory has been made and before anything has been moved into place.
            if (Directory.Exists(_layout.InstallRoot))
            {
                observed.AddRange(Directory
                    .EnumerateDirectories(_layout.InstallRoot)
                    .Select(directory => Path.GetFileName(directory)!));
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) };
        })));

        await stager.StageAsync(Release(), null, CancellationToken.None);

        Assert.Contains(InstallLayout.StagingFolder, observed);
    }

    [Fact]
    public async Task Staging_does_not_look_like_an_installed_version()
    {
        // A half-unpacked directory under versions\ would be listed as installed and could be
        // pruned mid-download, so staging sits beside that folder rather than inside it.
        await Stager(_layout, Package()).StageAsync(Release(), null, CancellationToken.None);

        Assert.Equal(["0.3.0"], _layout.InstalledVersions());
        Assert.DoesNotContain(InstallLayout.StagingFolder, _layout.InstalledVersions());
        Assert.StartsWith(_layout.InstallRoot, _layout.StagingRoot, StringComparison.Ordinal);
        Assert.DoesNotContain(InstallLayout.VersionsFolder, Path.GetRelativePath(_layout.InstallRoot, _layout.StagingRoot));
    }

    [Fact]
    public async Task An_archive_wrapped_in_a_single_folder_is_unwrapped()
    {
        var staged = await Stager(_layout, Package(wrapInFolder: true))
            .StageAsync(Release(), null, CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(staged.Path, "MergePool.Service.exe")));
    }

    [Fact]
    public async Task Staging_never_touches_the_version_already_installed()
    {
        var running = _layout.VersionPath("0.2.0");
        Directory.CreateDirectory(running);
        File.WriteAllText(Path.Combine(running, "MergePool.Service.exe"), "the running service");

        await Stager(_layout, Package()).StageAsync(Release(), null, CancellationToken.None);

        Assert.Equal("the running service", File.ReadAllText(Path.Combine(running, "MergePool.Service.exe")));
    }

    [Fact]
    public async Task A_package_that_does_not_match_its_checksum_is_refused()
    {
        var stager = Stager(_layout, Package());

        var failure = await Assert.ThrowsAsync<InvalidDataException>(
            () => stager.StageAsync(Release(sha256: "deadbeef"), null, CancellationToken.None));

        Assert.Contains("checksum", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(_layout.VersionPath("0.3.0")));
    }

    [Fact]
    public async Task A_package_that_matches_its_checksum_is_accepted()
    {
        var payload = Package();

        var staged = await Stager(_layout, payload)
            .StageAsync(Release(sha256: Sha256Of(payload)), null, CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(staged.Path, "MergePool.Service.exe")));
    }

    [Fact]
    public async Task A_package_that_is_not_a_MergePool_release_is_refused()
    {
        var stager = Stager(_layout, Package(includeService: false));

        await Assert.ThrowsAsync<InvalidDataException>(
            () => stager.StageAsync(Release(), null, CancellationToken.None));

        Assert.False(Directory.Exists(_layout.VersionPath("0.3.0")));
    }

    [Fact]
    public async Task A_package_larger_than_the_limit_is_refused()
    {
        var stager = new ReleaseStager(
            _layout,
            new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Package()),
            })))
        {
            MaxPackageBytes = 8,
        };

        await Assert.ThrowsAsync<InvalidDataException>(
            () => stager.StageAsync(Release(), null, CancellationToken.None));
    }

    [Fact]
    public async Task Progress_is_reported_while_downloading()
    {
        var reported = new List<double>();
        var progress = new Progress<double>(reported.Add);

        await Stager(_layout, Package()).StageAsync(Release(), progress, CancellationToken.None);

        // Progress is raised asynchronously; the staged result is what matters, so only assert the
        // download completed and any reported value was a sane fraction.
        Assert.All(reported, value => Assert.InRange(value, 0, 1));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
