using System.Xml.Linq;
using Xunit;

namespace MergePool.Core.Tests;

/// <summary>
/// Guards build properties that break the app at runtime rather than at compile time. CI compiles
/// the WPF UI but never launches it, so nothing else in the suite would notice these.
/// </summary>
public sealed class BuildConfigurationTests
{
    [Fact]
    public void InvariantGlobalization_is_never_enabled()
    {
        // WPF needs real culture data. Under invariant globalization it cannot resolve the UI
        // language and throws "Cannot find non-neutral culture related to 'en-us'." while the
        // window is being built, taking the whole UI down before it can show anything.
        foreach (var file in EnumerateBuildFiles())
        {
            var enabled = XDocument.Load(file)
                .Descendants()
                .Where(element => element.Name.LocalName == "InvariantGlobalization")
                .Select(element => element.Value.Trim())
                .Any(value => string.Equals(value, "true", StringComparison.OrdinalIgnoreCase));

            Assert.False(enabled, $"{Path.GetFileName(file)} enables InvariantGlobalization, which breaks the WPF UI.");
        }
    }

    [Fact]
    public void The_installer_version_matches_the_build_version()
    {
        // They are declared in two places. If they drift, the installer lays the binaries down in
        // versions\<installer version> and registers the service against a folder built for a
        // different one, which only shows up as a service that will not start.
        var root = RepositoryRoot();

        var build = XDocument.Load(Path.Combine(root, "Directory.Build.props"))
            .Descendants()
            .First(element => element.Name.LocalName == "VersionPrefix")
            .Value
            .Trim();

        var installer = File
            .ReadLines(Path.Combine(root, "installer", "MergePool.iss"))
            .Select(line => line.Trim())
            .First(line => line.StartsWith("#define AppVersion", StringComparison.Ordinal))
            .Split('"')[1];

        Assert.Equal(build, installer);
    }

    private static IEnumerable<string> EnumerateBuildFiles()
    {
        var root = RepositoryRoot();

        return Directory
            .EnumerateFiles(root, "*.props", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(Path.Combine(root, "src"), "*.csproj", SearchOption.AllDirectories))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
    }

    /// <summary>Walks up from the test binaries until the solution file turns up.</summary>
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "MergePool.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate the repository root from the test output directory.");
    }
}
