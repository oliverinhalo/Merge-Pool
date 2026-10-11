using System.Runtime.Versioning;
using System.Security.AccessControl;

namespace MergePool.Update;

/// <summary>
/// Puts a directory back to inheriting its permissions from its parent.
/// </summary>
/// <remarks>
/// <para>Needed because of how a move works on NTFS. Moving a directory keeps the permissions it
/// had where it came from — the entries it inherited from its old parent travel with it and the new
/// parent's are not applied. A version unpacked in the service's own temp directory therefore
/// arrives in the install root readable by LocalSystem and administrators and nobody else, and the
/// window, which runs as whoever is signed in, cannot even start from it: CreateProcess fails with
/// "Access is denied".</para>
/// <para>Versions are staged inside the install root now, so they inherit the right permissions from
/// the start. This exists to repair the ones an older build left behind, and as a guard for the
/// fallback path when the install root cannot be staged in.</para>
/// </remarks>
public static class DirectoryPermissions
{
    /// <summary>
    /// Drops every explicit rule on a directory and lets it inherit from its parent again. Returns
    /// whether anything was changed; false on a platform without Windows ACLs, or when the caller is
    /// not allowed to rewrite them.
    /// </summary>
    public static bool ResetToInherited(string path)
    {
        if (!OperatingSystem.IsWindows() || !Directory.Exists(path))
        {
            return false;
        }

        return ResetOnWindows(path);
    }

    [SupportedOSPlatform("windows")]
    private static bool ResetOnWindows(string path)
    {
        try
        {
            var directory = new DirectoryInfo(path);
            var security = directory.GetAccessControl(AccessControlSections.Access);

            // Protection off, inherited entries not preserved: Windows then recomputes this
            // directory's permissions from its parent and propagates them down.
            security.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);
            directory.SetAccessControl(security);
            return true;
        }
        catch (Exception exception) when (exception
            is UnauthorizedAccessException
            or PrivilegeNotHeldException
            or IOException
            or PlatformNotSupportedException)
        {
            // Not being allowed to fix the permissions is not a reason to fail an update: the files
            // are there and the service can read them. The window says what is wrong if it cannot.
            return false;
        }
    }
}
