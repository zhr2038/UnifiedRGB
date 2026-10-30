namespace UnifiedRgb.Devices.AsusRog;

/// <summary>
/// Direct per-key colour (<c>C0 81</c>) on the ONE keyboard vendor control route the shared OLED actor already owns. Requests
/// use that route's wired RID0/65 boundary representation: the wired 1C24 connection sends them as they are and the wireless
/// connection translates them to RID2/64, so both transports carry exactly the established RF framing
/// (<see cref="RogProtocol.KeyboardFrame"/>: <c>[RID, C0, 81, count, 00, (led, R, G, B) × count]</c>, at most 14 records,
/// zero tail). No handle, sender, retry or clock is added: callers are the actor that owns the connection, and every reply,
/// refusal and poison rule is the connection's own.
/// </summary>
/// <remarks>
/// Protocol provenance is the local compatibility reference (AsusKeyboard.SetLedColors, reportId 0 / 65-byte packets on the
/// direct vendor interface, one reply per packet, <c>FF AA</c> refusal). It is not optical proof; the reply shape of this
/// firmware and LED order remain Root's physical validation. Exact stock firmware uses GET27/SET74 software control and
/// returns C0/82 with a count, 23 opaque bytes and a zero tail after a C0/81 packet.
/// </remarks>
public static class RogPerKeyLighting
{
    /// <summary>Wired 1C24 vendor collection, RID0 / 65-byte reports.</summary>
    public const string WiredTransport = "rog-usb65-c081-v1";
    /// <summary>2.4 GHz receiver vendor collection, RID2 / 64-byte reports.</summary>
    public const string WirelessTransport = "rog-rf64-c081-v1";
    public const int BoundaryReportLength = 65;

    /// <summary>The per-key transport of an owned connection kind; null for anything else (never guessed).</summary>
    public static string? TransportFor(string? connectionKind) => connectionKind switch
    {
        "wired" => WiredTransport,
        "wireless" => WirelessTransport,
        _ => null,
    };

    /// <summary>
    /// Every report of one frame at the RID0/65 boundary. The whole frame is validated first (1..128 distinct LED ids), so an
    /// invalid frame returns nothing to send. Byte 64 is always zero, which the wireless translation requires.
    /// </summary>
    public static IReadOnlyList<byte[]> BoundaryFrame(ReadOnlySpan<RogKeyColor> keys)
    {
        var reports = RogProtocol.KeyboardFrame(2, keys);
        var result = new byte[reports.Count][];
        for (var i = 0; i < reports.Count; i++)
        {
            var boundary = new byte[BoundaryReportLength];
            reports[i].CopyTo(boundary, 0);
            boundary[0] = 0;
            result[i] = boundary;
        }
        return Array.AsReadOnly(result);
    }

    /// <summary>The original unpaced entry point, retained for existing compiled callers.</summary>
    public static Task WriteAsync(Func<byte[], bool, CancellationToken, Task<byte[]>> exchange, IReadOnlyList<RogKeyColor> keys,
        CancellationToken ct) => WriteAsync(exchange, keys, ct, TimeSpan.Zero, null);

    /// <summary>
    /// Sends one complete frame through the owner's exchange, one report and one reply at a time. Only the stock C0/82 reply
    /// with matching count/reserved, 23 opaque bytes and zero tail is accepted. No retry or partial-frame resume is allowed.
    /// A positive <paramref name="reportDelay"/> is awaited (cancellably, via <paramref name="delay"/>, default
    /// <see cref="Task.Delay(TimeSpan, CancellationToken)"/>) before EVERY report, including the first; zero sends unpaced.
    /// Report bytes, order and reply checks are identical either way.
    /// </summary>
    public static async Task WriteAsync(Func<byte[], bool, CancellationToken, Task<byte[]>> exchange, IReadOnlyList<RogKeyColor> keys,
        CancellationToken ct, TimeSpan reportDelay = default, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        ArgumentNullException.ThrowIfNull(exchange);
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentOutOfRangeException.ThrowIfLessThan(reportDelay, TimeSpan.Zero);
        delay ??= Task.Delay;
        foreach (var report in BoundaryFrame(keys.ToArray()))
        {
            if (reportDelay > TimeSpan.Zero) await delay(reportDelay, ct).ConfigureAwait(false);
            var reply = await exchange(report, false, ct).ConfigureAwait(false);
            RequireReply(reply, report);
        }
    }

    internal static bool IsFrameRequest(ReadOnlySpan<byte> request) => request.Length == BoundaryReportLength
        && request[0] == 0 && request[1] == 0xC0 && request[2] == 0x81 && request[3] is >= 1 and <= 14 && request[4] == 0;

    internal static bool MatchesReplyHeader(ReadOnlySpan<byte> reply, ReadOnlySpan<byte> request) => IsFrameRequest(request)
        && reply.Length == BoundaryReportLength && reply[0] == request[0] && reply[1] == 0xC0 && reply[2] == 0x82
        && reply[3] == request[3] && reply[4] == 0;

    internal static bool IsColourReply(ReadOnlySpan<byte> reply) => reply.Length == BoundaryReportLength
        && reply[0] == 0 && reply[1] == 0xC0 && reply[2] == 0x82 && reply[3] is >= 1 and <= 14 && reply[4] == 0
        && !reply[28..].ContainsAnyExcept((byte)0);

    internal static void RequireReply(ReadOnlySpan<byte> reply, ReadOnlySpan<byte> request)
    {
        if (!MatchesReplyHeader(reply, request) || reply[28..].ContainsAnyExcept((byte)0))
            throw new InvalidDataException("Per-key colour reply is not C0/82 with matching count, 23 opaque bytes and zero tail; no retry.");
    }
}
