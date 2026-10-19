using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using UnifiedRgb.Devices;

namespace UnifiedRgb.Transports.Safety;

public sealed class ForbiddenOpcodeException : InvalidOperationException
{
    public ForbiddenOpcodeException(string channel, byte opcode)
        : base($"Forbidden {channel} opcode {opcode} blocked by OpcodeFirewall")
    {
        Channel = channel;
        Opcode = opcode;
    }

    public ForbiddenOpcodeException()
    {
        Channel = string.Empty;
    }

    public ForbiddenOpcodeException(string message)
        : base(message)
    {
        Channel = string.Empty;
    }

    public ForbiddenOpcodeException(string message, Exception innerException)
        : base(message, innerException)
    {
        Channel = string.Empty;
    }

    public string Channel { get; }

    public byte Opcode { get; }
}

/// <summary>
/// Outermost ITransport decorator. Hard-coded numeric deny list; intentionally no names for the denied
/// opcodes exist anywhere in the code base. A hit throws and logs Critical. Not configurable at runtime.
/// </summary>
public sealed partial class OpcodeFirewall : ITransport
{
    // USB_CMD 32..35 and RF_CMD 23, 24, 33 (analysis 02 §4). Numbers only, by design.
    private static readonly byte[] DeniedUsb = [32, 33, 34, 35];
    private static readonly byte[] DeniedRf = [23, 24, 33];

    private readonly ITransport _inner;
    private readonly OpcodeLayout _layout;
    private readonly ILogger _logger;
    private readonly RelaySequence _sequence = new();

    public OpcodeFirewall(ITransport inner, OpcodeLayout layout, ILogger? logger = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
        _logger = logger ?? NullLogger.Instance;
    }

    public static IReadOnlyList<byte> DeniedUsbOpcodes => DeniedUsb;

    public static IReadOnlyList<byte> DeniedRfOpcodes => DeniedRf;

    public static bool IsUsbDenied(byte op) => Array.IndexOf(DeniedUsb, op) >= 0;

    public static bool IsRfDenied(byte op) => Array.IndexOf(DeniedRf, op) >= 0;

    /// <summary>
    /// Stateless part of the check. Throws ForbiddenOpcodeException when the frame carries a denied opcode, and fails closed
    /// on short frames, chunk indexes above <see cref="OpcodeLayout.MaxChunkIndex"/> and first chunks without the RF packet
    /// tag. Returns the relay chunk index, or -1 for a non-relay frame. Continuation chunks (index &gt; 0) carry no opcode;
    /// <see cref="RelaySequence"/> ties them to the checked first chunk.
    /// </summary>
    public static int Check(ReadOnlySpan<byte> frame, OpcodeLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if (frame.Length <= layout.UsbCmdOffset)
        {
            throw new ForbiddenOpcodeException("Frame too short to locate the USB opcode; refusing to send.");
        }

        var usb = frame[layout.UsbCmdOffset];
        if (IsUsbDenied(usb))
        {
            throw new ForbiddenOpcodeException("USB", usb);
        }

        if (!layout.IsRelay(usb))
        {
            return -1;
        }

        if (frame.Length <= layout.ChunkIndexOffset)
        {
            throw new ForbiddenOpcodeException("Relay frame too short to locate the chunk index; refusing to send.");
        }

        var chunk = frame[layout.ChunkIndexOffset];
        if (chunk > OpcodeLayout.MaxChunkIndex)
        {
            throw new ForbiddenOpcodeException($"Relay chunk index {chunk} is outside 0..{OpcodeLayout.MaxChunkIndex}; refusing to send.");
        }

        if (chunk > 0)
        {
            return chunk;
        }

        if (frame.Length <= layout.RfCmdOffset)
        {
            throw new ForbiddenOpcodeException("Relay frame too short to locate the RF opcode; refusing to send.");
        }

        if (frame[layout.RfTagOffset] != OpcodeLayout.RfPacketTag)
        {
            throw new ForbiddenOpcodeException("Relay packet does not start with the RF packet tag; its RF opcode cannot be located.");
        }

        var rf = frame[layout.RfCmdOffset];
        if (IsRfDenied(rf))
        {
            throw new ForbiddenOpcodeException("RF", rf);
        }

        return 0;
    }

    public ValueTask SendAsync(ReadOnlyMemory<byte> frame, CancellationToken ct)
    {
        try
        {
            _sequence.Check(frame.Span, _layout);
        }
        catch (ForbiddenOpcodeException ex)
        {
            LogBlocked(_logger, ex.Message, frame.Length);
            throw;
        }

        return _inner.SendAsync(frame, ct);
    }

    public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct) => _inner.ReadAsync(buffer, ct);

    [LoggerMessage(EventId = 9001, Level = LogLevel.Critical, Message = "OpcodeFirewall blocked a frame: {Reason} (length {Length})")]
    private static partial void LogBlocked(ILogger logger, string reason, int length);
}

/// <summary>
/// Per-transport state of the firewall: a relay continuation chunk is admitted only when it directly follows the chunk
/// before it of a packet whose first chunk passed <see cref="OpcodeFirewall.Check"/>. Any other frame ends the packet.
/// </summary>
public sealed class RelaySequence
{
    private readonly object _gate = new();
    private int _last = -1;

    public void Check(ReadOnlySpan<byte> frame, OpcodeLayout layout)
    {
        lock (_gate)
        {
            int chunk;
            try
            {
                chunk = OpcodeFirewall.Check(frame, layout);
            }
            catch (ForbiddenOpcodeException)
            {
                _last = -1;
                throw;
            }

            if (chunk > 0 && chunk != _last + 1)
            {
                _last = -1;
                throw new ForbiddenOpcodeException($"Relay chunk {chunk} does not continue a checked RF packet; refusing to send.");
            }

            _last = chunk;
        }
    }
}
