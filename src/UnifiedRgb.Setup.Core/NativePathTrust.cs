using System.Security.AccessControl;

namespace UnifiedRgb.Setup;

/// <summary>One allow/deny entry of a file or directory DACL, reduced to what the trust decision needs.</summary>
public sealed record NativeAce(string Sid, bool Allow, FileSystemRights Rights, bool InheritOnly);

/// <summary>
/// Trust policy for native libraries and modules setup refers to: only administrators, SYSTEM and TrustedInstaller may be able
/// to change the file or replace/rename any directory above it. A user-writable location is refused even when the hash matches,
/// because the file could be swapped after the check.
/// </summary>
public static class NativePathTrust
{
    public const string LocalSystem = "S-1-5-18";
    public const string Administrators = "S-1-5-32-544";
    public const string TrustedInstaller = "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";
    public const string CreatorOwner = "S-1-3-0";

    private const FileSystemRights GenericAll = (FileSystemRights)0x10000000;
    private const FileSystemRights GenericWrite = (FileSystemRights)0x40000000;

    /// <summary>Rights on the file itself that allow altering or replacing its content.</summary>
    public const FileSystemRights FileWriteRights = FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.Delete
        | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership | GenericAll | GenericWrite;

    /// <summary>Rights on an ancestor directory that allow deleting/renaming what is below it or re-permissioning it.</summary>
    public const FileSystemRights DirectoryWriteRights = FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.Delete
        | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership | GenericAll;

    public static bool IsTrustedSid(string sid) => sid is LocalSystem or Administrators or TrustedInstaller;

    /// <summary>
    /// Evaluates one object's owner and DACL. <paramref name="isDirectory"/> selects the dangerous-rights set; a drive root's own
    /// Delete right is meaningless and ignored. Returns null when trusted, else the reason.
    /// </summary>
    public static string? Evaluate(string path, string? ownerSid, IEnumerable<NativeAce> aces, bool isDirectory, bool isRoot)
    {
        ArgumentNullException.ThrowIfNull(aces);
        if (ownerSid is null || !IsTrustedSid(ownerSid))
        {
            return $"'{path}' is owned by {ownerSid ?? "an unknown principal"}, not by an administrative principal.";
        }

        var dangerous = isDirectory ? DirectoryWriteRights : FileWriteRights;
        if (isRoot)
        {
            dangerous &= ~FileSystemRights.Delete;
        }

        foreach (var ace in aces)
        {
            if (!ace.Allow || ace.InheritOnly || IsTrustedSid(ace.Sid) || ace.Sid == CreatorOwner)
            {
                continue;
            }

            if ((ace.Rights & dangerous) != 0)
            {
                return $"'{path}' grants {ace.Sid} rights {ace.Rights & dangerous}; it is not an administrator-only location.";
            }
        }

        return null;
    }
}
