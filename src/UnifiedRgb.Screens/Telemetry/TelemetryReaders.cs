using System.Runtime.InteropServices;

namespace UnifiedRgb.Screens.Telemetry;

/// <summary>Cumulative system CPU times in 100 ns units (kernel time includes idle time, as GetSystemTimes reports it).</summary>
public readonly record struct CpuTimes(ulong Idle, ulong Kernel, ulong User);

public readonly record struct MemoryStatus(long TotalBytes, long AvailableBytes);

/// <summary>Read-only OS counters. A failed read returns null; it never throws.</summary>
public interface ICpuMemoryReader
{
    CpuTimes? ReadCpuTimes();

    MemoryStatus? ReadMemory();
}

/// <summary>One GPU reading; any metric the driver refuses is null.</summary>
public readonly record struct GpuSample(double? UsagePercent, double? TemperatureC, long? MemoryUsedBytes, long? MemoryTotalBytes, string? Error);

/// <summary>A read-only GPU metrics source (NVML). Read may come from any one thread at a time.</summary>
public interface IGpuReader : IDisposable
{
    string Name { get; }

    GpuSample Read();
}

/// <summary>GetSystemTimes and GlobalMemoryStatusEx: no handle, no WMI, no performance counter, no CPU temperature.</summary>
public sealed class WindowsCpuMemoryReader : ICpuMemoryReader
{
    public CpuTimes? ReadCpuTimes() =>
        TelemetryNative.GetSystemTimes(out var idle, out var kernel, out var user)
            ? new CpuTimes((ulong)idle, (ulong)kernel, (ulong)user)
            : null;

    public MemoryStatus? ReadMemory()
    {
        var s = new TelemetryNative.MemoryStatusEx { Length = (uint)Marshal.SizeOf<TelemetryNative.MemoryStatusEx>() };
        return TelemetryNative.GlobalMemoryStatusEx(ref s) && s.TotalPhys > 0
            ? new MemoryStatus((long)s.TotalPhys, (long)Math.Min(s.AvailPhys, s.TotalPhys))
            : null;
    }
}

/// <summary>
/// Optional NVIDIA metrics through the display driver's own nvml.dll, loaded ONLY by full path from System32 with the System32
/// search restriction (never the current directory, PATH or an application folder; no nvidia-smi process). Read-only queries of
/// device index 0: utilization, core temperature and framebuffer memory. Nothing is ever set.
/// </summary>
public sealed unsafe class NvmlGpuReader : IGpuReader
{
    private const int Success = 0;
    private const uint TemperatureGpu = 0;

    private readonly IntPtr _library;
    private readonly IntPtr _device;
    private readonly delegate* unmanaged<int> _shutdown;
    private readonly delegate* unmanaged<IntPtr, NvmlUtilization*, int> _utilization;
    private readonly delegate* unmanaged<IntPtr, uint, uint*, int> _temperature;
    private readonly delegate* unmanaged<IntPtr, NvmlMemory*, int> _memory;
    private int _disposed;

    private NvmlGpuReader(IntPtr library, IntPtr device, IntPtr shutdown, IntPtr utilization, IntPtr temperature, IntPtr memory)
    {
        _library = library;
        _device = device;
        _shutdown = (delegate* unmanaged<int>)shutdown;
        _utilization = (delegate* unmanaged<IntPtr, NvmlUtilization*, int>)utilization;
        _temperature = (delegate* unmanaged<IntPtr, uint, uint*, int>)temperature;
        _memory = (delegate* unmanaged<IntPtr, NvmlMemory*, int>)memory;
    }

    public string Name => "NVML";

    public static string LibraryPath => Path.Combine(Environment.SystemDirectory, "nvml.dll");

    /// <summary>Loads and initializes NVML, or returns null with the reason (no driver, no GPU, missing export).</summary>
    public static NvmlGpuReader? TryCreate(out string? error)
    {
        var path = LibraryPath;
        if (!File.Exists(path))
        {
            error = "nvml.dll is not in System32";
            return null;
        }

        var lib = TelemetryNative.LoadLibraryEx(path, IntPtr.Zero, TelemetryNative.LoadLibrarySearchDllLoadDir | TelemetryNative.LoadLibrarySearchSystem32);
        if (lib == IntPtr.Zero)
        {
            error = $"nvml.dll did not load ({Marshal.GetLastPInvokeError()})";
            return null;
        }

        var init = TelemetryNative.GetProcAddress(lib, "nvmlInit_v2");
        var shutdown = TelemetryNative.GetProcAddress(lib, "nvmlShutdown");
        var handle = TelemetryNative.GetProcAddress(lib, "nvmlDeviceGetHandleByIndex_v2");
        var util = TelemetryNative.GetProcAddress(lib, "nvmlDeviceGetUtilizationRates");
        var temp = TelemetryNative.GetProcAddress(lib, "nvmlDeviceGetTemperature");
        var mem = TelemetryNative.GetProcAddress(lib, "nvmlDeviceGetMemoryInfo");
        if (init == IntPtr.Zero || shutdown == IntPtr.Zero || handle == IntPtr.Zero || util == IntPtr.Zero || temp == IntPtr.Zero || mem == IntPtr.Zero)
        {
            TelemetryNative.FreeLibrary(lib);
            error = "nvml.dll lacks a required export";
            return null;
        }

        var rc = ((delegate* unmanaged<int>)init)();
        if (rc != Success)
        {
            TelemetryNative.FreeLibrary(lib);
            error = $"nvmlInit_v2 returned {rc}";
            return null;
        }

        IntPtr device;
        rc = ((delegate* unmanaged<uint, IntPtr*, int>)handle)(0, &device);
        if (rc != Success)
        {
            ((delegate* unmanaged<int>)shutdown)();
            TelemetryNative.FreeLibrary(lib);
            error = $"nvmlDeviceGetHandleByIndex_v2(0) returned {rc}";
            return null;
        }

        error = null;
        return new NvmlGpuReader(lib, device, shutdown, util, temp, mem);
    }

    public GpuSample Read()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        double? usage = null;
        double? temperature = null;
        long? used = null;
        long? total = null;
        string? error = null;
        NvmlUtilization u;
        var rc = _utilization(_device, &u);
        if (rc == Success && u.Gpu <= 100)
        {
            usage = u.Gpu;
        }
        else
        {
            error = $"utilization {rc}";
        }

        uint t;
        rc = _temperature(_device, TemperatureGpu, &t);
        if (rc == Success && t < 200)
        {
            temperature = t;
        }
        else
        {
            error ??= $"temperature {rc}";
        }

        NvmlMemory m;
        rc = _memory(_device, &m);
        if (rc == Success && m.Total > 0 && m.Total <= long.MaxValue && m.Used <= m.Total)
        {
            used = (long)m.Used;
            total = (long)m.Total;
        }
        else
        {
            error ??= $"memory {rc}";
        }

        return new GpuSample(usage, temperature, used, total, error is null ? null : "NVML " + error);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _shutdown();
            TelemetryNative.FreeLibrary(_library);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NvmlUtilization
    {
        public uint Gpu;
        public uint Memory;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NvmlMemory
    {
        public ulong Total;
        public ulong Free;
        public ulong Used;
    }
}

internal static partial class TelemetryNative
{
    public const uint LoadLibrarySearchDllLoadDir = 0x00000100;
    public const uint LoadLibrarySearchSystem32 = 0x00000800;

    [StructLayout(LayoutKind.Sequential)]
    public struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetSystemTimes(out long idleTime, out long kernelTime, out long userTime);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [LibraryImport("kernel32.dll", EntryPoint = "LoadLibraryExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial IntPtr LoadLibraryEx(string fileName, IntPtr file, uint flags);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    public static partial IntPtr GetProcAddress(IntPtr module, string procName);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FreeLibrary(IntPtr module);
}
