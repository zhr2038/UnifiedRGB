using UnifiedRgb.Transports.Safety;
using UnifiedRgb.Transports.Smbus;

namespace UnifiedRgb.Safety.Tests;

/// <summary>
/// NATIVE design §5.3, §10 item 6: the default-deny "EneDram" SMBus firewall over compound operations. No bus here; the
/// firewall is judged on its decisions alone.
/// </summary>
public class SmbusFirewallTests
{
    private static SmbusFirewall Make(bool remap = false) => new(EneRegisters.PhysicalCandidates, remap);

    [Theory]
    [InlineData(0x50)]
    [InlineData(0x57)]
    [InlineData(0x40)]
    [InlineData(0x78)]
    public void Construction_refuses_spd_and_non_physical_candidates(byte address)
    {
        var act = () => new SmbusFirewall([0x77, address], remapEnabled: false);
        act.Should().Throw<ArgumentException>().WithMessage("*not an ENE DRAM candidate*");
    }

    [Fact]
    public void Physical_candidates_are_the_reference_list_and_never_spd()
    {
        EneRegisters.PhysicalCandidates.Should().Equal(
            (byte)0x70, (byte)0x71, (byte)0x72, (byte)0x73, (byte)0x74, (byte)0x75, (byte)0x76, (byte)0x77,
            (byte)0x4F, (byte)0x66, (byte)0x67, (byte)0x39, (byte)0x3A, (byte)0x3B, (byte)0x3C, (byte)0x3D);
        EneRegisters.PhysicalCandidates.Should().NotContain(a => EneRegisters.IsSpd(a));
    }

    [Fact]
    public void Probe_and_signature_reads_only_during_discovery_and_only_the_reference_commands()
    {
        var fw = Make();
        fw.Check(SmbusCompound.Probe(0x77)).Should().Be("probe outside discovery");
        fw.Check(SmbusCompound.ReadSig(0x77, 0xA0)).Should().Be("signature read outside discovery");
        fw.DiscoveryOpen = true;
        fw.Check(SmbusCompound.Probe(0x77)).Should().BeNull();
        fw.Check(SmbusCompound.Probe(0x50)).Should().Contain("not a candidate", "SPD is never probed");
        fw.Check(SmbusCompound.ReadSig(0x77, 0x00)).Should().BeNull();
        for (var c = 0xA0; c <= 0xAF; c++)
        {
            fw.Check(SmbusCompound.ReadSig(0x77, (byte)c)).Should().BeNull();
        }

        fw.Check(SmbusCompound.ReadSig(0x77, 0x9F)).Should().Contain("denied");
        fw.Check(SmbusCompound.ReadSig(0x77, 0xB0)).Should().Contain("denied");
        fw.Check(SmbusCompound.ReadSig(0x77, 0x81)).Should().Contain("denied");
    }

    [Theory]
    [InlineData(0x1000, true)]
    [InlineData(0x100F, true)]
    [InlineData(0x1010, false)]
    [InlineData(0x0FFF, false)]
    [InlineData(0x1030, true)]
    [InlineData(0x1035, true)]
    [InlineData(0x1036, false)]
    [InlineData(0x1C00, true)]
    [InlineData(0x1C3F, true)]
    [InlineData(0x1C40, false)]
    [InlineData(0x8020, true)]
    [InlineData(0x8021, true)]
    [InlineData(0x8022, false)]
    [InlineData(0x80A0, false)]
    [InlineData(0x8000, false)]
    public void Register_reads_only_in_the_identity_and_mode_ranges(int register, bool admitted)
    {
        var verdict = Make().Check(SmbusCompound.RegRead(0x77, (ushort)register));
        if (admitted)
        {
            verdict.Should().BeNull();
        }
        else
        {
            verdict.Should().Contain("not readable");
        }
    }

    [Fact]
    public void Direct_and_apply_writes_need_a_discovered_controller_and_never_save()
    {
        var fw = Make();
        fw.Check(SmbusCompound.RegWrite(0x77, EneRegisters.Direct, 1)).Should().Contain("not a discovered controller");
        fw.Check(SmbusCompound.RegWrite(0x77, EneRegisters.Apply, 1)).Should().Contain("not a discovered controller");
        fw.SetDirect(0x77, EneRegisters.DirectV2, 8);
        fw.Check(SmbusCompound.RegWrite(0x77, EneRegisters.Direct, 1)).Should().BeNull();
        fw.Check(SmbusCompound.RegWrite(0x77, EneRegisters.Direct, 0)).Should().BeNull();
        fw.Check(SmbusCompound.RegWrite(0x77, EneRegisters.Direct, 2)).Should().Contain("not 0 or 1");
        fw.Check(SmbusCompound.RegWrite(0x77, EneRegisters.Apply, 0x01)).Should().BeNull();
        fw.Check(SmbusCompound.RegWrite(0x77, EneRegisters.Apply, 0xAA)).Should().Contain("never save");
        fw.Check(SmbusCompound.RegWrite(0x77, EneRegisters.Mode, 1)).Should().Contain("not writable");
        fw.Check(SmbusCompound.RegWrite(0x77, EneRegisters.EffectV2, 1)).Should().Contain("not writable");
        fw.Check(SmbusCompound.RegWrite(0x71, EneRegisters.Direct, 1)).Should().Contain("not a discovered controller");
        fw.ForgetDirect(0x77);
        fw.Check(SmbusCompound.RegWrite(0x77, EneRegisters.Direct, 1)).Should().Contain("not a discovered controller");
    }

    [Fact]
    public void Remap_registers_only_at_0x77_with_the_gate_and_only_candidate_targets()
    {
        var off = Make(remap: false);
        off.Check(SmbusCompound.RegWrite(0x77, EneRegisters.SlotIndex, 0)).Should().Contain("RemapOnOpen");
        off.Check(SmbusCompound.RegWrite(0x77, EneRegisters.I2cAddress, 0x70 << 1)).Should().Contain("RemapOnOpen");

        var on = Make(remap: true);
        on.Check(SmbusCompound.RegWrite(0x77, EneRegisters.SlotIndex, 0)).Should().BeNull();
        on.Check(SmbusCompound.RegWrite(0x77, EneRegisters.SlotIndex, 7)).Should().BeNull();
        on.Check(SmbusCompound.RegWrite(0x77, EneRegisters.SlotIndex, 8)).Should().Contain("not 0..7");
        on.Check(SmbusCompound.RegWrite(0x71, EneRegisters.SlotIndex, 0)).Should().Contain("only at 0x77");
        on.Check(SmbusCompound.RegWrite(0x77, EneRegisters.I2cAddress, 0x70 << 1)).Should().BeNull();
        on.Check(SmbusCompound.RegWrite(0x77, EneRegisters.I2cAddress, 0x3D << 1)).Should().BeNull();
        on.Check(SmbusCompound.RegWrite(0x77, EneRegisters.I2cAddress, 0x77 << 1)).Should().Contain("not (candidate << 1)");
        on.Check(SmbusCompound.RegWrite(0x77, EneRegisters.I2cAddress, 0x50 << 1)).Should().Contain("not (candidate << 1)", "SPD is never a target");
        on.Check(SmbusCompound.RegWrite(0x77, EneRegisters.I2cAddress, (0x70 << 1) | 1)).Should().Contain("not (candidate << 1)");
        on.Check(SmbusCompound.RegWrite(0x72, EneRegisters.I2cAddress, 0x70 << 1)).Should().Contain("only at 0x77");

        var narrowed = new SmbusFirewall([0x77, 0x71], remapEnabled: true);
        narrowed.Check(SmbusCompound.RegWrite(0x77, EneRegisters.I2cAddress, 0x70 << 1)).Should().Contain("not (candidate << 1)", "configuration narrows the targets");
        narrowed.Check(SmbusCompound.RegWrite(0x77, EneRegisters.I2cAddress, 0x71 << 1)).Should().BeNull();
    }

    [Fact]
    public void Colour_blocks_and_bytes_stay_inside_the_registered_direct_range()
    {
        var fw = Make();
        fw.Check(SmbusCompound.RegBlock(0x77, 0x8100, [1, 2, 3])).Should().Contain("not a discovered controller");
        fw.SetDirect(0x77, EneRegisters.DirectV2, 8); // [0x8100, 0x8118)
        fw.Check(SmbusCompound.RegBlock(0x77, 0x8100, [1, 2, 3])).Should().BeNull();
        fw.Check(SmbusCompound.RegBlock(0x77, 0x8115, [1, 2, 3])).Should().BeNull();
        fw.Check(SmbusCompound.RegBlock(0x77, 0x8116, [1, 2, 3])).Should().Contain("outside direct range", "crosses the end");
        fw.Check(SmbusCompound.RegBlock(0x77, 0x8118, [1])).Should().Contain("outside direct range");
        fw.Check(SmbusCompound.RegBlock(0x77, 0x80FF, [1])).Should().Contain("outside direct range");
        fw.Check(SmbusCompound.RegBlock(0x77, 0x8000, [1, 2, 3])).Should().Contain("outside direct range", "V1 base is not this controller's");
        fw.Check(SmbusCompound.RegWrite(0x77, 0x8117, 9)).Should().BeNull("single-byte fallback inside the range");
        fw.Check(SmbusCompound.RegWrite(0x77, 0x8118, 9)).Should().Contain("not writable");
        var big = () => SmbusCompound.RegBlock(0x77, 0x8100, [1, 2, 3, 4]);
        big.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(0x8000, 5, true)]
    [InlineData(0x8000, 6, false)]
    [InlineData(0x8100, 32, true)]
    [InlineData(0x8100, 33, false)]
    [InlineData(0x8100, 0, false)]
    [InlineData(0x8010, 1, false)]
    [InlineData(0x8020, 1, false)]
    public void Direct_range_registration_never_reaches_effect_or_control_registers(int directBase, int leds, bool admitted)
    {
        var act = () => Make().SetDirect(0x77, (ushort)directBase, leds);
        if (admitted)
        {
            act.Should().NotThrow();
        }
        else
        {
            act.Should().Throw<ArgumentException>();
        }
    }

    [Fact]
    public void Max_direct_leds_per_version()
    {
        EneRegisters.MaxDirectLeds(EneRegisters.DirectV1).Should().Be(5);
        EneRegisters.MaxDirectLeds(EneRegisters.DirectV2).Should().Be(32);
        EneRegisters.MaxDirectLeds(0x8010).Should().Be(0);
    }

    [Fact]
    public void Ensure_throws_and_counts_every_denial()
    {
        var fw = Make();
        var act = () => fw.Ensure(SmbusCompound.RegWrite(0x77, EneRegisters.Apply, 0xAA));
        act.Should().Throw<ForbiddenSmbusException>().WithMessage("*refused*");
        act.Should().Throw<ForbiddenSmbusException>();
        fw.Denied.Should().Be(2);
        fw.SetDirect(0x77, EneRegisters.DirectV1, 5);
        fw.Ensure(SmbusCompound.RegWrite(0x77, EneRegisters.Apply, 0x01));
        fw.Denied.Should().Be(2);
    }

    [Theory]
    [InlineData(0x8000, 0x0080)]
    [InlineData(0x1C00, 0x001C)]
    [InlineData(0x80F9, 0xF980)]
    [InlineData(0x1035, 0x3510)]
    public void Pointer_word_is_the_register_byte_swapped(int register, int word) =>
        EneRegisters.PointerWord((ushort)register).Should().Be((ushort)word);

    [Fact]
    public void Wire_frames_carry_kind_address_register_and_data()
    {
        SmbusCompound.Probe(0x77).ToWire().Should().Equal(1, 0x77);
        SmbusCompound.ReadSig(0x77, 0xA3).ToWire().Should().Equal(2, 0x77, 0xA3);
        SmbusCompound.RegRead(0x77, 0x1C02).ToWire().Should().Equal(3, 0x77, 0x1C, 0x02);
        SmbusCompound.RegWrite(0x77, 0x8020, 1).ToWire().Should().Equal(4, 0x77, 0x80, 0x20, 1);
        SmbusCompound.RegBlock(0x77, 0x8103, [0x10, 0x30, 0x20]).ToWire().Should().Equal(5, 0x77, 0x81, 0x03, 3, 0x10, 0x30, 0x20);
        SmbusCompound.RegBlock(0x77, 0x8103, [0x10]).Should().NotBe(SmbusCompound.RegBlock(0x77, 0x8103, [0x11]));
    }
}
