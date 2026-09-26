using MergePool.Core.Update;
using Xunit;

namespace MergePool.Core.Tests;

/// <summary>
/// A window pinned to a version directory is how "the app updated but the window did not" happens,
/// so every one of these is a real way that has to stop being possible.
/// </summary>
public sealed class VersionHandoffTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "mergepool-handoff-" + Guid.NewGuid().ToString("N"));

    private string Install(string version, string executable = "MergePool.exe")
    {
        var directory = Path.Combine(_root, VersionHandoff.VersionsFolder, version);
        Directory.CreateDirectory(directory);

        var path = Path.Combine(directory, executable);
        File.WriteAllText(path, "not really an executable");
        return path;
    }

    [Fact]
    public void Hands_over_to_a_newer_version()
    {
        var started = Install("0.4.0");
        var newer = Install("0.5.0");

        Assert.Equal(newer, VersionHandoff.NewerExecutable(started));
    }

    [Fact]
    public void Hands_over_to_the_newest_of_several()
    {
        var started = Install("0.4.0");
        Install("0.4.1");
        var newest = Install("0.10.0");
        Install("0.5.0");

        // Compared as versions, not as text: 0.10.0 beats 0.5.0, which sorts the other way.
        Assert.Equal(newest, VersionHandoff.NewerExecutable(started));
    }

    [Fact]
    public void Stays_put_when_it_is_already_the_newest()
    {
        Install("0.4.0");
        var started = Install("0.5.0");

        Assert.Null(VersionHandoff.NewerExecutable(started));
    }

    [Fact]
    public void Ignores_a_newer_version_whose_executable_is_missing()
    {
        var started = Install("0.4.0");
        Directory.CreateDirectory(Path.Combine(_root, VersionHandoff.VersionsFolder, "0.5.0"));

        Assert.Null(VersionHandoff.NewerExecutable(started));
    }

    [Fact]
    public void Ignores_directories_that_are_not_versions()
    {
        var started = Install("0.4.0");
        Install("scratch");

        Assert.Null(VersionHandoff.NewerExecutable(started));
    }

    [Fact]
    public void Hands_over_the_same_executable_name()
    {
        var started = Install("0.4.0", "MergePool.Service.exe");
        var newer = Install("0.5.0", "MergePool.Service.exe");
        Install("0.5.0");

        Assert.Equal(newer, VersionHandoff.NewerExecutable(started));
    }

    [Fact]
    public void Leaves_a_build_outside_the_versions_layout_alone()
    {
        var loose = Path.Combine(_root, "MergePool.exe");
        Directory.CreateDirectory(_root);
        File.WriteAllText(loose, "not really an executable");
        Install("9.9.9");

        Assert.Null(VersionHandoff.NewerExecutable(loose));
        Assert.Null(VersionHandoff.StableLaunchPath(loose));
    }

    [Fact]
    public void Leaves_a_build_started_through_the_link_alone()
    {
        // A build started through 'current' is already whatever the link points at, and reading
        // through the link to decide would defeat the point.
        var through = Path.Combine(_root, VersionHandoff.CurrentLink, "MergePool.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(through)!);
        File.WriteAllText(through, "not really an executable");
        Install("9.9.9");

        Assert.Null(VersionHandoff.NewerExecutable(through));
    }

    [Fact]
    public void Finds_the_version_independent_path_for_a_start_up_entry()
    {
        var started = Install("0.4.0");

        var current = Path.Combine(_root, VersionHandoff.CurrentLink);
        Directory.CreateDirectory(current);
        File.WriteAllText(Path.Combine(current, "MergePool.exe"), "not really an executable");

        Assert.Equal(Path.Combine(current, "MergePool.exe"), VersionHandoff.StableLaunchPath(started));
    }

    [Fact]
    public void Will_not_offer_a_link_path_that_does_not_resolve()
    {
        // The link being broken is precisely when a start-up entry must not be pointed at it.
        var started = Install("0.4.0");

        Assert.Null(VersionHandoff.StableLaunchPath(started));
    }

    [Fact]
    public void Finds_a_named_newer_version()
    {
        var started = Install("0.4.0");
        var newer = Install("0.4.1");

        Assert.Equal(newer, VersionHandoff.ExecutableForVersion(started, "0.4.1"));
    }

    [Fact]
    public void Will_not_move_backwards_or_sideways_to_a_named_version()
    {
        var started = Install("0.4.1");
        Install("0.4.0");

        Assert.Null(VersionHandoff.ExecutableForVersion(started, "0.4.0"));
        Assert.Null(VersionHandoff.ExecutableForVersion(started, "0.4.1"));
        Assert.Null(VersionHandoff.ExecutableForVersion(started, "not a version"));
        Assert.Null(VersionHandoff.ExecutableForVersion(started, null));
    }

    [Fact]
    public void Restarts_a_window_into_the_version_the_service_is_on()
    {
        var started = Install("0.4.0");
        var newer = Install("0.5.0");

        Assert.Equal(newer, VersionHandoff.RestartTargetForVersion(started, "0.5.0"));
    }

    [Fact]
    public void Restarts_a_window_launched_through_the_link_from_the_same_path()
    {
        // The link already points at the new version, so the path it was started from is the way
        // back in — and is the only path that still exists once the old version is pruned.
        var through = Path.Combine(_root, VersionHandoff.CurrentLink, "MergePool.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(through)!);
        File.WriteAllText(through, "not really an executable");

        Assert.Equal(through, VersionHandoff.RestartTargetForVersion(through, "0.5.0"));
    }

    [Fact]
    public void Has_nowhere_to_restart_a_window_that_is_already_current()
    {
        var started = Install("0.5.0");

        Assert.Null(VersionHandoff.RestartTargetForVersion(started, "0.5.0"));
        Assert.Null(VersionHandoff.RestartTargetForVersion(started, "0.4.0"));
        Assert.Null(VersionHandoff.RestartTargetForVersion(started, null));
    }

    [Fact]
    public void Has_nowhere_to_restart_a_development_build()
    {
        var loose = Path.Combine(_root, "MergePool.exe");
        Directory.CreateDirectory(_root);
        File.WriteAllText(loose, "not really an executable");

        Assert.Null(VersionHandoff.RestartTargetForVersion(loose, "9.9.9"));
    }

    [Fact]
    public void Says_nothing_about_a_path_it_cannot_read()
    {
        Assert.Null(VersionHandoff.NewerExecutable(null));
        Assert.Null(VersionHandoff.NewerExecutable(string.Empty));
        Assert.Null(VersionHandoff.NewerExecutable("   "));
        Assert.Null(VersionHandoff.StableLaunchPath(null));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
