using System.Runtime.Versioning;
using MergePool.Update;

namespace MergePool.Updater;

/// <summary>
/// Implements the <c>current</c> link as a directory junction. Repointing it replaces one
/// directory entry; the version directories themselves are never modified, which is what makes a
/// rollback safe.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsCurrentLinkManager(InstallLayout layout) : ICurrentLinkManager
{
    public string? ReadCurrentVersion()
    {
        var current = layout.CurrentPath;
        if (!Directory.Exists(current))
        {
            return null;
        }

        var info = new DirectoryInfo(current);
        var target = info.LinkTarget ?? info.FullName;

        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(target));
        return string.IsNullOrEmpty(name) ? null : name;
    }

    public void PointAt(string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);

        var target = layout.VersionPath(version);
        if (!Directory.Exists(target))
        {
            throw new DirectoryNotFoundException($"Version directory '{target}' does not exist.");
        }

        var current = layout.CurrentPath;

        // Build the new link beside the old one, then swap, so a crash mid-way leaves either the
        // old link or the new one — never a missing 'current'.
        var staging = current + ".new";
        RemoveLink(staging);

        // A directory symbolic link, which administrators may create; the installer uses a junction
        // for the same path. Both resolve identically and both are read back through LinkTarget.
        Directory.CreateSymbolicLink(staging, target);

        if (Directory.Exists(current))
        {
            var retired = current + ".old";
            RemoveLink(retired);
            Directory.Move(current, retired);

            try
            {
                Directory.Move(staging, current);
            }
            catch
            {
                Directory.Move(retired, current);
                throw;
            }

            RemoveLink(retired);
            return;
        }

        Directory.Move(staging, current);
    }

    /// <summary>
    /// Deletes a link without following it: the version directory behind it must survive.
    /// </summary>
    private static void RemoveLink(string path)
    {
        if (!Directory.Exists(path) && !File.Exists(path))
        {
            return;
        }

        var info = new DirectoryInfo(path);
        if (info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            info.Delete();
            return;
        }

        // A real directory where a link was expected: a previous install left it behind.
        Directory.Delete(path, recursive: true);
    }
}
