namespace UnifiedRgb.Transports.Safety;

/// <summary>
/// Where the opcode bytes sit in a Lian Li frame, IL-pinned (analysis/il, M5c: <c>MasterDevice.SendRfData</c>). Every RF
/// packet is 240 bytes, relayed as four 64-byte USB frames <c>[16][chunk 0..3][channel][rx][60 packet bytes]</c>. Only chunk 0
/// holds the packet head <c>[18][RF_CMD]…</c>, so the RF opcode sits at frame byte 5 of chunk 0; chunks 1..3 are
/// continuation data with no opcode. <see cref="OpcodeFirewall.Check"/> checks chunk 0 and <see cref="RelaySequence"/>
/// admits a continuation only straight after the checked chunk before it.
/// </summary>
public sealed record OpcodeLayout(int UsbCmdOffset, int ChunkIndexOffset, int RfTagOffset, int RfCmdOffset, IReadOnlyList<byte> RelayUsbCmds)
{
    /// <summary>Byte 0 of every RF packet the IL builds (<c>array[0] = 18</c>); anything else is refused.</summary>
    public const byte RfPacketTag = 18;

    /// <summary>240-byte packet / 60 bytes per chunk → chunk indexes 0..3.</summary>
    public const int MaxChunkIndex = 3;

    /// <summary>
    /// WinUSB path. USB 16 is the relay (<c>SendRfData</c>); USB 14 never appears in the IL but stays under the same rules
    /// (fail closed).
    /// </summary>
    public static OpcodeLayout LianLi { get; } = new(0, 1, 4, 5, new byte[] { 14, 16 });

    /// <summary>HID path: byte 0 is the report ID, everything else shifts by one.</summary>
    public static OpcodeLayout LianLiHid { get; } = new(1, 2, 5, 6, new byte[] { 14, 16 });

    public bool IsRelay(byte usbCmd) => RelayUsbCmds.Contains(usbCmd);

    /// <summary>Diagnostics opcode: USB_CMD, or (USB_CMD &lt;&lt; 8) | RF_CMD for the first chunk of a relay packet.</summary>
    public int OpcodeOf(ReadOnlySpan<byte> frame)
    {
        if (frame.Length <= UsbCmdOffset)
        {
            return -1;
        }

        var usb = frame[UsbCmdOffset];
        return IsRelay(usb) && frame.Length > RfCmdOffset && frame[ChunkIndexOffset] == 0 ? (usb << 8) | frame[RfCmdOffset] : usb;
    }
}
