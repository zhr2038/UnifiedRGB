using UnifiedRgb.Transports.Nvapi;

namespace UnifiedRgb.Devices.MsiGpu;

/// <summary>Bound from UnifiedRgb:MsiGpu (NATIVE design §8). Every gate defaults to closed.</summary>
public sealed record MsiGpuOptions
{
    /// <summary>The recovered NV5000 indexed path (default; <see cref="MsiNv5000Adapter"/>).</summary>
    public const string Nv5000Indexed = "Nv5000Indexed";

    /// <summary>The dormant provisional legacy register map (<see cref="MsiLegacyAdapter"/>); not the 5090 path.</summary>
    public const string LegacyGlobal = "LegacyGlobal";

    /// <summary>
    /// Legacy profile only: the MSIGPUv2 register map is provisional (see <see cref="MsiGpuV2Registers"/>). Until it is confirmed
    /// on hardware this stays false and the legacy adapter refuses to open the GPU, so no I2C write happens.
    /// </summary>
    public bool RegisterMapVerified { get; init; }

    /// <summary><see cref="Nv5000Indexed"/> or <see cref="LegacyGlobal"/>; anything else is refused (no silent fallback).</summary>
    public string Profile { get; init; } = Nv5000Indexed;

    /// <summary>
    /// Set by Codex only after the owner's E3c (design §3.4). False: the GPU is discoverable and openable in a leased bring-up
    /// session but never gets a scene writer. No packet status, read-back or black-frame result sets it.
    /// </summary>
    public bool ControlVerified { get; init; }

    /// <summary>Records per NV5000 frame, 1..34 (the recovered caller's indices 0..33); larger values are refused.</summary>
    public int MaxIndices { get; init; } = Nv5000Defaults.MaxIndices;

    /// <summary>Configured per-frame budget in ms (ceiling = ⌊1000 / ms⌋); 0 = the class ceiling of 20 fps (design §9).</summary>
    public int FrameBudgetMs { get; init; }

    /// <summary>Delay before the release write 0x06 ← 1; at least the recovered 100 ms.</summary>
    public int ReleaseDelayMs { get; init; } = Nv5000Defaults.ReleaseDelayMs;

    /// <summary>
    /// Resend an unchanged frame after this many ms (0 = never; RQ-G6 pending). Under hold-black it is the period of the
    /// fixed all-black resend (overnight addendum §4).
    /// </summary>
    public int KeepAliveMs { get; init; }

    /// <summary>
    /// Copied by the Host from UnifiedRgb:Bringup:HoldBlackOnRelease (overnight addendum §4); a value configured here is
    /// overwritten. True: no release write; an entered GPU is left in host mode showing black.
    /// </summary>
    public bool HoldBlackOnRelease { get; init; }
}

/// <summary>Defaults of the NV5000 options.</summary>
public static class Nv5000Defaults
{
    public const int MaxIndices = UnifiedRgb.Transports.Safety.Nv5000.EvidencedIndices;
    public const int ReleaseDelayMs = 100;
    public const int CeilingFps = 20;
}

public sealed record MsiGpuDevice(ControllerDescriptor Descriptor, IntPtr Handle);

/// <summary>Finds the owner's MSI RTX 5090 SUPRIM: NVIDIA vendor 0x10DE, device 0x2B85, subsystem 0x53011462 (MSI 0x1462).</summary>
public static class MsiGpuDiscovery
{
    public const ushort NvidiaVendor = 0x10DE;
    public const ushort Rtx5090Device = 0x2B85;
    public const uint MsiSuprimSubSystem = 0x53011462;
    public const int MaxFps = 20;

    public static bool IsSupported(NvPciIds ids) =>
        ids.Vendor == NvidiaVendor && ids.Device == Rtx5090Device && ids.SubSystemId == MsiSuprimSubSystem;

    public static IReadOnlyList<MsiGpuDevice> Discover(INvapi nvapi)
    {
        ArgumentNullException.ThrowIfNull(nvapi);
        if (!nvapi.Initialize())
        {
            return [];
        }

        var found = new List<MsiGpuDevice>();
        var gpus = nvapi.EnumPhysicalGpus();
        for (var i = 0; i < gpus.Count; i++)
        {
            var ids = nvapi.GetPciIdentifiers(gpus[i]);
            if (!IsSupported(ids))
            {
                continue;
            }

            var id = new ControllerId(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"msigpu:{i}:{ids.SubSystemId:X8}"));
            var caps = new Capabilities(true, MaxFps, MsiGpuModes.All, MsiGpuV2Registers.BrightnessLevels.Count, MsiGpuV2Registers.SpeedLevels.Count, 3);
            var d = new ControllerDescriptor(id, "MSI", "MSI GeForce RTX 5090 SUPRIM", DeviceType.Gpu, DeviceClass.MsiGpu,
                id.Value, [new ZoneDescriptor(new ZoneId(0), "Logo", ZoneLayout.Point)], caps);
            found.Add(new MsiGpuDevice(d, gpus[i]));
        }

        return found;
    }
}
