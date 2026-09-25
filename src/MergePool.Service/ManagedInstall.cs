using System.Runtime.Versioning;
using MergePool.Update;

namespace MergePool.Service;

/// <summary>
/// Works out whether this build is running from a managed side-by-side install, and where its root
/// is. Only such an install can update itself, because only it has a <c>current</c> link to repoint.
/// </summary>
[SupportedOSPlatform("windows")]
public static class ManagedInstall
{
    /// <summary>
    /// The folder holding <c>versions\</c> and <c>current</c>, or <c>null</c> when the binaries are
    /// somewhere else entirely — a development run, or a plain copy of the output folder.
    /// </summary>
    public static string? FindRoot(string? startFrom = null)
    {
        var directory = new DirectoryInfo(startFrom ?? AppContext.BaseDirectory);

        // current\ -> install root, or versions\<version>\ -> install root: at most three levels up.
        for (var depth = 0; depth < 4 && directory is not null; depth++, directory = directory.Parent)
        {
            var candidate = directory.Parent;
            if (candidate is null)
            {
                continue;
            }

            if (Directory.Exists(Path.Combine(candidate.FullName, InstallLayout.VersionsFolder)))
            {
                return candidate.FullName;
            }
        }

        return null;
    }
}
