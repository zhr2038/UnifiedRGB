namespace UnifiedRgb.Devices.Safety;

/// <summary>Per-transport frame rules: length bounds and the location/range of the channel byte.</summary>
public sealed record FrameValidationRules
{
    public int MinLength { get; init; } = 1;

    public int MaxLength { get; init; } = 1024;

    /// <summary>When non-empty only these exact lengths are accepted (e.g. fixed-size HID reports).</summary>
    public IReadOnlySet<int> AllowedLengths { get; init; } = new HashSet<int>();

    /// <summary>Byte offset of the channel/port index, or null when the protocol has none.</summary>
    public int? ChannelOffset { get; init; }

    /// <summary>Inclusive maximum channel index.</summary>
    public int MaxChannel { get; init; } = 255;
}

public enum FrameRejectReason
{
    None,
    TooShort,
    TooLong,
    LengthNotAllowed,
    ChannelOutOfRange,
    DeviceOffline,
    LoopHeaderInvalid,
}

/// <summary>Header fields of one loop upload, as they will go on the wire (ARCHITECTURE-D2 §7).</summary>
public readonly record struct LoopHeader(int Frames, int LedCount, int CompressedLength, int PacketCount);

/// <summary>Device limits a loop header is checked against.</summary>
/// <param name="ZoneLeds">LED count of the target zone; the header must carry exactly this.</param>
/// <param name="MaxCompressedLength">Largest compressed animation the device accepts (Lian Li: 12288).</param>
/// <param name="ChunkLength">Compressed bytes per data packet (Lian Li: 220); packets = ⌈len / chunk⌉ + 1 header.</param>
public sealed record LoopHeaderRules(int ZoneLeds, int MaxCompressedLength, int ChunkLength, int MaxFrames = ushort.MaxValue, int MaxLeds = byte.MaxValue);

public sealed class FrameRejectedException : InvalidOperationException
{
    public FrameRejectedException(FrameRejectReason reason, int length)
        : base($"Frame rejected: {reason} (length {length})")
    {
        Reason = reason;
    }

    public FrameRejectedException()
    {
    }

    public FrameRejectedException(string message)
        : base(message)
    {
    }

    public FrameRejectedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public FrameRejectReason Reason { get; }
}

/// <summary>ITransport decorator: validates length, channel index and device online state before every send.</summary>
public sealed class FrameValidator : ITransport
{
    private readonly ITransport _inner;
    private readonly FrameValidationRules _rules;
    private readonly IDeviceState _state;

    public FrameValidator(ITransport inner, FrameValidationRules rules, IDeviceState state)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _rules = rules ?? throw new ArgumentNullException(nameof(rules));
        _state = state ?? throw new ArgumentNullException(nameof(state));
    }

    public static FrameRejectReason Validate(ReadOnlySpan<byte> frame, FrameValidationRules rules, IDeviceState state)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(state);
        if (!state.IsOnline)
        {
            return FrameRejectReason.DeviceOffline;
        }

        if (frame.Length < rules.MinLength)
        {
            return FrameRejectReason.TooShort;
        }

        if (frame.Length > rules.MaxLength)
        {
            return FrameRejectReason.TooLong;
        }

        if (rules.AllowedLengths.Count > 0 && !rules.AllowedLengths.Contains(frame.Length))
        {
            return FrameRejectReason.LengthNotAllowed;
        }

        if (rules.ChannelOffset is int off)
        {
            if (off >= frame.Length || frame[off] > rules.MaxChannel)
            {
                return FrameRejectReason.ChannelOutOfRange;
            }
        }

        return FrameRejectReason.None;
    }

    /// <summary>
    /// Loop-header check run before the first packet of an upload: frames ≥ 1 (and fit the field), LED count = zone size,
    /// 1 ≤ compressed length ≤ the device maximum, packet count = ⌈length / chunk⌉ + 1.
    /// </summary>
    public static FrameRejectReason ValidateLoopHeader(LoopHeader header, LoopHeaderRules rules, IDeviceState state)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(state);
        if (!state.IsOnline)
        {
            return FrameRejectReason.DeviceOffline;
        }

        var ok = header.Frames >= 1 && header.Frames <= rules.MaxFrames
            && header.LedCount == rules.ZoneLeds && header.LedCount >= 1 && header.LedCount <= rules.MaxLeds
            && header.CompressedLength >= 1 && header.CompressedLength <= rules.MaxCompressedLength
            && rules.ChunkLength > 0
            && header.PacketCount == ((header.CompressedLength + rules.ChunkLength - 1) / rules.ChunkLength) + 1;
        return ok ? FrameRejectReason.None : FrameRejectReason.LoopHeaderInvalid;
    }

    /// <summary>Throws <see cref="FrameRejectedException"/> unless <see cref="ValidateLoopHeader"/> passes.</summary>
    public static void EnsureLoopHeader(LoopHeader header, LoopHeaderRules rules, IDeviceState state)
    {
        var verdict = ValidateLoopHeader(header, rules, state);
        if (verdict != FrameRejectReason.None)
        {
            throw new FrameRejectedException(verdict, header.CompressedLength);
        }
    }

    public ValueTask SendAsync(ReadOnlyMemory<byte> frame, CancellationToken ct)
    {
        var verdict = Validate(frame.Span, _rules, _state);
        if (verdict != FrameRejectReason.None)
        {
            throw new FrameRejectedException(verdict, frame.Length);
        }

        return _inner.SendAsync(frame, ct);
    }

    public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct) => _inner.ReadAsync(buffer, ct);
}
