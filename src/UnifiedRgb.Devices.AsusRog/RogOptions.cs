using UnifiedRgb.Transports.Hid;

namespace UnifiedRgb.Devices.AsusRog;

/// <summary>Local route pins, never scene-package content. Empty by default: discovery exposes diagnostic receivers only.</summary>
public sealed record RogKeyboardBinding
{
    public string ReceiverKey { get; init; } = string.Empty;
    public byte ReportId { get; init; }
    public byte[] LedIds { get; init; } = [];
    public bool ControlVerified { get; init; }
    public bool KeyOrderVerified { get; init; }
    /// <summary>Single aggregate colour with snapshot/readback; does not need or invent physical key indices.</summary>
    public bool WholeColorOnly { get; init; }
}

public sealed record RogOptions
{
    public int HidTimeoutMs { get; init; } = 250;
    /// <summary>Conservative host ceiling, not a measured lighting/panel refresh rate.</summary>
    public int MaximumFps { get; init; } = 10;
    public RogKeyboardBinding[] Keyboards { get; init; } = [];
    public RogMouseBinding[] Mice { get; init; } = [];
    /// <summary>Per-key colour on the shared keyboard lighting owner; empty and disabled by default.</summary>
    public RogPerKeyOptions KeyboardPerKey { get; init; } = new();
}

/// <summary>
/// Local per-key pins for the shared keyboard lighting owner. Nothing here is product content: every map is a local physical
/// scan result, one per transport (<see cref="RogPerKeyLighting.WiredTransport"/>, <see cref="RogPerKeyLighting.WirelessTransport"/>).
/// With nothing configured the keyboard stays exactly the aggregate whole-keyboard zone.
/// </summary>
public sealed record RogPerKeyOptions
{
    public bool Enabled { get; init; }
    public RogPerKeyMapOptions[] Maps { get; init; } = [];

    /// <summary>False when nothing at all is configured: the adapter then reports no per-key status.</summary>
    public bool Configured => Enabled || Maps.Length > 0;
}

/// <summary>One bindable local map. <see cref="KeyOrderVerified"/> is the physical key-order check of this transport's scan.</summary>
public sealed record RogPerKeyMapOptions
{
    public string Model { get; init; } = string.Empty;
    public string Transport { get; init; } = string.Empty;
    public bool Complete { get; init; }
    public bool Verified { get; init; }
    public string? Evidence { get; init; }
    public bool KeyOrderVerified { get; init; }
    public RogPerKeyCellOptions[] Keys { get; init; } = [];

    public PerKeyMap ToMap() =>
        new(Model, Transport, Keys.Select(k => new PerKeyCell(k.LedId, k.Key, k.Row, k.Column)).ToArray(), Complete, Verified, Evidence);
}

public sealed record RogPerKeyCellOptions
{
    public int LedId { get; init; }
    public string Key { get; init; } = string.Empty;
    public int Row { get; init; }
    public int Column { get; init; }
}

/// <summary>One area0 aggregate zone; no claim of separately addressable light-guide LEDs.</summary>
public sealed record RogMouseBinding
{
    public string ReceiverKey { get; init; } = string.Empty;
    public byte ReportId { get; init; }
    public bool ControlVerified { get; init; }
}

public sealed class RogAdapterFactory : IDeviceAdapterFactory
{
    public DeviceClass Class => DeviceClass.AsusRog;

    public IDeviceAdapter Create(IServiceProvider sp)
    {
        ArgumentNullException.ThrowIfNull(sp);
        return new RogAdapter(new WinHidReportDevices(), sp.GetService(typeof(RogOptions)) as RogOptions ?? new());
    }
}
