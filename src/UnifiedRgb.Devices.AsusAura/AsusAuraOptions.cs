using Microsoft.Extensions.Logging;
using UnifiedRgb.Transports.Hid;
using UnifiedRgb.Transports.Safety;

namespace UnifiedRgb.Devices.AsusAura;

/// <summary>One addressable header: the owner-entered strip length (the config table has none). 0 = not exposed.</summary>
public sealed record AsusAuraHeaderOptions
{
    public int LedCount { get; init; }
}

/// <summary>Options of the native Aura HID adapter (NATIVE design §4, §8). Every gate defaults to closed.</summary>
public sealed record AsusAuraOptions
{
    /// <summary>
    /// Set by Codex only after the owner's E3c (design §4.4). False: the board is discoverable and probed in a leased
    /// bring-up session, but never gets a scene writer. No packet status or reply sets it.
    /// </summary>
    public bool ControlVerified { get; init; }

    /// <summary>Configured per-frame budget in ms (ceiling = ⌊1000 / ms⌋); 0 = the class ceiling of 30 fps (design §9).</summary>
    public int FrameBudgetMs { get; init; }

    /// <summary>Bound of every HID input-report read (0x82/0xB0 replies).</summary>
    public int HidTimeoutMs { get; init; } = AsusAuraDefaults.HidTimeoutMs;

    /// <summary>Addressable header i: exposed only when <c>LedCount &gt; 0</c> and the config table reports header i.</summary>
    public IReadOnlyList<AsusAuraHeaderOptions> Headers { get; init; } = [];

    /// <summary>
    /// Copied by the Host from UnifiedRgb:Bringup:HoldBlackOnRelease (overnight addendum §4). Aura's release is unchanged
    /// (nothing is sent); while true only black markers are accepted and a failed black send stops all further sends.
    /// </summary>
    public bool HoldBlackOnRelease { get; init; }
}

public static class AsusAuraDefaults
{
    public const int CeilingFps = 30;
    public const int HidTimeoutMs = 250;
    public const ushort Vid = 0x0B05;
    public const ushort Pid = 0x19AF;
    public const ushort UsagePage = 0xFF72;
    public const ushort Usage = 0x00A1;

    /// <summary>The live MI_02 lighting collection (design §4.1, addendum PnP evidence); MI_00 is never selected.</summary>
    public static HidCollectionRule Rule { get; } = new(Vid, Pid, UsagePage, Usage, AuraProtocol.ReportLength);
}

public sealed class AsusAuraAdapterFactory : IDeviceAdapterFactory
{
    public DeviceClass Class => DeviceClass.AsusAura;

    public IDeviceAdapter Create(IServiceProvider sp)
    {
        ArgumentNullException.ThrowIfNull(sp);
        var options = sp.GetService(typeof(AsusAuraOptions)) as AsusAuraOptions ?? new AsusAuraOptions();
        var loggers = sp.GetService(typeof(ILoggerFactory)) as ILoggerFactory;
        return new AsusAuraAdapter(new WinHidReportDevices(), options, loggers?.CreateLogger<AsusAuraAdapter>(), sp.GetService(typeof(INativeMapSource)) as INativeMapSource);
    }
}
