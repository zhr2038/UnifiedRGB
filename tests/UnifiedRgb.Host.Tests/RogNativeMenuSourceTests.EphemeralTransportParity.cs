using UnifiedRgb.Devices.AsusRog;

namespace UnifiedRgb.Host.Tests;

public sealed partial class RogNativeMenuSourceTests
{
    [Theory]
    [InlineData(1, "begin")] [InlineData(2, "begin")]
    [InlineData(1, "lease")] [InlineData(2, "lease")]
    [InlineData(1, "commit")] [InlineData(2, "commit")]
    public async Task Native_ephemeral_transport_failure_rebuilds_a_complete_current_frame_on_a_fresh_owner(byte transport, string phase)
    {
        await using var rig = new NativeReconciliationRig();
        await rig.StartAsync(transport);
        var old = rig.Routes.Ports[^1];
        var images = rig.Firmware(transport).Images.Count;
        var record = File.ReadAllBytes(rig.Journal);
        if (phase == "commit")
        {
            rig.Routes.RefuseCapabilityAfterImages = images + 1;
            rig.ChangePixels();
        }
        else
        {
            rig.Routes.RefuseReadCommand = 0x67;
            if (phase == "begin") rig.ChangePixels();
        }
        await SwitchUntil(() => old.Disposed && rig.Source.DedicatedPageActive
            && rig.Firmware(transport).Images.Count > images && rig.Routes.Ports[^1] != old, rig.Time);
        old.Requests.Should().HaveCount(old.TransientFailureAtWriteCount!.Value);
        var fresh = rig.Routes.Ports[^1];
        fresh.Requests.Should().NotContain(p => IsPersistentSetting(p));
        fresh.Requests.Count(p => p[1] == 0x67 && p[2] == 1).Should().Be(transport == 1 ? 111 : 113);
        rig.Firmware(transport).Images[^1].Should().Equal(rig.CurrentPixels);
        File.ReadAllBytes(rig.Journal).Should().Equal(record);
        rig.Routes.MaximumLivePorts.Should().Be(1);
        rig.Routes.OpenWhilePriorInputLive.Should().BeFalse();
        (await rig.Owner.GetKeyboardDisplayStatusAsync()).LastError.Should().BeNull();
    }

    [Theory]
    [InlineData(1)] [InlineData(2)]
    public async Task Native_ephemeral_colour_failure_fails_the_old_request_and_accepts_only_a_new_current_frame(byte transport)
    {
        await using var rig = new NativeReconciliationRig();
        await rig.StartAsync(transport);
        var old = rig.Routes.Ports[^1];
        var record = File.ReadAllBytes(rig.Journal);
        if (transport == 1) rig.Routes.RefuseReadCommand = 0xC0;
        else rig.Routes.TimeoutColourPacket = 3;
        var failed = await ((Func<Task>)(() => WriteRouteKeys(rig.Bridge, transport)))
            .Should().ThrowAsync<RogNativePresentationUnavailableException>();
        failed.Which.Phase.Should().Be("per-key-frame");
        failed.Which.ConnectionKind.Should().Be(transport == 1 ? "wired" : "wireless");
        await SwitchUntil(() => old.Disposed && rig.Bridge.Available && rig.Source.DedicatedPageActive
            && rig.Routes.Ports[^1] != old, rig.Time);
        old.Requests.Should().HaveCount(old.TransientFailureAtWriteCount!.Value);
        var fresh = rig.Routes.Ports[^1];
        fresh.Requests.Should().NotContain(p => IsPersistentSetting(p) || p[1] == 0xC0);
        var latest = Enumerable.Range(0, 85).Select(i => new RogKeyColor((byte)i, new(17, (byte)i, 29))).ToArray();
        await rig.Bridge.WriteKeysAsync(latest, transport == 1 ? RogPerKeyLighting.WiredTransport : RogPerKeyLighting.WirelessTransport, default);
        var expected = RogPerKeyLighting.BoundaryFrame(latest).Select(p =>
        {
            if (transport == 1) return p;
            var radio = new byte[64]; radio[0] = 2; p.AsSpan(1, 63).CopyTo(radio.AsSpan(1)); return radio;
        });
        fresh.Requests.Where(p => p[1] == 0xC0).Select(Convert.ToHexString).Should().Equal(expected.Select(Convert.ToHexString));
        File.ReadAllBytes(rig.Journal).Should().Equal(record);
        rig.Routes.MaximumLivePorts.Should().Be(1);
    }

    [Theory]
    [InlineData(1)] [InlineData(2)]
    public async Task Native_ephemeral_policy_keeps_ARM_mutation_refusal_terminal(byte transport)
    {
        await using var rig = new NativeReconciliationRig();
        rig.Routes.WiredAvailable = transport == 1;
        rig.Routes.RefuseReadCommand = 0x71;
        await rig.Owner.StartAsync(default);
        await SwitchUntil(() => rig.Owner.GetKeyboardDisplayStatusAsync().GetAwaiter().GetResult().State == "Error", rig.Time);
        rig.Routes.Ports.Should().ContainSingle();
        var failed = rig.Routes.Ports[0];
        failed.Requests.Should().HaveCount(failed.TransientFailureAtWriteCount!.Value);
        File.Exists(rig.Journal).Should().BeTrue();
        rig.Source.DedicatedPageActive.Should().BeFalse();
    }
}
