using UnifiedRgb.Devices.LianLi.Protocol;
using UnifiedRgb.Transports.Safety;

namespace UnifiedRgb.Safety.Tests;

public class OpcodeFirewallTests
{
    private static readonly byte[] DeniedUsb = [32, 33, 34, 35];
    private static readonly byte[] DeniedRf = [23, 24, 33];

    private static (OpcodeFirewall Fw, RecordingTransport Sink) Make()
    {
        var sink = new RecordingTransport();
        return (new OpcodeFirewall(sink, OpcodeLayout.LianLi), sink);
    }

    /// <summary>Chunk 0 of an RF packet as <c>MasterDevice.SendRfData</c> builds it: [usb][0][channel][rx][18][rf]…</summary>
    private static byte[] First(byte usb, byte rf) => [usb, 0, 8, 1, OpcodeLayout.RfPacketTag, rf, 0];

    private static byte[] Chunk(byte index) => [16, index, 8, 1, 23, 24, 33];

    [Theory]
    [InlineData(32)]
    [InlineData(33)]
    [InlineData(34)]
    [InlineData(35)]
    public async Task Denied_usb_opcode_throws_and_never_reaches_transport(byte op)
    {
        var (fw, sink) = Make();
        var act = async () => await fw.SendAsync(new byte[] { op, 0, 0, 0 }, CancellationToken.None);
        await act.Should().ThrowAsync<ForbiddenOpcodeException>();
        sink.Frames.Should().BeEmpty();
    }

    [Theory]
    [InlineData(14, 23)]
    [InlineData(14, 24)]
    [InlineData(14, 33)]
    [InlineData(16, 23)]
    [InlineData(16, 24)]
    [InlineData(16, 33)]
    public async Task Denied_rf_opcode_inside_relay_throws(byte relay, byte rf)
    {
        var (fw, sink) = Make();
        var act = async () => await fw.SendAsync(First(relay, rf), CancellationToken.None);
        await act.Should().ThrowAsync<ForbiddenOpcodeException>();
        sink.Frames.Should().BeEmpty();
    }

    [Fact]
    public async Task Every_non_denied_usb_opcode_passes()
    {
        var (fw, sink) = Make();
        var sent = 0;
        for (var op = 0; op < 256; op++)
        {
            if (DeniedUsb.Contains((byte)op))
            {
                continue;
            }

            await fw.SendAsync(OpcodeLayout.LianLi.IsRelay((byte)op) ? First((byte)op, 0x12) : new byte[] { (byte)op, 0x12, 0 }, CancellationToken.None);
            sent++;
        }

        sink.SentFrames.Should().HaveCount(sent).And.HaveCount(252);
    }

    [Fact]
    public async Task Every_non_denied_rf_opcode_passes_in_both_relay_kinds()
    {
        var (fw, sink) = Make();
        foreach (var relay in new byte[] { 14, (byte)UsbCmd.SendRf })
        {
            for (var op = 0; op < 256; op++)
            {
                if (!DeniedRf.Contains((byte)op))
                {
                    await fw.SendAsync(First(relay, (byte)op), CancellationToken.None);
                }
            }
        }

        sink.SentFrames.Should().HaveCount(2 * 253);
    }

    [Fact]
    public async Task All_compiled_allowed_opcodes_pass()
    {
        var (fw, sink) = Make();
        foreach (var u in Enum.GetValues<UsbCmd>())
        {
            await fw.SendAsync(u == UsbCmd.SendRf ? First((byte)u, 20) : new byte[] { (byte)u, 0 }, CancellationToken.None);
        }

        foreach (var r in Enum.GetValues<RfCmd>())
        {
            await fw.SendAsync(First((byte)UsbCmd.SendRf, (byte)r), CancellationToken.None);
        }

        sink.SentFrames.Should().HaveCount(Enum.GetValues<UsbCmd>().Length + Enum.GetValues<RfCmd>().Length);
    }

    [Fact]
    public void Allowed_enums_contain_no_denied_value()
    {
        Enum.GetValues<UsbCmd>().Select(v => (byte)v).Should().NotIntersectWith(DeniedUsb);
        Enum.GetValues<RfCmd>().Select(v => (byte)v).Should().NotIntersectWith(DeniedRf);
    }

    [Fact]
    public async Task Empty_or_truncated_relay_frame_fails_closed()
    {
        var (fw, _) = Make();
        foreach (var bad in new[] { Array.Empty<byte>(), new byte[] { 16 }, new byte[] { 16, 0, 8, 1, 18 } })
        {
            var act = async () => await fw.SendAsync(bad, CancellationToken.None);
            await act.Should().ThrowAsync<ForbiddenOpcodeException>();
        }
    }

    [Fact]
    public async Task First_chunk_without_the_rf_packet_tag_or_with_an_index_above_3_is_refused()
    {
        var (fw, sink) = Make();
        var untagged = async () => await fw.SendAsync(new byte[] { 16, 0, 8, 1, 0, 32 }, CancellationToken.None);
        await untagged.Should().ThrowAsync<ForbiddenOpcodeException>().WithMessage("*tag*");
        await fw.SendAsync(First(16, 32), CancellationToken.None);
        var index4 = async () => await fw.SendAsync(Chunk(4), CancellationToken.None);
        await index4.Should().ThrowAsync<ForbiddenOpcodeException>();
        sink.SentFrames.Should().ContainSingle();
    }

    [Fact]
    public async Task Continuation_chunks_pass_only_in_order_after_a_checked_first_chunk()
    {
        var (fw, sink) = Make();
        var orphan = async () => await fw.SendAsync(Chunk(1), CancellationToken.None);
        await orphan.Should().ThrowAsync<ForbiddenOpcodeException>().WithMessage("*does not continue*");

        await fw.SendAsync(First(16, 32), CancellationToken.None);
        for (byte i = 1; i <= 3; i++)
        {
            await fw.SendAsync(Chunk(i), CancellationToken.None); // continuation bytes may hold any value, even 23/24/33
        }

        var replay = async () => await fw.SendAsync(Chunk(3), CancellationToken.None);
        await replay.Should().ThrowAsync<ForbiddenOpcodeException>();

        await fw.SendAsync(First(16, 20), CancellationToken.None);
        await fw.SendAsync(new byte[] { 17, 8 }, CancellationToken.None); // any other frame ends the packet
        var afterOther = async () => await fw.SendAsync(Chunk(1), CancellationToken.None);
        await afterOther.Should().ThrowAsync<ForbiddenOpcodeException>();

        await fw.SendAsync(First(16, 20), CancellationToken.None);
        var skip = async () => await fw.SendAsync(Chunk(2), CancellationToken.None);
        await skip.Should().ThrowAsync<ForbiddenOpcodeException>();
        sink.SentFrames.Should().HaveCount(7);
    }

    [Fact]
    public async Task Rejected_first_chunk_does_not_open_a_sequence()
    {
        var (fw, sink) = Make();
        var denied = async () => await fw.SendAsync(First(16, 24), CancellationToken.None);
        await denied.Should().ThrowAsync<ForbiddenOpcodeException>();
        var cont = async () => await fw.SendAsync(Chunk(1), CancellationToken.None);
        await cont.Should().ThrowAsync<ForbiddenOpcodeException>();
        sink.SentFrames.Should().BeEmpty();
    }

    [Fact]
    public async Task Hid_layout_checks_after_report_id()
    {
        var sink = new RecordingTransport();
        var fw = new OpcodeFirewall(sink, OpcodeLayout.LianLiHid);
        var act = async () => await fw.SendAsync(new byte[] { 0xE0, 33, 0 }, CancellationToken.None);
        await act.Should().ThrowAsync<ForbiddenOpcodeException>();
        var relay = async () => await fw.SendAsync(new byte[] { 0xE0, 16, 0, 8, 1, 18, 24 }, CancellationToken.None);
        await relay.Should().ThrowAsync<ForbiddenOpcodeException>();
        await fw.SendAsync(new byte[] { 0xE0, 16, 0, 8, 1, 18, 32 }, CancellationToken.None);
        sink.SentFrames.Should().ContainSingle();
    }

    [Fact]
    public void Diagnostics_opcode_names_the_rf_command_on_the_first_chunk_only()
    {
        OpcodeLayout.LianLi.OpcodeOf(First(16, 32)).Should().Be((16 << 8) | 32);
        OpcodeLayout.LianLi.OpcodeOf(Chunk(2)).Should().Be(16);
        OpcodeLayout.LianLi.OpcodeOf(new byte[] { 17, 8 }).Should().Be(17);
    }
}
