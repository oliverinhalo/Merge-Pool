using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;

namespace MergePool.Update;

public sealed record StagedRelease
{
    public required string Version { get; init; }

    /// <summary>The version directory the files were unpacked into.</summary>
    public required string Path { get; init; }

    /// <summary>The executable an upgrade will run to swap the <c>current</c> link.</summary>
    public required string UpdaterPath { get; init; }
}

/// <summary>
/// Downloads a release and unpacks it into its own version directory, ready for
/// <see cref="UpgradeCoordinator"/> to make current.
/// </summary>
/// <remarks>
/// <para>Nothing existing is overwritten. The files land in <c>versions\{version}</c>, which is a
/// directory no other version shares, and the running version is not touched at all — that is the
/// whole point of the side-by-side layout. Pool data and the configuration live outside the install
/// root and are never even opened here.</para>
/// <para>The archive is unpacked into a staging directory first and only moved into place once it is
/// complete and contains a service executable, so an interrupted download cannot leave a
/// half-populated version directory that looks installable.</para>
/// </remarks>
public sealed class ReleaseStager(InstallLayout layout, HttpClient http)
{
    private const string ServiceExecutable = "MergePool.Service.exe";
    private const string UpdaterExecutable = "MergePool.Updater.exe";

    private readonly InstallLayout _layout = layout ?? throw new ArgumentNullException(nameof(layout));
    private readonly HttpClient _http = http ?? throw new ArgumentNullException(nameof(http));

    /// <summary>Largest package accepted, as a guard against a wrong or hostile URL.</summary>
    public long MaxPackageBytes { get; init; } = 512L * 1024 * 1024;

    public async Task<StagedRelease> StageAsync(
        ReleaseInfo release,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(release);

        var versionPath = _layout.VersionPath(release.Version);
        if (Directory.Exists(versionPath) && File.Exists(Path.Combine(versionPath, ServiceExecutable)))
        {
            // Already downloaded, e.g. by an earlier attempt that failed later on.
            return Describe(release.Version, versionPath);
        }

        var work = Path.Combine(Path.GetTempPath(), "MergePool.Update", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var archive = Path.Combine(work, "package.zip");

        try
        {
            await DownloadAsync(release, archive, progress, cancellationToken).ConfigureAwait(false);
            VerifyDigest(archive, release.Sha256);

            var unpacked = Path.Combine(work, "unpacked");
            ZipFile.ExtractToDirectory(archive, unpacked);

            var root = FindPayloadRoot(unpacked)
                ?? throw new InvalidDataException(
                    $"The downloaded package does not contain {ServiceExecutable}, so it is not a MergePool release.");

            Directory.CreateDirectory(_layout.VersionsRoot);

            // Move into place last. Until this succeeds there is no version directory to find.
            if (Directory.Exists(versionPath))
            {
                Directory.Delete(versionPath, recursive: true);
            }

            Directory.Move(root, versionPath);
            return Describe(release.Version, versionPath);
        }
        finally
        {
            TryDeleteDirectory(work);
        }
    }

    private async Task DownloadAsync(
        ReleaseInfo release,
        string target,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        using var response = await _http
            .GetAsync(release.PackageUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        var expected = response.Content.Headers.ContentLength ?? release.PackageBytes;
        if (expected > MaxPackageBytes)
        {
            throw new InvalidDataException(string.Create(
                CultureInfo.InvariantCulture,
                $"The update package is {expected:N0} bytes, which is larger than the {MaxPackageBytes:N0} byte limit."));
        }

        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None);

        var buffer = new byte[1 << 20];
        long total = 0;
        int read;

        while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            total += read;

            if (total > MaxPackageBytes)
            {
                throw new InvalidDataException("The update package is larger than the download limit.");
            }

            if (expected > 0)
            {
                progress?.Report(Math.Clamp(total / (double)expected, 0, 1));
            }
        }

        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Checks the package against the digest the release published. A release without one is still
    /// installable — it came from the same HTTPS host — but a digest that does not match never is.
    /// </summary>
    private static void VerifyDigest(string archivePath, string? expected)
    {
        if (string.IsNullOrWhiteSpace(expected))
        {
            return;
        }

        using var stream = File.OpenRead(archivePath);
        var actual = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();

        if (!string.Equals(actual, expected.Trim().ToLowerInvariant(), StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The downloaded update does not match its published checksum, so it was discarded.");
        }
    }

    /// <summary>
    /// Finds the folder holding the service executable. Release archives are sometimes wrapped in a
    /// single top-level folder, so one level down is checked too.
    /// </summary>
    private static string? FindPayloadRoot(string unpacked)
    {
        if (File.Exists(Path.Combine(unpacked, ServiceExecutable)))
        {
            return unpacked;
        }

        foreach (var directory in Directory.EnumerateDirectories(unpacked))
        {
            if (File.Exists(Path.Combine(directory, ServiceExecutable)))
            {
                return directory;
            }
        }

        return null;
    }

    private static StagedRelease Describe(string version, string path) => new()
    {
        Version = version,
        Path = path,
        UpdaterPath = Path.Combine(path, UpdaterExecutable),
    };

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}
