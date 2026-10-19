using System.Buffers.Binary;
using Microsoft.Extensions.Logging;
using UnifiedRgb.Devices;
using UnifiedRgb.Transports.OpenRgb;

namespace UnifiedRgb.Transports.Safety;

/// <summary>
/// Default-deny firewall for the OpenRGB SDK connection: every outgoing packet must carry the ORGB magic, a consistent
/// size and a packet id from <see cref="OpenRgbPacketIds.Sendable"/>. Profile and mode-persisting packets cannot pass.
/// </summary>
public sealed partial class OpenRgbPacketFirewall(ITransport inner, ILogger? logger = null) : ITransport
{
    public static void Check(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < OpenRgbHeader.Size || !packet[..4].SequenceEqual(OpenRgbHeader.Magic))
        {
            throw new ForbiddenOpcodeException("openrgb", 0xFF);
        }

        var id = BinaryPrimitives.ReadUInt32LittleEndian(packet[8..]);
        var size = BinaryPrimitives.ReadUInt32LittleEndian(packet[12..]);
        if (!OpenRgbPacketIds.Sendable.Contains(id) || size != packet.Length - OpenRgbHeader.Size)
        {
            throw new ForbiddenOpcodeException($"OpenRGB packet id {id} (size {size}) is not allowed");
        }
    }

    public ValueTask SendAsync(ReadOnlyMemory<byte> frame, CancellationToken ct)
    {
        try
        {
            Check(frame.Span);
        }
        catch (ForbiddenOpcodeException ex)
        {
            if (logger is not null)
            {
                LogBlocked(logger, ex.Message);
            }

            throw;
        }

        return inner.SendAsync(frame, ct);
    }

    public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct) => inner.ReadAsync(buffer, ct);

    [LoggerMessage(EventId = 9003, Level = LogLevel.Critical, Message = "Blocked OpenRGB packet: {Reason}")]
    private static partial void LogBlocked(ILogger logger, string reason);
}
