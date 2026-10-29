using UnifiedRgb.Devices.AsusRog;

namespace UnifiedRgb.Devices.Tests;

public sealed class RogLeverDecoderTests
{
    private static byte[] Frame(byte mask, byte opcode = 0x72)
    {
        var frame = new byte[21]; frame[0] = 4; frame[1] = opcode; frame[2] = mask; return frame;
    }
    private static RogLeverDecoder Armed()
    {
        var decoder = new RogLeverDecoder(); decoder.Begin(1); decoder.Read(Frame(0), 1, TimeSpan.Zero); return decoder;
    }

    [Theory]
    [InlineData(1, RogLeverDirection.Up)]
    [InlineData(2, RogLeverDirection.Middle)]
    [InlineData(4, RogLeverDirection.Down)]
    public void Owner_verified_masks_return_one_edge_after_real_neutral(byte mask, RogLeverDirection direction)
    {
        var decoder = Armed();
        decoder.Read(Frame(mask), 1, TimeSpan.FromMilliseconds(10)).Should().Be(direction);
        decoder.Read(Frame(mask), 1, TimeSpan.FromMilliseconds(20)).Should().BeNull();
        decoder.Read(Frame(0), 1, TimeSpan.FromMilliseconds(30)).Should().BeNull();
        decoder.Read(Frame(mask), 1, TimeSpan.FromMilliseconds(40)).Should().Be(direction);
    }

    [Fact]
    public void Initial_or_reconnected_press_is_not_a_fresh_action_until_a_real_release()
    {
        var decoder = new RogLeverDecoder();
        decoder.Read(Frame(1), 1, TimeSpan.Zero).Should().BeNull();
        decoder.Begin(1); decoder.Read(Frame(1), 1, TimeSpan.Zero).Should().BeNull();
        decoder.Readiness.Should().Be(RogLeverReadiness.AwaitingNeutral);
        decoder.Read(Frame(0), 1, TimeSpan.FromSeconds(1));
        decoder.Read(Frame(1), 1, TimeSpan.FromSeconds(2)).Should().Be(RogLeverDirection.Up);
        decoder.Begin(2); decoder.Read(Frame(1), 2, TimeSpan.Zero).Should().BeNull();
    }

    [Fact]
    public void Long_hold_and_read_time_gaps_do_not_repeat_or_invent_release()
    {
        var decoder = Armed();
        decoder.Read(Frame(1), 1, TimeSpan.FromSeconds(1)).Should().Be(RogLeverDirection.Up);
        decoder.Read(Frame(1), 1, TimeSpan.FromSeconds(200)).Should().BeNull();
        decoder.Readiness.Should().Be(RogLeverReadiness.Held);
        decoder.Read(Frame(0), 1, TimeSpan.FromSeconds(201)).Should().BeNull();
        decoder.Read(Frame(1), 1, TimeSpan.FromSeconds(202)).Should().Be(RogLeverDirection.Up);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(255)]
    public void Unverified_multi_bit_or_unknown_mask_faults_without_an_action(byte mask)
    {
        var decoder = Armed(); decoder.Read(Frame(mask), 1, TimeSpan.Zero).Should().BeNull();
        decoder.Readiness.Should().Be(RogLeverReadiness.Faulted);
        decoder.Read(Frame(0), 1, TimeSpan.FromSeconds(1));
        decoder.Read(Frame(1), 1, TimeSpan.FromSeconds(2)).Should().BeNull();
    }

    [Fact]
    public void Malformed_extension_wrong_report_id_and_native_length_fault()
    {
        var extension = Frame(1); extension[20] = 1;
        var wrongRid = Frame(1); wrongRid[0] = 3;
        foreach (var report in new[] { extension, wrongRid, new byte[20], new byte[22], Array.Empty<byte>() })
        {
            var decoder = Armed(); decoder.Read(report, 1, TimeSpan.Zero).Should().BeNull();
            decoder.Readiness.Should().Be(RogLeverReadiness.Faulted);
        }
    }

    [Fact]
    public void Ordinary_report_content_and_mode_notifications_never_become_gestures()
    {
        var decoder = Armed();
        foreach (var opcode in new byte[] { 0x61, 0x71, 0x91, 0xFF })
        {
            var report = Frame(1, opcode); report.AsSpan(3).Fill(0xAA);
            decoder.Read(report, 1, TimeSpan.Zero).Should().BeNull();
            decoder.Readiness.Should().Be(RogLeverReadiness.Ready);
        }
    }

    [Fact]
    public void Direction_change_without_a_real_release_is_rejected()
    {
        var decoder = Armed(); decoder.Read(Frame(1), 1, TimeSpan.Zero);
        decoder.Read(Frame(4), 1, TimeSpan.FromSeconds(1)).Should().BeNull();
        decoder.Readiness.Should().Be(RogLeverReadiness.Faulted);
    }

    [Fact]
    public void Wrong_generation_backwards_time_and_explicit_disarm_cannot_reuse_a_press()
    {
        var decoder = Armed(); decoder.Read(Frame(1), 2, TimeSpan.Zero).Should().BeNull();
        decoder.Readiness.Should().Be(RogLeverReadiness.Faulted);
        decoder.Begin(3); decoder.Read(Frame(0), 3, TimeSpan.FromSeconds(3));
        decoder.Read(Frame(1), 3, TimeSpan.FromSeconds(2)).Should().BeNull();
        decoder.Readiness.Should().Be(RogLeverReadiness.Faulted);
        decoder.Begin(4); decoder.Read(Frame(0), 4, TimeSpan.Zero); decoder.Disarm();
        decoder.Read(Frame(1), 4, TimeSpan.FromSeconds(1)).Should().BeNull();
    }
}
