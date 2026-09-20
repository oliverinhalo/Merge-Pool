using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace MergePool.Service;

/// <summary>
/// Creates the service's named pipe with an explicit ACL: interactive users may talk to the
/// service (that is how the UI works), and only administrators and the system may own it.
/// </summary>
[SupportedOSPlatform("windows")]
public static class PipeSecurityFactory
{
    public static NamedPipeServerStream Create(string pipeName, int maxInstances)
    {
        var security = new PipeSecurity();

        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var authenticated = new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null);

        security.SetOwner(administrators);
        security.AddAccessRule(new PipeAccessRule(system, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(administrators, PipeAccessRights.FullControl, AccessControlType.Allow));

        // The UI runs as the signed-in user and needs to send requests and read the replies.
        security.AddAccessRule(new PipeAccessRule(
            authenticated,
            PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance,
            AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(
            pipeName,
            PipeDirection.InOut,
            maxInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: 0,
            outBufferSize: 0,
            security);
    }
}
