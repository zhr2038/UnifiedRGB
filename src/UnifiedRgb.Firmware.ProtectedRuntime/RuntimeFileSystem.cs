using System.Security.AccessControl;

namespace UnifiedRgb.Firmware.ProtectedRuntime;

public sealed record RuntimeAccessRule(string Sid, FileSystemRights Rights, AccessControlType Type,
    bool Inherited = false, InheritanceFlags Inheritance = InheritanceFlags.None,
    PropagationFlags Propagation = PropagationFlags.None);
public sealed record RuntimeSecurity(string OwnerSid, bool DaclProtected, IReadOnlyList<RuntimeAccessRule> Rules);
public sealed record RuntimeEntry(bool Directory, bool Reparse, RuntimeSecurity? Security = null);

/// <summary>Fakeable filesystem only. Implementations must not hide access errors as absence.</summary>
public interface IFirmwareRuntimeFileSystem
{
    RuntimeEntry? Inspect(string path, bool security);
    IEnumerable<string> EnumerateChildren(string directory);
    byte[] ReadLockedSnapshot(string path, int maximum);
    void CreateProtectedDirectory(string path);
    void CreateProtectedFile(string path, byte[] verifiedBytes);
}

public interface IFirmwareRuntimeElevation
{
    bool IsAdministratorOrSystem { get; }
}

public static class FirmwareRuntimeSecurity
{
    public const string Administrators = "S-1-5-32-544";
    public const string System = "S-1-5-18";
    public const string Users = "S-1-5-32-545";

    /// <summary>Do not let ACL rule projection silently hide opaque/callback/object ACEs.</summary>
    public static void ValidateNativeAceShape(FileSystemSecurity security, int parsedRuleCount)
    {
        var raw = new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(), 0);
        if (!security.AreAccessRulesCanonical || raw.DiscretionaryAcl is not { } dacl || dacl.Count != parsedRuleCount ||
            dacl.Cast<GenericAce>().Any(ace => ace is not CommonAce { AceQualifier: AceQualifier.AccessAllowed, IsCallback: false }))
            throw new InvalidDataException("Protected runtime ACL contains unsupported or unprojected ACEs");
    }

    public static RuntimeSecurity Required(bool directory) => new(Administrators, true,
    [
        Rule(Administrators, FileSystemRights.FullControl, directory),
        Rule(System, FileSystemRights.FullControl, directory),
        Rule(Users, FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize, directory),
    ]);

    private static RuntimeAccessRule Rule(string sid, FileSystemRights rights, bool directory) =>
        new(sid, rights, AccessControlType.Allow, Inheritance: directory
            ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit : InheritanceFlags.None);

    public static void Validate(RuntimeEntry entry)
    {
        var security = entry.Security ?? throw new InvalidDataException("Protected runtime ACL missing");
        if (security.OwnerSid is not (Administrators or System) || !security.DaclProtected || security.Rules.Count != 3)
            throw new InvalidDataException("Protected runtime owner/DACL is untrusted");
        var required = Required(entry.Directory);
        foreach (var rule in required.Rules)
            if (security.Rules.Count(x => x == rule) != 1)
                throw new InvalidDataException("Protected runtime requires only explicit Admins/System full and Users read/execute ACLs");
    }
}
