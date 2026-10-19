using System.Runtime.InteropServices;

namespace UnifiedRgb.Transports.Nvapi;

/// <summary>
/// NVAPI interface ids resolved through <c>nvapi_QueryInterface</c>. This table is the binding allow-list: a Safety test
/// asserts it contains exactly these entries and nothing for clocks, voltage, power or fans (ARCHITECTURE §8).
/// </summary>
public static class NvapiInterfaceIds
{
    public const uint Initialize = 0x0150E828;
    public const uint EnumPhysicalGpus = 0xE5AC921F;
    public const uint GetPciIdentifiers = 0x2DDFB66E;
    public const uint I2cWriteExtended = 0x283AC65A;
    public const uint I2cReadExtended = 0x4D7B0709;
    public const uint GetThermalSettings = 0xE3640A56;

    public static IReadOnlyList<uint> All { get; } =
        [Initialize, EnumPhysicalGpus, GetPciIdentifiers, I2cWriteExtended, I2cReadExtended, GetThermalSettings];
}

/// <summary>
/// P/Invoke into nvapi64.dll. The library is loaded lazily (tests and machines without an NVIDIA driver never load it).
/// NV_I2C_INFO_V3 is built from an <see cref="NvI2cProfile"/> (NATIVE design §3.2): MSI NV5000 (400 kHz enum, third
/// argument 1) or the legacy OpenRGB form (default speed, third argument 0).
/// Not thread-safe by design: one owner thread (the GPU DeviceWriter) per handle.
/// </summary>
public sealed unsafe class NvapiNative : INvapi
{
    private const int MaxPhysicalGpus = 64;
    private const uint ThermalTargetAll = 15;

    private delegate* unmanaged<int> _initialize;
    private delegate* unmanaged<IntPtr*, uint*, int> _enumGpus;
    private delegate* unmanaged<IntPtr, uint*, uint*, uint*, uint*, int> _getPciIds;
    private delegate* unmanaged<IntPtr, NvI2cInfoV3*, uint*, int> _i2cWrite;
    private delegate* unmanaged<IntPtr, NvI2cInfoV3*, uint*, int> _i2cRead;
    private delegate* unmanaged<IntPtr, uint, NvThermalSettingsV2*, int> _thermal;
    private bool _ready;

    public bool Initialize()
    {
        if (_ready)
        {
            return true;
        }

        if (!NativeLibrary.TryLoad("nvapi64.dll", out var lib) || !NativeLibrary.TryGetExport(lib, "nvapi_QueryInterface", out var qiPtr))
        {
            return false;
        }

        var qi = (delegate* unmanaged<uint, IntPtr>)qiPtr;
        _initialize = (delegate* unmanaged<int>)qi(NvapiInterfaceIds.Initialize);
        _enumGpus = (delegate* unmanaged<IntPtr*, uint*, int>)qi(NvapiInterfaceIds.EnumPhysicalGpus);
        _getPciIds = (delegate* unmanaged<IntPtr, uint*, uint*, uint*, uint*, int>)qi(NvapiInterfaceIds.GetPciIdentifiers);
        _i2cWrite = (delegate* unmanaged<IntPtr, NvI2cInfoV3*, uint*, int>)qi(NvapiInterfaceIds.I2cWriteExtended);
        _i2cRead = (delegate* unmanaged<IntPtr, NvI2cInfoV3*, uint*, int>)qi(NvapiInterfaceIds.I2cReadExtended);
        _thermal = (delegate* unmanaged<IntPtr, uint, NvThermalSettingsV2*, int>)qi(NvapiInterfaceIds.GetThermalSettings);
        if (_initialize == null || _enumGpus == null || _getPciIds == null || _i2cWrite == null || _i2cRead == null)
        {
            return false;
        }

        _ready = _initialize() == 0;
        return _ready;
    }

    public IReadOnlyList<IntPtr> EnumPhysicalGpus()
    {
        EnsureReady();
        var handles = stackalloc IntPtr[MaxPhysicalGpus];
        uint count = 0;
        Check(_enumGpus(handles, &count), "EnumPhysicalGPUs");
        var list = new IntPtr[count];
        for (var i = 0; i < count; i++)
        {
            list[i] = handles[i];
        }

        return list;
    }

    public NvPciIds GetPciIdentifiers(IntPtr gpu)
    {
        EnsureReady();
        uint dev = 0, sub = 0, rev = 0, ext = 0;
        Check(_getPciIds(gpu, &dev, &sub, &rev, &ext), "GetPCIIdentifiers");
        return new NvPciIds(dev, sub, rev, ext);
    }

    public int I2cWrite(IntPtr gpu, byte device, byte register, ReadOnlySpan<byte> data, NvI2cProfile profile)
    {
        EnsureReady();
        CheckProfile(device, profile);
        var reg = register;
        var third = profile.ThirdArg; // pinned (stack local), initialised from the profile; never an uninitialised buffer
        fixed (byte* p = data)
        {
            var info = NvI2cInfoV3.Create(profile, &reg, p, (uint)data.Length);
            return _i2cWrite(gpu, &info, &third);
        }
    }

    public int I2cRead(IntPtr gpu, byte device, byte register, Span<byte> data, NvI2cProfile profile, out int returnedSize)
    {
        EnsureReady();
        CheckProfile(device, profile);
        data.Clear(); // never decode a stale or uninitialised buffer
        var reg = register;
        var third = profile.ThirdArg;
        fixed (byte* p = data)
        {
            var info = NvI2cInfoV3.Create(profile, &reg, p, (uint)data.Length);
            var status = _i2cRead(gpu, &info, &third);
            returnedSize = info.Size > int.MaxValue ? int.MaxValue : (int)info.Size;
            return status;
        }
    }

    /// <summary>Test seam: the NV_I2C_INFO_V3 a profile produces (pointers omitted), its size and version (NATIVE design §10 item 1).</summary>
    public static NvI2cInfoSnapshot Describe(NvI2cProfile profile, uint size)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var i = NvI2cInfoV3.Create(profile, null, null, size);
        return new NvI2cInfoSnapshot(Marshal.SizeOf<NvI2cInfoV3>(), i.Version, i.DisplayMask, i.IsDdcPort, i.I2cDevAddress, i.RegAddrSize, i.Size,
            i.I2cSpeed, i.I2cSpeedKhz, i.PortId, i.IsPortIdSet, profile.ThirdArg);
    }

    /// <summary>Test seam: byte offset of a field of NV_I2C_INFO_V3 on this platform.</summary>
    public static int FieldOffset(string field) => (int)Marshal.OffsetOf<NvI2cInfoV3>(field);

    public int? GetGpuTemperature(IntPtr gpu)
    {
        if (!_ready || _thermal == null)
        {
            return null;
        }

        var s = default(NvThermalSettingsV2);
        s.Version = (uint)sizeof(NvThermalSettingsV2) | (2u << 16);
        if (_thermal(gpu, ThermalTargetAll, &s) != 0 || s.Count == 0)
        {
            return null;
        }

        return s.Sensor0CurrentTemp;
    }

    private static void Check(int status, string op)
    {
        if (status != 0)
        {
            throw new NvapiException(status, op);
        }
    }

    private static void CheckProfile(byte device, NvI2cProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (device != profile.DevAddress7)
        {
            throw new ArgumentException($"Device 0x{device:X2} does not match profile {profile.Name} (0x{profile.DevAddress7:X2}).", nameof(device));
        }
    }

    private void EnsureReady()
    {
        if (!_ready)
        {
            throw new InvalidOperationException("NVAPI is not initialized.");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NvI2cInfoV3
    {
        public uint Version;
        public uint DisplayMask;
        public byte IsDdcPort;
        public byte I2cDevAddress;
        public byte* I2cRegAddress;
        public uint RegAddrSize;
        public byte* Data;
        public uint Size;
        public uint I2cSpeed;
        public int I2cSpeedKhz;
        public byte PortId;
        public uint IsPortIdSet;

        /// <summary>Version = Marshal.SizeOf | 3 &lt;&lt; 16 (64 bytes on x64 → 0x30040; NDA's x86 form is 44 → 0x3002C).</summary>
        public static NvI2cInfoV3 Create(NvI2cProfile profile, byte* reg, byte* data, uint size) => new()
        {
            Version = (uint)Marshal.SizeOf<NvI2cInfoV3>() | (3u << 16),
            DisplayMask = 0,
            IsDdcPort = 0,
            I2cDevAddress = (byte)(profile.DevAddress7 << 1),
            I2cRegAddress = reg,
            RegAddrSize = profile.RegAddrSize,
            Data = data,
            Size = size,
            I2cSpeed = profile.SpeedField,
            I2cSpeedKhz = (int)profile.SpeedKhzEnum,
            PortId = profile.PortId,
            IsPortIdSet = profile.PortIdSet ? 1u : 0u,
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NvThermalSettingsV2
    {
        public uint Version;
        public uint Count;
        public int Sensor0Controller;
        public int Sensor0DefaultMin;
        public int Sensor0DefaultMax;
        public int Sensor0CurrentTemp;
        public int Sensor0Target;
        public fixed int OtherSensors[10];
    }
}

/// <summary>The managed view of one NV_I2C_INFO_V3 as built for a profile (test seam; pointers omitted).</summary>
public sealed record NvI2cInfoSnapshot(
    int StructSize,
    uint Version,
    uint DisplayMask,
    byte IsDdcPort,
    byte DevAddress8,
    uint RegAddrSize,
    uint Size,
    uint SpeedField,
    int SpeedKhzEnum,
    byte PortId,
    uint IsPortIdSet,
    uint ThirdArg);
