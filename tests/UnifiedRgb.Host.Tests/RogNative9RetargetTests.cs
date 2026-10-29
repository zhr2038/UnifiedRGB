using System.Text.Json;
using UnifiedRgb.Devices.AsusRog;
using UnifiedRgb.Host.Services;

namespace UnifiedRgb.Host.Tests;

public sealed partial class RogNativeMenuSourceTests
{
    [Fact]
    public void Pinned_actual_C_fields_decode_and_ARM_reencodes_byte_exact_for_both_transports_and_all_raw_profiles()
    {
        using var stream = typeof(RogNativeMenuSourceTests).Assembly.GetManifestResourceStream("Actual25Producer.json")!;
        using var json = JsonDocument.Parse(stream);
        json.RootElement.GetProperty("sourceSha256").GetString().Should().Be("e86a58a91f09aa326f25900ee48d549b6d7a474a1d33e5f87902758b893c8e3e");
        var contexts = new HashSet<(byte, byte)>();
        foreach (var item in json.RootElement.GetProperty("cases").EnumerateArray())
        {
            var transport = item.GetProperty("transport").GetByte();
            var profile = item.GetProperty("rawProfile").GetByte(); contexts.Add((transport, profile));
            var capability = RogNativeMenuCodec.ParseCapability(Convert.FromHexString(item.GetProperty("capability65").GetString()!), 0x12, transport);
            capability.Mode.Should().Be(RogNativeMenuCodec.NativeMode); capability.Profile.Should().Be(profile);
            RogNativeMenuCodec.ArmRequest(capability, Cookie).Should().Equal(Convert.FromHexString(item.GetProperty("armRequest65").GetString()!));
            var reply = RogNativeMenuCodec.ParseCapability(Convert.FromHexString(item.GetProperty("armReply65").GetString()!), 0x71, transport);
            var state = new RogNativeMenuState(); state.Arm(capability, reply, Cookie);
            var decisions = new List<RogNativeDecision>();
            foreach (var wire in item.GetProperty("events21").EnumerateArray())
            {
                var kind = RogNativeMenuCodec.ParseNotice(Convert.FromHexString(wire.GetString()!), transport, out var notice);
                decisions.Add(state.Accept(kind, notice));
            }
            decisions.Should().Equal(RogNativeDecision.Ignore, RogNativeDecision.Ignore);
        }
        contexts.Should().BeEquivalentTo(from byte transport in new byte[] { 1, 2 } from byte profile in Enumerable.Range(0, 6).Select(p => (byte)p) select (transport, profile));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    [InlineData(5)] [InlineData(6)] [InlineData(7)] [InlineData(8)]
    public void Stock_or_temporary_mode_cannot_arm_or_dispatch_tagged_case_action(byte mode)
    {
        var prior = new RogNativeCapability(0, 9, 5, 1, 48, 1, 0, 0);
        var next = new RogNativeCapability(1, 9, 5, 1, 49, 2, Cookie, 0);
        ((Action)(() => RogNativeMenuCodec.ArmRequest(prior with { Mode = mode }, Cookie))).Should().Throw<InvalidDataException>();
        var state = new RogNativeMenuState(); state.Arm(prior, next, Cookie);
        state.Accept(RogNativeNoticeKind.Tagged, new(0, 9, 5, 3, 2, Cookie, 1)).Should().Be(RogNativeDecision.Ignore);
        state.Accept(RogNativeNoticeKind.Tagged, new(1, mode, 5, 1, 2, Cookie, 2)).Should().Be(RogNativeDecision.Disarmed);
        state.Scope.Should().BeNull();
        var ordinary = new byte[21]; ordinary[0] = 3; ordinary[1] = 0x91; ordinary[4] = mode;
        RogNativeMenuCodec.ParseNotice(ordinary, 1, out _).Should().Be(RogNativeNoticeKind.OutsideNative);
    }

    [Theory]
    [InlineData(1)] [InlineData(2)]
    public async Task Fake_repeated_up_down_middle_remain_neutral_gated_and_nonduplicating(byte transport)
    {
        await using var rig = new Rig(transport); rig.Source.RequestEnabled(true);
        (await rig.Actor.StepAsync(Pixels, default)).Should().BeTrue();
        byte[] masks = [0, 1, 0, 4, 0, 2, 0, 1];
        for (ushort i = 0; i < masks.Length; i++) rig.Input.Send(Notice((ushort)(i + 1), masks[i], i == 0 ? (byte)3 : (byte)1, transport));
        await rig.Input.WaitReads(masks.Length); await rig.Actor.StepAsync(Pixels, default);
        var presses = rig.Events.OfType<NativeMenuPress>().Select(e => e.Intent).ToArray();
        presses.Select(e => e.Action).Should().Equal(DesiredAction.Toggle);
        presses.Select(e => e.Sequence).Should().Equal(6);
        presses.Select(e => e.NeutralArmSequence).Should().Equal(5);
    }

    [Theory]
    [InlineData("old-build")] [InlineData("old-image")] [InlineData("old-contract")]
    public async Task Old_native5_installation_identity_is_ineligible_before_any_native_IO(string mutation)
    {
        var policy = Policy();
        policy = mutation switch
        {
            "old-build" => policy with { NativeBuild = "GYM1:25314D47" },
            "old-image" => policy with { CandidateSha256 = "bfa1af30deecb6d64394e73963d7231c5c86811bad476cd7027c87c2d695292c" },
            _ => policy with { ContractSha256 = "c631094eae90fa265a978167017b50af55c29f7a8e2323e89ecbaf51ebe7b52c" },
        };
        var time = new ManualTimeProvider(); var input = new Input(time, 1); using var io = new Connection(time, 1);
        var source = new RogNativeMenuSource(policy, input, input, time); source.RequestEnabled(true);
        source.IsPresent.Should().BeFalse(); await using var actor = source.Attach(io);
        (await actor.StepAsync(Pixels, default)).Should().BeFalse();
        io.Writes.Should().BeEmpty(); input.Opens.Should().Be(0); input.Enumerations.Should().Be(0);
    }
}
