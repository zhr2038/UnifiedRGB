using Microsoft.Extensions.Logging;
using UnifiedRgb.Devices;
using UnifiedRgb.Transports.Safety;

namespace UnifiedRgb.Transports.OpenRgb;

/// <summary>
/// OpenRGB SDK client (protocol v3/v4). Every outgoing packet passes <see cref="OpenRgbPacketFirewall"/>; the raw
/// transport is never exposed. Requests are serialised; the DeviceListUpdated notification (100) may arrive at any time
/// and only sets <see cref="DeviceListChanged"/>.
/// </summary>
public sealed class OpenRgbSdkClient : IDisposable
{
    /// <summary>Upper bound for one reply; a real controller blob is a few KiB.</summary>
    public const int MaxPayload = 1 << 20;

    private readonly ITransport _raw;
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private readonly byte[] _header = new byte[OpenRgbHeader.Size];
    private volatile bool _deviceListChanged;

    public OpenRgbSdkClient(ITransport transport, ILogger? logger = null)
    {
        _raw = transport ?? throw new ArgumentNullException(nameof(transport));
        Transport = new OpenRgbPacketFirewall(transport, logger);
    }

    /// <summary>Firewalled transport; per-controller write chains are built on top of this.</summary>
    public ITransport Transport { get; }

    /// <summary>Negotiated protocol version (0 before <see cref="HandshakeAsync"/>).</summary>
    public uint Protocol { get; private set; }

    public bool DeviceListChanged => _deviceListChanged;

    /// <summary>Sends the client name and negotiates min(server, <see cref="OpenRgbProtocol.ClientProtocolVersion"/>).</summary>
    public async Task HandshakeAsync(string clientName, CancellationToken ct)
    {
        var reply = await RequestAsync(OpenRgbProtocol.RequestProtocolVersion(), OpenRgbPacketIds.RequestProtocolVersion, ct).ConfigureAwait(false);
        var server = OpenRgbProtocol.ParseU32(reply);
        var negotiated = Math.Min(server, OpenRgbProtocol.ClientProtocolVersion);
        if (negotiated < OpenRgbProtocol.MinServerProtocolVersion)
        {
            throw new NotSupportedException($"OpenRGB server protocol {server} is older than {OpenRgbProtocol.MinServerProtocolVersion}.");
        }

        Protocol = negotiated;
        await Transport.SendAsync(OpenRgbProtocol.SetClientName(clientName), ct).ConfigureAwait(false);
    }

    public async Task<int> GetControllerCountAsync(CancellationToken ct)
    {
        _deviceListChanged = false;
        var reply = await RequestAsync(OpenRgbProtocol.RequestControllerCount(), OpenRgbPacketIds.RequestControllerCount, ct).ConfigureAwait(false);
        return checked((int)OpenRgbProtocol.ParseU32(reply));
    }

    public async Task<OpenRgbControllerData> GetControllerDataAsync(int index, CancellationToken ct)
    {
        RequireHandshake();
        var reply = await RequestAsync(
            OpenRgbProtocol.RequestControllerData((uint)index, Protocol), OpenRgbPacketIds.RequestControllerData, ct).ConfigureAwait(false);
        return OpenRgbProtocol.ParseControllerData(reply, Protocol);
    }

    public async Task<IReadOnlyList<OpenRgbControllerData>> GetAllControllersAsync(CancellationToken ct)
    {
        var n = await GetControllerCountAsync(ct).ConfigureAwait(false);
        var list = new OpenRgbControllerData[n];
        for (var i = 0; i < n; i++)
        {
            list[i] = await GetControllerDataAsync(i, ct).ConfigureAwait(false);
        }

        return list;
    }

    /// <summary>Selects a mode for this session (UpdateMode, never the persisting SaveMode).</summary>
    public ValueTask UpdateModeAsync(int controllerIndex, int modeIndex, OpenRgbMode mode, CancellationToken ct)
    {
        RequireHandshake();
        return Transport.SendAsync(OpenRgbProtocol.UpdateMode((uint)controllerIndex, modeIndex, mode, Protocol), ct);
    }

    public void Dispose()
    {
        _requestGate.Dispose();
        (_raw as IDisposable)?.Dispose();
    }

    private void RequireHandshake()
    {
        if (Protocol == 0)
        {
            throw new InvalidOperationException("OpenRGB handshake has not completed.");
        }
    }

    private async Task<byte[]> RequestAsync(byte[] request, uint replyId, CancellationToken ct)
    {
        await _requestGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await Transport.SendAsync(request, ct).ConfigureAwait(false);
            while (true)
            {
                await ReadExactlyAsync(_header, ct).ConfigureAwait(false);
                var h = OpenRgbHeader.Parse(_header);
                if (h.PayloadSize > MaxPayload)
                {
                    throw new InvalidDataException($"OpenRGB reply of {h.PayloadSize} bytes exceeds {MaxPayload}.");
                }

                var payload = new byte[h.PayloadSize];
                await ReadExactlyAsync(payload, ct).ConfigureAwait(false);
                if (h.PacketId == OpenRgbPacketIds.DeviceListUpdated)
                {
                    _deviceListChanged = true;
                    continue;
                }

                if (h.PacketId == replyId)
                {
                    return payload;
                }
            }
        }
        finally
        {
            _requestGate.Release();
        }
    }

    private async Task ReadExactlyAsync(Memory<byte> buffer, CancellationToken ct)
    {
        while (!buffer.IsEmpty)
        {
            var n = await _raw.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (n <= 0)
            {
                throw new EndOfStreamException("OpenRGB SDK connection closed.");
            }

            buffer = buffer[n..];
        }
    }
}
