using UnifiedRgb.Devices.AsusRog;

namespace UnifiedRgb.Host.Tests;

public sealed partial class RogNativeMenuSourceTests
{
    [Theory]
    [InlineData((byte)1)] [InlineData((byte)2)]
    public async Task Fake_frame_READY_requires_all_ordered_bytes_and_is_reset_by_BEGIN_and_scope_changes(byte transport)
    {
        await using var rig = new Rig(transport);
        rig.Io.DedicatedPage = true; rig.Io.Mode = 10; rig.Source.RequestCodexEnabled(true);
        (await rig.Actor.StepAsync(() => null, default)).Should().BeFalse();
        async Task<RogNativeCapability> Cap() => RogNativeMenuCodec.ParseCapability(
            await rig.Io.ExchangeAsync(RogNativeMenuCodec.CapabilityRequest(), false, default), 0x12, transport);
        ((await Cap()).Flags & 8).Should().Be(0, "ARM alone cannot make an image ready");
        var packets = RogWiredOledProtocol.Pack(Pixels(), rig.Io.PixelPayloadCapacity);
        await rig.Io.ExchangeAsync(packets[0], true, default);
        var begun = await Cap();
        (begun.Flags & 8).Should().Be(0); (begun.Flags & 4).Should().Be(4); rig.Io.Rebuilt.Should().BeEmpty();
        foreach (var packet in packets.Skip(1).SkipLast(1)) await rig.Io.ExchangeAsync(packet, true, default);
        var incomplete = await Cap();
        (incomplete.Flags & 8).Should().Be(0); (incomplete.Flags & 4).Should().Be(4);
        await ((Func<Task>)(() => rig.Io.ExchangeAsync(RogWiredOledProtocol.Renew(), true, default)))
            .Should().ThrowAsync<InvalidDataException>().WithMessage("*before same-scope full6656B READY*");
        await rig.Io.ExchangeAsync(packets[^1], true, default);
        var complete = await Cap();
        (complete.Flags & 8).Should().Be(8); (complete.Flags & 4).Should().Be(0); rig.Io.Rebuilt.Should().Equal(Pixels());
        await rig.Io.ExchangeAsync(packets[0], true, default);
        ((await Cap()).Flags & 8).Should().Be(0, "new BEGIN invalidates previous complete bytes");
        rig.Io.Epoch++;
        var changedEpoch = await Cap();
        (changedEpoch.Flags & 8).Should().Be(0); (changedEpoch.Flags & 4).Should().Be(0);
        await ((Func<Task>)(() => rig.Io.ExchangeAsync(packets[1], true, default))).Should().ThrowAsync<InvalidDataException>();
        rig.Io.Profile = 5;
        ((await Cap()).Flags & 8).Should().Be(0); rig.Io.Rebuilt.Should().BeEmpty();
        rig.Io.Mode = 7;
        ((await Cap()).Flags & 8).Should().Be(0);
    }
}
