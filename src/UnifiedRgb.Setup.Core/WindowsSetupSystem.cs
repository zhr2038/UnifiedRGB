using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using UnifiedRgb.Transports.Hid;
using UnifiedRgb.Transports.Smbus;
using UnifiedRgb.Transports.WinUsb;

namespace UnifiedRgb.Setup;

/// <summary>The real, read-only Windows backend of <see cref="ISetupSystem"/>, built on the existing transports.</summary>
public sealed partial class WindowsSetupSystem : ISetupSystem
{
    /// <summary>Upper bound on enumerated device instances; a larger set is reported as an error rather than partially read.</summary>
    public const int MaxDeviceInstances = 8192;

    /// <summary>Upper bound on files hashed by inspection (codec ~ hundreds of KiB, PawnIO files are small).</summary>
    public const long MaxInspectedFileBytes = 64L << 20;

    private const uint DigcfPresent = 0x2;
    private const uint DigcfAllClasses = 0x4;

    private readonly SetupApiEnumerator _interfaces = new();

    public string ProgramFilesDirectory => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

    public IReadOnlyList<string> PresentDeviceInstanceIds()
    {
        var set = SetupDiGetClassDevsW(IntPtr.Zero, null, IntPtr.Zero, DigcfPresent | DigcfAllClasses);
        if (set == new IntPtr(-1))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "SetupDiGetClassDevs failed");
        }

        var ids = new List<string>();
        var buffer = new char[1024];
        try
        {
            for (uint i = 0; ; i++)
            {
                var data = new SpDevInfoData { Size = (uint)Marshal.SizeOf<SpDevInfoData>() };
                if (!SetupDiEnumDeviceInfo(set, i, ref data))
                {
                    break; // ERROR_NO_MORE_ITEMS
                }

                if (i >= MaxDeviceInstances)
                {
                    throw new InvalidOperationException($"More than {MaxDeviceInstances} device instances are present.");
                }

                if (InstanceId(set, ref data, buffer) is { } id)
                {
                    ids.Add(id);
                }
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }

        return ids;
    }

    private static unsafe string? InstanceId(IntPtr set, ref SpDevInfoData data, char[] buffer)
    {
        fixed (char* p = buffer)
        {
            return SetupDiGetDeviceInstanceIdW(set, ref data, p, (uint)buffer.Length, out var required) && required is > 1 and <= 1024
                ? new string(p, 0, (int)required - 1)
                : null;
        }
    }

    public IReadOnlyList<UsbDeviceInfo> DeviceInterfaces(Guid interfaceClass) => _interfaces.Enumerate(interfaceClass);

    public IReadOnlyList<HidCollectionInfo> HidCollections(ushort vid, ushort pid) => new WinHidReportDevices(_interfaces).Enumerate(vid, pid);

    public byte[] QueryRfReceiverList(UsbDeviceInfo receiver) => RfReceiverQuery.ReadList(receiver);

    public SetupFileFacts InspectFile(string fullPath)
    {
        try
        {
            var info = new FileInfo(fullPath);
            if (!info.Exists)
            {
                return new(false, 0, null, null);
            }

            if (info.Length > MaxInspectedFileBytes)
            {
                return new(true, info.Length, null, null, $"File exceeds {MaxInspectedFileBytes} bytes.");
            }

            using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var sha = Convert.ToHexString(SHA256.HashData(stream));
            return new(true, info.Length, sha, PawnIoPin.FileVersionOf(fullPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return new(true, 0, null, null, ex.Message);
        }
    }

    public bool IsAdminOnlyWritable(string fullPath, out string reason)
    {
        try
        {
            FileSystemInfo? current = new FileInfo(fullPath);
            var first = true;
            while (current is not null)
            {
                if (!current.Exists)
                {
                    reason = $"'{current.FullName}' does not exist.";
                    return false;
                }

                if (current.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    reason = $"'{current.FullName}' is a reparse point (link/junction).";
                    return false;
                }

                var isDirectory = !first;
                FileSystemSecurity security = isDirectory
                    ? FileSystemAclExtensions.GetAccessControl((DirectoryInfo)current, AccessControlSections.Owner | AccessControlSections.Access)
                    : FileSystemAclExtensions.GetAccessControl((FileInfo)current, AccessControlSections.Owner | AccessControlSections.Access);
                var owner = (security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier)?.Value;
                var aces = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
                    .Select(r => new NativeAce(r.IdentityReference.Value, r.AccessControlType == AccessControlType.Allow, r.FileSystemRights,
                        r.PropagationFlags.HasFlag(PropagationFlags.InheritOnly)));
                var parent = isDirectory ? ((DirectoryInfo)current).Parent : ((FileInfo)current).Directory;
                if (NativePathTrust.Evaluate(current.FullName, owner, aces, isDirectory, isRoot: parent is null) is { } refusal)
                {
                    reason = refusal;
                    return false;
                }

                current = parent;
                first = false;
            }

            reason = string.Empty;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SystemException)
        {
            reason = $"Access control of '{fullPath}' could not be read: {ex.Message}";
            return false;
        }
    }

    public string ProbePawnIoApi(string libraryPath, string sha256, string apiVersion)
    {
        using var loaded = PawnIoNative.Load(libraryPath, sha256, apiVersion);
        return $"API {loaded.ApiVersion}, file version {loaded.FileVersion}";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDevInfoData
    {
        public uint Size;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [LibraryImport("setupapi.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr SetupDiGetClassDevsW(IntPtr classGuid, string? enumerator, IntPtr parent, uint flags);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref SpDevInfoData data);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool SetupDiGetDeviceInstanceIdW(IntPtr set, ref SpDevInfoData data, char* id, uint size, out uint required);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiDestroyDeviceInfoList(IntPtr set);
}
