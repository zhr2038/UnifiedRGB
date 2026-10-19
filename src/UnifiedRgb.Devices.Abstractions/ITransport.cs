namespace UnifiedRgb.Devices;

/// <summary>
/// Raw byte transport. Decorators are applied by AdapterHost in this order:
/// OpcodeFirewall → FrameValidator → RateLimiter → physical transport.
/// </summary>
public interface ITransport
{
    ValueTask SendAsync(ReadOnlyMemory<byte> frame, CancellationToken ct);

    ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct);
}

/// <summary>Device online state consulted by FrameValidator before every send.</summary>
public interface IDeviceState
{
    bool IsOnline { get; }
}

public sealed class DeviceState : IDeviceState
{
    private volatile bool _online;

    public DeviceState(bool online = true) => _online = online;

    public bool IsOnline
    {
        get => _online;
        set => _online = value;
    }
}
