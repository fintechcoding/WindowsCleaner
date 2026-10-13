using Cleaner.Core;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Cleaner.Service;

internal static class SecureStorage
{
    public static void Ensure()
    {
        var directory = new DirectoryInfo(Product.DataDirectory);
        var parent = directory.Parent!.FullName;
        if (SafeCleanup.HasLinkedAncestor(parent) || (directory.Exists && SafeCleanup.HasLinkedAncestor(directory.FullName)))
            throw new IOException("The service settings directory must not be linked.");
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null), FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        security.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        if (!directory.Exists) directory.Create(security);
        else
        {
            directory.SetAccessControl(security);
            foreach (var file in directory.GetFiles())
            {
                if ((file.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked settings files are not allowed.");
                var fileSecurity = new FileSecurity();
                fileSecurity.SetAccessRuleProtection(true, false);
                foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
                    fileSecurity.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null), FileSystemRights.FullControl, AccessControlType.Allow));
                file.SetAccessControl(fileSecurity);
            }
        }
    }
}
