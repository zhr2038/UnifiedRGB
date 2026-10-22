namespace UnifiedRgb.Screens.Usb;

/// <summary>Keep a successful USB zero-length packet distinct from ERROR_SEM_TIMEOUT.</summary>
internal readonly record struct ScreenPipeRead(int Length, bool TimedOut, bool MatchesRequest = false);

internal delegate ScreenPipeRead ScreenPipeReader(Span<byte> buffer, int timeoutMs);

/// <summary>
/// H2/TURZX reply flow, as vendor ReadFlush and the pinned TURZX read_flush do. Actual captures contain
/// [512-byte ACK, successful ZLP] pairs. Treating ZLP as no-data leaves ACKs queued and eventually blocks OUT.
/// Reads only, with strict iteration/pipe-timeout bounds; no reset, control transfer or device command.
/// </summary>
internal static class ScreenReplyFlow
{
    public const int DrainTimeoutMs = 10;
    public const int OpenDrainLimit = 64;
    public const int FrameDrainLimit = 8;

    /// <summary>Negotiated native data only. Any extra, short, stale or missing packet invalidates this opened channel.</summary>
    public static int ReadExactPair(ScreenPipeReader read, Span<byte> response, int timeoutMs)
    {
        var ack = read(response, timeoutMs);
        if (ack is { Length: 512, TimedOut: false, MatchesRequest: true })
        {
            Span<byte> terminal = stackalloc byte[512];
            var end = read(terminal, DrainTimeoutMs);
            if (end is { Length: 0, TimedOut: false }) return ack.Length;
        }
        throw new ScreenTransferException(ScreenTransferError.ResponseMismatch,
            "Native data did not return exactly its full matching ACK and terminal ZLP; close the channel before another write.");
    }

    /// <summary>One outstanding image: consume OUR timestamp-matched status AND its terminating USB ZLP. A stale ZLP never ends it.</summary>
    public static int ReadCorrelated(ScreenPipeReader read, Span<byte> response, int timeoutMs)
    {
        var matchedLength = 0;
        for (var i = 0; i <= FrameDrainLimit; i++)
        {
            var packet = read(response, i == 0 ? timeoutMs : DrainTimeoutMs);
            if (packet.TimedOut) break;
            if (packet.Length > 0 && packet.MatchesRequest) matchedLength = packet.Length;
            else if (packet.Length == 0 && matchedLength > 0) return matchedLength;
        }
        throw new ScreenTransferException(ScreenTransferError.ResponseMismatch, "The current image reply and its terminal ZLP were not both verified; close and return to conservative reply draining.");
    }

    public static bool MatchesImageReply(ReadOnlySpan<byte> reply, byte command, uint timestamp) =>
        reply.Length >= 6 && reply[0] == command && reply[1] == 200
        && System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(reply[2..]) == timestamp;

    public static int ReadAndDrain(ScreenPipeReader read, Span<byte> response, int timeoutMs, int quietMs = DrainTimeoutMs)
    {
        if (quietMs is < 1 or > DrainTimeoutMs) throw new ArgumentOutOfRangeException(nameof(quietMs));
        var first = read(response, timeoutMs);
        for (var i = 0; !first.TimedOut && first.Length == 0; i++)
        {
            if (i == FrameDrainLimit) { throw Busy(); }
            first = read(response, quietMs);
        }
        if (first.TimedOut) { return 0; }
        Drain(read, FrameDrainLimit, quietMs);
        return first.Length;
    }

    /// <summary>Stop only on a real timeout. A ZLP is a successful packet and must not end the drain.</summary>
    public static void Drain(ScreenPipeReader read, int limit, int quietMs = DrainTimeoutMs)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        if (quietMs is < 1 or > DrainTimeoutMs) throw new ArgumentOutOfRangeException(nameof(quietMs));
        Span<byte> extra = stackalloc byte[512];
        for (var i = 0; i < limit; i++)
        {
            if (read(extra, quietMs).TimedOut) { return; }
        }
        throw Busy();
    }

    private static ScreenTransferException Busy() => new(ScreenTransferError.ReplyBacklog,
        "Screen reply stream did not settle within the bounded read drain; no further write is permitted on this channel.");
}

/// <summary>Fast completion is negotiated from eight complete, matching ACK/ZLP/quiet transactions of THIS opened device.</summary>
internal sealed class ScreenReplyCapability
{
    private int _consecutive;
    public bool Verified => _consecutive >= 8;
    public void Observe(bool expected, ScreenReadCounts counts) => _consecutive = expected && counts is { Reads: 3, Matched: 1, ZeroLength: 1, Timeouts: 1 }
        ? Math.Min(8, _consecutive + 1) : 0;
}
