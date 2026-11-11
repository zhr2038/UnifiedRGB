using System.Buffers.Binary;
using UnifiedRgb.Devices.AsusRog;

namespace UnifiedRgb.Devices.Tests;

public sealed class CodexPageContractTests
{
    [Theory]
    [InlineData((byte)1)] [InlineData((byte)2)]
    public void HomeAssistant_requires_its_own_capability_and_preserves_older_flag_semantics(byte transport)
    {
        var packet = Cap(11, transport, 0x70);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(29), 0x25314841);
        Action read = () => RogNativeMenuCodec.ParseCapability(packet, 0x12, transport);
        read.Should().Throw<InvalidDataException>();
        packet[14] |= 0x80;
        var ha = RogNativeMenuCodec.ParseCapability(packet, 0x12, transport);
        RogNativeMenuCodec.ArmRequest(ha, 123)[25].Should().Be(11);
        packet[11] = 10;
        RogNativeMenuCodec.ParseCapability(packet, 0x12, transport).Flags.Should().Be(0x70);
        packet[11] = 12;
        read.Should().Throw<InvalidDataException>();
    }

    [Theory]
    [InlineData((byte)1)] [InlineData((byte)2)]
    public void Old_modes_remain_admitted_and_diagnostic_build_never_enables10(byte transport)
    {
        for (byte mode = 0; mode <= 9; mode++)
            RogNativeMenuCodec.ParseCapability(Cap(mode, transport, 0x30), 0x12, transport).Mode.Should().Be(mode);
        foreach (var build in new[] { 0u, 0x25314D49u, 0x25314D50u, uint.MaxValue })
        {
            var packet = Cap(10, transport, 0x30); BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(29), build);
            ((Action)(() => RogNativeMenuCodec.ParseCapability(packet, 0x12, transport))).Should().Throw<InvalidDataException>();
            packet[14] |= 0x40;
            var admitted = RogNativeMenuCodec.ParseCapability(packet, 0x12, transport);
            RogNativeMenuCodec.ArmRequest(admitted, 123)[25].Should().Be(10);
        }
    }

    [Theory]
    [InlineData("unknown")] [InlineData("profile")] [InlineData("protocol")] [InlineData("width")]
    [InlineData("height")] [InlineData("payload")] [InlineData("semantic")] [InlineData("epoch")]
    [InlineData("cookie")] [InlineData("trailing")] [InlineData("inEpoch")] [InlineData("status")]
    public void Dedicated_flag_does_not_bypass_any_existing_grammar_guard(string mutation)
    {
        var p = Cap(10, 1, 0x70);
        switch (mutation)
        {
            case "unknown": p[11] = 11; break;
            case "profile": p[12] = 6; break;
            case "protocol": p[9] = 3; break;
            case "width": p[33] = 207; break;
            case "height": p[35] = 63; break;
            case "payload": p[36] = 59; break;
            case "semantic": p[37] = 2; break;
            case "epoch": BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(15), uint.MaxValue); break;
            case "cookie": p[19] = 1; break;
            case "trailing": p[64] = 1; break;
            case "inEpoch": p[14] &= 0xDF; break;
            case "status": p[10] = 2; break;
        }
        ((Action)(() => RogNativeMenuCodec.ParseCapability(p, 0x12, 1))).Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void State_requires_exact_page_cookie_sequence_and_never_returns_a_Codex_control_action()
    {
        var prior = RogNativeMenuCodec.ParseCapability(Cap(10, 1, 0x70), 0x12, 1);
        var armed = prior with { Epoch = 2, Status = 1, Cookie = 123, Flags = 0x71 };
        var state = new RogNativeMenuState();
        ((Action)(() => state.Arm(prior, armed with { Mode = 9 }, 123))).Should().Throw<InvalidDataException>();
        state.Arm(prior, armed, 123);
        state.Matches(armed with { Mode = 9 }).Should().BeFalse();
        state.Matches(armed with { Flags = 0x31 }).Should().BeFalse();
        state.Accept(RogNativeNoticeKind.Tagged, new(0, 10, 0, 3, 2, 123, 1)).Should().Be(RogNativeDecision.Ignore);
        state.Accept(RogNativeNoticeKind.Tagged, new(2, 10, 0, 1, 2, 123, 2)).Should().Be(RogNativeDecision.Ignore);
        state.Accept(RogNativeNoticeKind.Tagged, new(0, 9, 0, 1, 2, 123, 3)).Should().Be(RogNativeDecision.Disarmed);
        state.Arm(prior, armed, 123);
        state.Accept(RogNativeNoticeKind.Tagged, new(0, 10, 0, 3, 2, 124, 1)).Should().Be(RogNativeDecision.Disarmed);
        state.Arm(prior, armed, 123);
        state.Accept(RogNativeNoticeKind.Tagged, new(0, 10, 0, 3, 2, 123, 1));
        state.Accept(RogNativeNoticeKind.Tagged, new(0, 10, 0, 3, 2, 123, 1)).Should().Be(RogNativeDecision.Disarmed);
    }

    private static byte[] Cap(byte mode, byte transport, byte flags)
    {
        var p = new byte[65]; p[1] = 0x12; p[2] = 0x7D; "GYM1"u8.CopyTo(p.AsSpan(5));
        p[9] = 2; p[11] = mode; p[13] = transport; p[14] = flags;
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(15), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(33), 208); p[35] = 64; p[36] = transport == 1 ? (byte)60 : (byte)59; p[37] = 3;
        return p;
    }
}
