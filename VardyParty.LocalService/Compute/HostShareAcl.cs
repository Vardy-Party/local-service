using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace VardyParty.LocalService.Compute;

/// <summary>
/// Owner-only access for the host-share file and its key directory.
/// </summary>
public static class HostShareAcl
{
    public static void RestrictToCurrentUser(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || (!File.Exists(path) && !Directory.Exists(path)))
        {
            return;
        }

        if (OperatingSystem.IsWindows())
        {
            RestrictWindows(path);
            return;
        }

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            RestrictUnix(path);
        }
    }

    [SupportedOSPlatform("windows")]
    private static void RestrictWindows(string path)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User;
        if (sid is null)
        {
            return;
        }

        if (Directory.Exists(path))
        {
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(
                sid,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
            new DirectoryInfo(path).SetAccessControl(security);
            return;
        }

        var fileSecurity = new FileSecurity();
        fileSecurity.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        fileSecurity.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(fileSecurity);
    }

    [UnsupportedOSPlatform("windows")]
    private static void RestrictUnix(string path)
    {
        var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        if (Directory.Exists(path))
        {
            mode |= UnixFileMode.UserExecute;
        }

        File.SetUnixFileMode(path, mode);
    }
}
