using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;

namespace UnifiedRgb.Firmware.ProtectedRuntime;

public sealed class WindowsFirmwareRuntimeElevation : IFirmwareRuntimeElevation
{
    public bool IsAdministratorOrSystem
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return identity.User?.Value == FirmwareRuntimeSecurity.System ||
                new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }
}

/// <summary>Not invoked by checks. No ACL repair, overwrite, deletion, process, or device APIs.</summary>
public sealed partial class WindowsFirmwareRuntimeFileSystem : IFirmwareRuntimeFileSystem
{
    public RuntimeEntry? Inspect(string path, bool security)
    {
        FileAttributes attributes;
        try { attributes = File.GetAttributes(path); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        var directory = (attributes & FileAttributes.Directory) != 0;
        var reparse = (attributes & FileAttributes.ReparsePoint) != 0;
        RuntimeSecurity? descriptor = null;
        if (security && !reparse)
        {
            FileSystemSecurity acl = directory
                ? new DirectoryInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access)
                : new FileInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
            var rules = acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
                .Select(x => new RuntimeAccessRule(x.IdentityReference.Value, x.FileSystemRights, x.AccessControlType,
                    x.IsInherited, x.InheritanceFlags, x.PropagationFlags)).ToArray();
            FirmwareRuntimeSecurity.ValidateNativeAceShape(acl, rules.Length);
            descriptor = new(acl.GetOwner(typeof(SecurityIdentifier))?.Value ?? "", acl.AreAccessRulesProtected, rules);
        }
        return new(directory, reparse, descriptor);
    }

    public IEnumerable<string> EnumerateChildren(string directory) => Directory.EnumerateFileSystemEntries(directory);

    public byte[] ReadLockedSnapshot(string path, int maximum)
    {
        RequireNoReparse(path);
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > maximum) throw new InvalidDataException("Locked runtime snapshot bound: " + Path.GetFileName(path));
        var bytes = new byte[checked((int)input.Length)]; input.ReadExactly(bytes);
        if (input.ReadByte() != -1) throw new InvalidDataException("Locked snapshot length changed");
        RequireNoReparse(path);
        return bytes;
    }

    public void CreateProtectedDirectory(string path)
    {
        RequireTarget(path, true);
        var security = new DirectorySecurity(); Apply(security, true);
        var binary = security.GetSecurityDescriptorBinaryForm();
        var descriptor = Marshal.AllocHGlobal(binary.Length);
        try
        {
            Marshal.Copy(binary, 0, descriptor, binary.Length);
            var attributes = new SecurityAttributes
            {
                Length = Marshal.SizeOf<SecurityAttributes>(), SecurityDescriptor = descriptor,
            };
            // Unlike Directory.CreateDirectory, ERROR_ALREADY_EXISTS is a failure, including the root race.
            if (!CreateDirectory(path, ref attributes)) throw new Win32Exception(Marshal.GetLastPInvokeError(), "Exclusive protected directory creation failed; existing objects are never modified");
        }
        finally { Marshal.FreeHGlobal(descriptor); }
    }

    public void CreateProtectedFile(string path, byte[] verifiedBytes)
    {
        RequireTarget(path, false);
        var security = new FileSecurity(); Apply(security, false);
        using var output = new FileInfo(path).Create(FileMode.CreateNew, FileSystemRights.Write, FileShare.None,
            4096, FileOptions.WriteThrough, security);
        output.Write(verifiedBytes); output.Flush(true);
    }

    private void RequireTarget(string path, bool directory)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetFullPath(FirmwareRuntimeResolver.LiveDirectory);
        if (full != root)
            FirmwareFiles.Relative(Path.GetRelativePath(root, full).Replace('\\', '/'));
        else if (!directory) throw new InvalidDataException("The fixed runtime root must be a directory");
        RequireNoReparse(full);
        if (Inspect(full, false) is not null) throw new IOException("Protected target already exists; no repair or overwrite");
        var parent = Path.GetDirectoryName(full)!;
        var entry = Inspect(parent, full != root) ?? throw new InvalidDataException("Protected parent must already exist");
        if (!entry.Directory) throw new InvalidDataException("Protected parent must be a directory");
        if (full != root) FirmwareRuntimeSecurity.Validate(entry);
    }

    private void RequireNoReparse(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if (Inspect(current, false)?.Reparse == true) throw new InvalidDataException("Reparse paths forbidden");
    }

    private static void Apply(FileSystemSecurity security, bool directory)
    {
        security.SetOwner(new SecurityIdentifier(FirmwareRuntimeSecurity.Administrators));
        security.SetAccessRuleProtection(true, false);
        foreach (var rule in FirmwareRuntimeSecurity.Required(directory).Rules)
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(rule.Sid), rule.Rights,
                rule.Inheritance, rule.Propagation, rule.Type));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        internal int Length;
        internal nint SecurityDescriptor;
        internal int InheritHandle;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateDirectoryW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateDirectory(string path, ref SecurityAttributes securityAttributes);
}
