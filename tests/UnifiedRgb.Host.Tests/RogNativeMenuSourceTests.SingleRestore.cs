using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Devices.AsusRog;
using UnifiedRgb.Host.Services;

namespace UnifiedRgb.Host.Tests;

public sealed partial class RogNativeMenuSourceTests
{
    [Fact]
    public async Task NativeIdle_stopping_custom_artwork_performs_one_fresh_checked_brightness_restore()
    {
        await using var rig = new PresentationRig(new DeclaredPresentation(KeyboardNativePresentationSupport.Available),
            mode: RogNativeMenuCodec.NativeMode, animation: true, deviceShowsLogo: true);
        rig.Timeline.At(1500);
        await rig.Owner.StartAsync(default);
        await rig.Prepared();
        await Until(() => rig.Control.ImageCommits >= 1 && rig.Shows(KeyboardDisplayServiceTests.Fill(0x22)));
        await rig.NextTick();
        var restores = rig.Control.NativeRestores;
        var before = rig.Control.Packets.Count;
        await rig.Owner.SetKeyboardDisplayAnimationAsync(new(false, null, null, "contain", KeyboardDisplayAnimationEncodings.LumaRound, 2000));
        await Until(() => rig.Control.Disarms == 1 && rig.Control.Arms == 2 && rig.Control.NativeRestores > restores);
        await Task.Delay(220); // also includes ordinary idle observations, which must not count as restore readbacks
        var packets = rig.Control.Packets.Skip(before).ToArray();
        var setters = Enumerable.Range(0, packets.Length).Where(i => packets[i][1] == 0x68).ToArray();
        setters.Should().ContainSingle("the callback resolves the goal and the existing actor restores exactly once");
        var at = setters[0];
        packets[at - 2][1].Should().Be(0x12, "fresh scope guard precedes the restoration");
        packets[at - 1][1].Should().Be(0x61, "the existing stored selector restore stays intact");
        packets[at + 1][1..3].Should().Equal(new byte[] { 0x23, 3 }, "SET68 still has its mandatory brightness readback");
        packets[at + 2][1..3].Should().Equal(new byte[] { 0x21, 0 }, "the stored selector is still read back");
        Enumerable.Range(0, packets.Length - 1).Count(i => packets[i][1] == 0x68
            && packets[i + 1][1] == 0x23 && packets[i + 1][2] == 3).Should().Be(1);
        rig.Control.NativeRestores.Should().Be(restores + 1);
        rig.Source!.IsPresent.Should().BeTrue();
    }
}
