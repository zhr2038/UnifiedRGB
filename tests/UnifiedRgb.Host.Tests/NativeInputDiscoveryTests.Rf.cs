using UnifiedRgb.Devices.AsusRog;
using UnifiedRgb.Host.Services;

namespace UnifiedRgb.Host.Tests;

/// <summary>
/// RF route fixtures on Root's actual receiver metadata that do not depend on the pending evidence seam: an owner without
/// current evidence keeps today's rule (initial proof valid 3 minutes, then no ARM and no action), the route identity comes
/// from the captured binding, and the full image framing is preserved.
/// </summary>
public sealed partial class NativeInputDiscoveryTests
{
    [Theory]
    [InlineData(1, 111, 60, 56)] [InlineData(2, 113, 59, 48)]
    public async Task Actual_route_upload_keeps_the_full_frame_chunking(byte transport, int chunks, int payload, int tail)
    {
        await using var rig = new Rig(transport); rig.Source.RequestEnabled(true);
        var pixels = Pixels();
        (await rig.Actor.StepAsync(() => pixels, default)).Should().BeTrue();
        rig.Io.ImageChunks.Should().HaveCount(chunks);
        var rebuilt = rig.Io.ImageChunks.SelectMany((p, i) => p.AsSpan(5, i == chunks - 1 ? tail : payload).ToArray()).ToArray();
        rebuilt.Should().Equal(pixels, "no crop, re-quantization or short frame");
        rig.Io.ImageChunks[^1].AsSpan(5 + tail).ToArray().Should().OnlyContain(b => b == 0);
    }

    [Fact]
    public async Task RF_without_current_evidence_expires_after_180_s_with_one_DISARM_and_no_later_IO()
    {
        await using var rig = new Rig(2); rig.Source.RequestEnabled(true);
        (await rig.Actor.StepAsync(Pixels, default)).Should().BeTrue();
        rig.Time.Advance(TimeSpan.FromSeconds(170));
        (await rig.Actor.StepAsync(Pixels, default)).Should().BeTrue("the initial pairing proof is still inside its 3-minute window");
        rig.Time.Advance(TimeSpan.FromSeconds(11));
        (await rig.Actor.StepAsync(Pixels, default)).Should().BeFalse();
        rig.Events.OfType<NativeMenuContextEnded>().Should().ContainSingle(e => e.Reason.Contains("pairing proof expired", StringComparison.Ordinal));
        rig.Events.OfType<NativeMenuPress>().Should().BeEmpty();
        rig.Io.Arms.Should().Be(1); rig.Io.Disarms.Should().Be(1); rig.Source.IsPresent.Should().BeFalse();
        var exchanges = rig.Io.Exchanges;
        (await rig.Actor.StepAsync(Pixels, default)).Should().BeFalse();
        rig.Io.Exchanges.Should().Be(exchanges, "an expired generation performs no further native I/O");
        rig.World.Opens.Should().Be(1); rig.World.LiveHandles.Should().Be(0);
    }

    [Fact]
    public async Task RF_without_current_evidence_cannot_ARM_after_180_s_and_never_enumerates_input()
    {
        await using var rig = new Rig(2); rig.Source.RequestEnabled(true); rig.Io.Mode = 7;
        (await rig.Actor.StepAsync(Pixels, default)).Should().BeFalse();
        rig.Time.Advance(TimeSpan.FromSeconds(181)); rig.Io.Mode = RogNativeMenuCodec.NativeMode;
        var thrown = await ((Func<Task>)(() => rig.Actor.StepAsync(Pixels, default))).Should().ThrowAsync<InvalidDataException>();
        thrown.Which.Message.Should().Contain("RF pairing proof unavailable");
        rig.World.Enumerations.Should().Be(0); rig.World.Opens.Should().Be(0); rig.Io.Arms.Should().Be(0); rig.Events.Should().BeEmpty();
    }

    [Theory]
    [InlineData("route-report-id")] [InlineData("route-product")] [InlineData("route-notify")] [InlineData("no-keyboard")]
    [InlineData("future-pairing")]
    public async Task RF_unverified_or_future_dated_captured_route_never_ARMs_or_touches_input(string mutation)
    {
        await using var rig = new Rig(2, owner: io =>
        {
            switch (mutation)
            {
                case "route-report-id": io.KeyboardOverride = new(0x1C25, 3, 4); break;
                case "route-product": io.KeyboardOverride = new(0x1C26, 2, 4); break;
                case "route-notify": io.KeyboardOverride = new(0x1C25, 2, 5); break;
                case "no-keyboard": io.NoKeyboard = true; break;
                case "future-pairing": io.PairedAt += (long)(10 * TimeProvider.System.TimestampFrequency); break;
                default: throw new ArgumentOutOfRangeException(nameof(mutation));
            }
        });
        rig.Source.RequestEnabled(true);
        await ((Func<Task>)(() => rig.Actor.StepAsync(Pixels, default))).Should().ThrowAsync<InvalidDataException>();
        rig.Io.Arms.Should().Be(0); rig.Io.Exchanges.Should().Be(1, "only the capability read preceded the refused route");
        rig.World.Enumerations.Should().Be(0); rig.World.Opens.Should().Be(0); rig.Events.Should().BeEmpty();
        rig.Source.IsPresent.Should().BeFalse();
    }
}
