namespace UnifiedRgb.Screens.Usb;

/// <summary>Keep a successful USB zero-length packet distinct from ERROR_SEM_TIMEOUT.</summary>
internal readonly record struct ScreenPipeRead(int Length, bool TimedOut);

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

    public static int ReadAndDrain(ScreenPipeReader read, Span<byte> response, int timeoutMs)
    {
        var first = read(response, timeoutMs);
        for (var i = 0; !first.TimedOut && first.Length == 0; i++)
        {
            if (i == FrameDrainLimit) { throw Busy(); }
            first = read(response, DrainTimeoutMs);
        }
        if (first.TimedOut) { return 0; }
        Drain(read, FrameDrainLimit);
        return first.Length;
    }

    /// <summary>Stop only on a real timeout. A ZLP is a successful packet and must not end the drain.</summary>
    public static void Drain(ScreenPipeReader read, int limit)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        Span<byte> extra = stackalloc byte[512];
        for (var i = 0; i < limit; i++)
        {
            if (read(extra, DrainTimeoutMs).TimedOut) { return; }
        }
        throw Busy();
    }

    private static ScreenTransferException Busy() => new(ScreenTransferError.ReplyBacklog,
        "Screen reply stream did not settle within the bounded read drain; no further write is permitted on this channel.");
}
