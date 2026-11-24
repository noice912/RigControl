using System.Security.AccessControl;
using System.Security.Principal;

namespace RigControl.Bundled;

/// <summary>
/// Answers one question about a file or folder: could a program WITHOUT administrator rights replace it, or one of the
/// folders it sits in? Looks at who owns each of them and who Windows lets write there. Anything that can't be read
/// counts as "yes". (RigControl.Lighting has the same check for the OpenRGB it starts; the two projects share no code.)
/// </summary>
public static class FolderGuard
{
    // Write, append, delete, change permissions, take ownership (and the "generic" forms of the same).
    private const int ChangeRights = 0x2 | 0x4 | 0x10 | 0x40 | 0x100 | 0x10000 | 0x40000 | 0x80000 | 0x40000000 | 0x10000000;
    // On the drive's top folder only "delete what is inside" and the permission rights matter.
    private const int RootRights = 0x40 | 0x40000 | 0x80000 | 0x10000000;

    private static readonly SecurityIdentifier[] Trusted =
    {
        new(WellKnownSidType.LocalSystemSid, null),
        new(WellKnownSidType.BuiltinAdministratorsSid, null),
        new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464"), // TrustedInstaller
    };

    /// <summary>True when ordinary programs can change this file or folder (which must exist), or anything above it.</summary>
    public static bool OthersCanChange(string path) => FirstOpen(path) is not null;

    /// <summary>
    /// The file or folder on this path that ordinary programs can change: the one nearest the drive when there are
    /// several (putting that one right is what helps), or the path itself when it can't be checked at all. Null when
    /// nothing on the path is open.
    /// </summary>
    public static string? FirstOpen(string path)
    {
        string full;
        try { full = Path.GetFullPath(path); }
        catch { return path; }
        try
        {
            var root = Path.GetPathRoot(full);
            if (string.IsNullOrEmpty(root) || full.StartsWith(@"\\", StringComparison.Ordinal)) return full;
            // Only NTFS and ReFS keep permissions at all.
            var format = new DriveInfo(root).DriveFormat;
            if (!format.Equals("NTFS", StringComparison.OrdinalIgnoreCase) && !format.Equals("ReFS", StringComparison.OrdinalIgnoreCase)) return full;

            string? open = null;
            DirectoryInfo? dir;
            if (Directory.Exists(full))
            {
                dir = new DirectoryInfo(full);
            }
            else
            {
                var info = new FileInfo(full);
                if (!info.Exists) return full;
                if (info.Attributes.HasFlag(FileAttributes.ReparsePoint) || Open(info.GetAccessControl(), ChangeRights, checkOwner: true)) open = full;
                dir = info.Directory;
            }
            for (; dir is not null; dir = dir.Parent)
            {
                var top = dir.Parent is null;
                // A link could point the whole path somewhere else.
                if ((!top && dir.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    || Open(dir.GetAccessControl(), top ? RootRights : ChangeRights, checkOwner: !top))
                    open = dir.FullName;
            }
            return open;
        }
        catch
        {
            return full;
        }
    }

    /// <summary>True when the owner, or anyone Windows allows one of these rights, is not SYSTEM, Administrators or TrustedInstaller.</summary>
    internal static bool Open(FileSystemSecurity security, int rights, bool checkOwner)
    {
        if (checkOwner && (security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner || !Trusted.Contains(owner))) return true;
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow) continue;
            if (rule.PropagationFlags.HasFlag(PropagationFlags.InheritOnly)) continue; // only for things created inside later
            if (((int)rule.FileSystemRights & rights) == 0) continue;
            if (rule.IdentityReference is SecurityIdentifier sid && Trusted.Contains(sid)) continue;
            return true;
        }
        return false;
    }
}
