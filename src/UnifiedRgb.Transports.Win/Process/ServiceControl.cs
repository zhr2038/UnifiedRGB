using System.Runtime.InteropServices;
using System.ServiceProcess;

namespace UnifiedRgb.Transports.Process;

/// <summary>SCM operations WriterGuard Takeover needs (ARCHITECTURE §7.3). Never touches files or processes.</summary>
public interface IServiceControl
{
    /// <summary>Null when the service is not installed.</summary>
    ServiceInfo? Query(string name);

    void Stop(string name, TimeSpan timeout);

    void Start(string name, TimeSpan timeout);

    /// <summary>One of "Automatic", "Manual", "Disabled" (ServiceStartMode names).</summary>
    void SetStartType(string name, string startType);
}

/// <summary>
/// Real SCM access, restricted to the service-kind entries of <see cref="KnownWriters"/>: any other name is rejected before
/// the SCM is even opened, so a bug or a malicious RPC argument cannot stop or reconfigure unrelated services.
/// </summary>
public sealed partial class WindowsServiceControl : IServiceControl
{
    private const uint ScManagerConnect = 0x0001;
    private const uint ServiceChangeConfig = 0x0002;
    private const uint ServiceNoChange = 0xFFFFFFFF;

    public static bool IsControllable(string name) =>
        KnownWriters.All.Any(w => w.Kind.HasFlag(WriterKind.Service) && string.Equals(w.ScmName, name, StringComparison.OrdinalIgnoreCase));

    public ServiceInfo? Query(string name)
    {
        Require(name);
        using var sc = new ServiceController(name);
        try
        {
            return new ServiceInfo(sc.ServiceName, sc.Status == ServiceControllerStatus.Running, sc.StartType.ToString());
        }
        catch (InvalidOperationException)
        {
            return null; // not installed
        }
    }

    public void Stop(string name, TimeSpan timeout)
    {
        Require(name);
        using var sc = new ServiceController(name);
        if (sc.Status is ServiceControllerStatus.Stopped or ServiceControllerStatus.StopPending)
        {
            sc.WaitForStatus(ServiceControllerStatus.Stopped, timeout);
            return;
        }

        sc.Stop();
        sc.WaitForStatus(ServiceControllerStatus.Stopped, timeout);
    }

    public void Start(string name, TimeSpan timeout)
    {
        Require(name);
        using var sc = new ServiceController(name);
        if (sc.Status is ServiceControllerStatus.Running or ServiceControllerStatus.StartPending)
        {
            return;
        }

        sc.Start();
        sc.WaitForStatus(ServiceControllerStatus.Running, timeout);
    }

    public void SetStartType(string name, string startType)
    {
        Require(name);
        uint mode = startType switch
        {
            nameof(ServiceStartMode.Automatic) => 2,
            nameof(ServiceStartMode.Manual) => 3,
            nameof(ServiceStartMode.Disabled) => 4,
            _ => throw new ArgumentOutOfRangeException(nameof(startType), startType, "Only Automatic, Manual or Disabled."),
        };

        var scm = OpenSCManagerW(null, null, ScManagerConnect);
        if (scm == IntPtr.Zero)
        {
            throw new InvalidOperationException($"OpenSCManager failed ({Marshal.GetLastPInvokeError()})");
        }

        try
        {
            var svc = OpenServiceW(scm, name, ServiceChangeConfig);
            if (svc == IntPtr.Zero)
            {
                throw new InvalidOperationException($"OpenService({name}) failed ({Marshal.GetLastPInvokeError()})");
            }

            try
            {
                if (!ChangeServiceConfigW(svc, ServiceNoChange, mode, ServiceNoChange, null, null, IntPtr.Zero, null, null, null, null))
                {
                    throw new InvalidOperationException($"ChangeServiceConfig({name}) failed ({Marshal.GetLastPInvokeError()})");
                }
            }
            finally
            {
                _ = CloseServiceHandle(svc);
            }
        }
        finally
        {
            _ = CloseServiceHandle(scm);
        }
    }

    private static void Require(string name)
    {
        if (string.IsNullOrEmpty(name) || !IsControllable(name))
        {
            throw new ArgumentException($"'{name}' is not a known lighting-writer service; refusing to control it.", nameof(name));
        }
    }

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial IntPtr OpenSCManagerW(string? machine, string? database, uint access);

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial IntPtr OpenServiceW(IntPtr scm, string name, uint access);

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ChangeServiceConfigW(
        IntPtr service,
        uint serviceType,
        uint startType,
        uint errorControl,
        string? binaryPath,
        string? loadOrderGroup,
        IntPtr tagId,
        string? dependencies,
        string? startName,
        string? password,
        string? displayName);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseServiceHandle(IntPtr handle);
}
