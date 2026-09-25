using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace MergePool.Update;

/// <summary>A release the update machinery could install.</summary>
public sealed record ReleaseInfo
{
    /// <summary>Normalised version, e.g. <c>0.3.0</c> — the <c>v</c> prefix of a tag is stripped.</summary>
    public required string Version { get; init; }

    public required Uri PackageUrl { get; init; }

    public long PackageBytes { get; init; }

    /// <summary>Lowercase hex SHA-256 of the package, when the release publishes one.</summary>
    public string? Sha256 { get; init; }

    public string? Notes { get; init; }

    public Uri? ReleaseUrl { get; init; }

    public bool IsNewerThan(string installedVersion) => Compare(Version, installedVersion) > 0;

    /// <summary>
    /// Compares two versions. Anything unparseable sorts below anything parseable rather than
    /// throwing, so one malformed tag in the feed cannot break checking for updates.
    /// </summary>
    public static int Compare(string? left, string? right)
    {
        var leftParsed = TryParse(left, out var a);
        var rightParsed = TryParse(right, out var b);

        return (leftParsed, rightParsed) switch
        {
            (true, true) => a.CompareTo(b),
            (true, false) => 1,
            (false, true) => -1,
            _ => string.Compare(left, right, StringComparison.OrdinalIgnoreCase),
        };
    }

    public static bool TryParse(string? value, out System.Version version)
    {
        version = new System.Version(0, 0);
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        if (trimmed.StartsWith('v') || trimmed.StartsWith('V'))
        {
            trimmed = trimmed[1..];
        }

        // Drop any pre-release or build suffix: 0.3.0-beta.1 is treated as 0.3.0 for ordering.
        var cut = trimmed.IndexOfAny(['-', '+']);
        if (cut >= 0)
        {
            trimmed = trimmed[..cut];
        }

        return System.Version.TryParse(trimmed, out version!);
    }
}

public sealed record ReleaseFeedOptions
{
    /// <summary>GitHub repository the releases come from.</summary>
    public string Owner { get; init; } = "oliverinhalo";

    public string Repository { get; init; } = "Merge-Pool";

    /// <summary>Include pre-releases. Off by default: a stable machine should get stable builds.</summary>
    public bool IncludePrereleases { get; init; }

    /// <summary>
    /// Marks the asset holding the version's files. The asset is a plain zip of what goes into
    /// <c>versions\{version}</c>, not an installer, so applying it never runs a second setup program.
    /// </summary>
    public string PackageAssetSuffix { get; init; } = "-windows-x64.zip";

    public string ChecksumAssetName { get; init; } = "SHA256SUMS.txt";

    public Uri BaseAddress { get; init; } = new("https://api.github.com/");
}

/// <summary>Where the updater looks for new versions.</summary>
public interface IReleaseFeed
{
    Task<ReleaseInfo?> GetLatestAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Reads the repository's GitHub releases. Only ever a GET: this never authenticates, never posts,
/// and a failure is reported rather than thrown at the caller's tick.
/// </summary>
public sealed class GitHubReleaseFeed(HttpClient http, ReleaseFeedOptions? options = null) : IReleaseFeed
{
    private readonly HttpClient _http = http ?? throw new ArgumentNullException(nameof(http));
    private readonly ReleaseFeedOptions _options = options ?? new ReleaseFeedOptions();

    public async Task<ReleaseInfo?> GetLatestAsync(CancellationToken cancellationToken)
    {
        var path = string.Create(
            CultureInfo.InvariantCulture,
            $"repos/{_options.Owner}/{_options.Repository}/releases?per_page=20");

        var releases = await _http
            .GetFromJsonAsync<List<GitHubRelease>>(new Uri(_options.BaseAddress, path), cancellationToken)
            .ConfigureAwait(false);

        if (releases is null)
        {
            return null;
        }

        var candidates = releases
            .Where(release => !release.Draft)
            .Where(release => _options.IncludePrereleases || !release.Prerelease)
            .Select(Describe)
            .Where(release => release is not null)
            .OrderByDescending(release => release!.Version, Comparer<string>.Create(ReleaseInfo.Compare))
            .ToList();

        return candidates.FirstOrDefault();
    }

    private ReleaseInfo? Describe(GitHubRelease release)
    {
        if (string.IsNullOrWhiteSpace(release.TagName) || !ReleaseInfo.TryParse(release.TagName, out var version))
        {
            return null;
        }

        var package = release.Assets.FirstOrDefault(asset =>
            asset.Name.EndsWith(_options.PackageAssetSuffix, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(asset.BrowserDownloadUrl));

        if (package is null || !Uri.TryCreate(package.BrowserDownloadUrl, UriKind.Absolute, out var url))
        {
            // A release with no package for this platform is simply not installable from here.
            return null;
        }

        return new ReleaseInfo
        {
            Version = version.ToString(3),
            PackageUrl = url,
            PackageBytes = package.Size,
            Sha256 = NormaliseDigest(package.Digest),
            Notes = release.Body,
            ReleaseUrl = Uri.TryCreate(release.HtmlUrl, UriKind.Absolute, out var htmlUrl) ? htmlUrl : null,
        };
    }

    /// <summary>GitHub reports asset digests as <c>sha256:hex</c>. Anything else is ignored.</summary>
    private static string? NormaliseDigest(string? digest)
    {
        if (string.IsNullOrWhiteSpace(digest))
        {
            return null;
        }

        const string prefix = "sha256:";
        return digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? digest[prefix.Length..].Trim().ToLowerInvariant()
            : null;
    }

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; set; }

        [JsonPropertyName("draft")]
        public bool Draft { get; set; }

        [JsonPropertyName("prerelease")]
        public bool Prerelease { get; set; }

        [JsonPropertyName("body")]
        public string? Body { get; set; }

        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; set; }

        [JsonPropertyName("assets")]
        public List<GitHubAsset> Assets { get; set; } = [];
    }

    private sealed class GitHubAsset
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("size")]
        public long Size { get; set; }

        [JsonPropertyName("browser_download_url")]
        public string? BrowserDownloadUrl { get; set; }

        [JsonPropertyName("digest")]
        public string? Digest { get; set; }
    }
}
