using System.Buffers.Binary;
using UnifiedRgb.Devices.AsusRog;

namespace UnifiedRgb.Devices.Tests;

/// <summary>
/// Owner correction: the GYM1 build field is a diagnostic tag, not protocol identity. The same structurally valid live
/// capability parses identically whatever the tag says; incompatible grammar or shape is rejected whatever the tag says.
/// </summary>
public sealed partial class RuntimeCapabilityCompatibilityTests
{
    [Theory]
    [InlineData(0x25314D51u)] [InlineData(0x25314D47u)] [InlineData(0x25314D48u)] [InlineData(0x00314D49u)]
    [InlineData(0x25324E49u)] [InlineData(0u)] [InlineData(uint.MaxValue)] [InlineData(0x44434241u)]
    public void Capability_build_field_is_a_diagnostic_tag_and_never_admission(uint tag)
    {
        foreach (var transport in new byte[] { 1, 2 })
        {
            var reference = RogNativeMenuCodec.ParseCapability(Capability(transport: transport), 0x12, transport);
            var idle = Capability(transport: transport); BinaryPrimitives.WriteUInt32LittleEndian(idle.AsSpan(29), tag);
            RogNativeMenuCodec.ParseCapability(idle, 0x12, transport).Should().Be(reference);
            var armed = Capability(transport: transport, flags: 49, status: 1, command: 0x71);
            BinaryPrimitives.WriteUInt32LittleEndian(armed.AsSpan(29), tag);
            var live = RogNativeMenuCodec.ParseCapability(armed, 0x71, transport);
            var state = new RogNativeMenuState();
            state.Arm(reference, live, live.Cookie);
            state.Matches(live).Should().BeTrue();
        }
    }

    [Theory]
    [InlineData("magic", 0x25314D49u)] [InlineData("magic", 0x25314D51u)] [InlineData("proto1", 0x25314D49u)] [InlineData("proto1", 0x25314D51u)]
    [InlineData("mode10", 0x25314D49u)] [InlineData("profile6", 0x25314D51u)] [InlineData("transport", 0x25314D49u)]
    [InlineData("missing-artwork", 0x25314D50u)] [InlineData("width", 0x25314D49u)] [InlineData("chunk", 0u)]
    [InlineData("layout", 0x25314D50u)] [InlineData("padding", 0x25314D49u)] [InlineData("epoch0", 0x25314D49u)]
    public void Incompatible_capability_grammar_is_rejected_whatever_the_build_tag_says(string fault, uint tag)
    {
        var p = Capability(); BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(29), tag);
        byte transport = 1;
        switch (fault)
        {
            case "magic": p[5] = (byte)'X'; break;
            case "proto1": p[9] = 1; break;
            case "mode10": p[11] = 10; break;
            case "profile6": p[12] = 6; break;
            case "transport": transport = 2; break;
            case "missing-artwork": p[14] = 16; break;
            case "width": p[33] = 207; break;
            case "chunk": p[36] = 59; break;
            case "layout": p[37] = 2; break;
            case "padding": p[^1] = 1; break;
            default: p.AsSpan(15, 4).Clear(); break;
        }
        ((Action)(() => RogNativeMenuCodec.ParseCapability(p, 0x12, transport))).Should().Throw<InvalidDataException>();
    }
}
