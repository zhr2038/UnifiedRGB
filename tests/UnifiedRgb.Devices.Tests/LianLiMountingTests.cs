using System.Buffers.Binary;
using Microsoft.Extensions.Logging.Abstractions;
using UnifiedRgb.Devices.LianLi;
using UnifiedRgb.Devices.LianLi.Codec;
using UnifiedRgb.Devices.LianLi.Protocol;
using UnifiedRgb.Devices.LianLi.Protocol.Families;
using UnifiedRgb.Transports.Safety;

namespace UnifiedRgb.Devices.Tests;

/// <summary>WP-11 package B (DEBUG-SESSION-1 §3.2/§4): exact mounting maps and their application on upload.</summary>
public class LianLiMountingTests
{
    private static readonly PacketAssembler Tl = FamilyDetector.ByFamily("TL")!;
    private static readonly PacketAssembler H2 = FamilyDetector.ByFamily("H2")!;

    private static LianLiMounting M(int rotation = 0, bool reverse = false, params LianLiFanMounting[] fans)
    {
        var m = new LianLiMounting { RotationDegrees = rotation, Reverse = reverse };
        foreach (var f in fans)
        {
            m.Fans.Add(f);
        }

        return m;
    }

    private static int[] Map(PacketAssembler family, int leds, LianLiMounting m)
    {
        MountingMap.TryBuild(family, leds, m, out var map, out var error).Should().BeTrue(error);
        return map!;
    }

    [Fact]
    public void Identity_builds_no_map_and_apply_returns_the_same_bytes()
    {
        MountingMap.TryBuild(Tl, 78, new LianLiMounting(), out var map, out _).Should().BeTrue();
        map.Should().BeNull("identity keeps the unmapped path byte-equal");
        var data = Enumerable.Range(0, 2 * 78 * 3).Select(i => (byte)i).ToArray();
        MountingMap.Apply(data, 78, null).ToArray().Should().Equal(data);
        MountingMap.Describe(new LianLiMounting()).Should().Be("identity");
    }

    [Theory]
    [InlineData("TL", 78, 0, true)]
    [InlineData("TL", 104, 180, false)]
    [InlineData("TL", 52, 180, true)]
    [InlineData("H2", 24, 90, false)]
    [InlineData("H2", 24, 270, true)]
    [InlineData("CL", 96, 180, true)]
    public void Maps_are_bijections_that_never_move_an_led_to_another_fan(string family, int leds, int rotation, bool reverse)
    {
        var f = FamilyDetector.ByFamily(family)!;
        var map = Map(f, leds, M(rotation, reverse));
        map.Should().HaveCount(leds);
        map.Should().OnlyHaveUniqueItems().And.OnlyContain(x => x >= 0 && x < leds);
        var per = f.Spec.LedsPerUnit;
        Enumerable.Range(0, leds).Should().OnlyContain(j => map[j] / per == j / per, "the transform is fan-local");
    }

    [Fact]
    public void Tl_mirror_reverses_each_edge_and_180_swaps_the_edges()
    {
        var mirror = Map(Tl, 26, M(reverse: true));
        mirror[0].Should().Be(12);
        mirror[12].Should().Be(0);
        mirror[13].Should().Be(25);
        var half = Map(Tl, 26, M(180));
        half[0].Should().Be(25, "top-left becomes bottom-right");
        half[13].Should().Be(12);
        var both = Map(Tl, 26, M(180, true));
        both[0].Should().Be(13, "mirror then 180° = edges swapped, k kept");
    }

    [Theory]
    [InlineData(90)]
    [InlineData(270)]
    public void Tl_quarter_turns_are_refused_not_faked(int rotation)
    {
        MountingMap.TryBuild(Tl, 78, M(rotation), out var map, out var error).Should().BeFalse();
        map.Should().BeNull();
        error.Should().Contain("90°/270°");
        MountingMap.TryBuild(Tl, 78, M(0, false, new LianLiFanMounting { Fan = 1, RotationDegrees = 90 }), out _, out _).Should().BeFalse();
    }

    [Fact]
    public void Ring_quarter_turn_moves_six_leds_and_mirror_keeps_led_0()
    {
        var turn = Map(H2, 24, M(90));
        turn[0].Should().Be(6);
        turn[23].Should().Be(5);
        var mirror = Map(H2, 24, M(reverse: true));
        (mirror[0], mirror[1], mirror[23]).Should().Be((0, 23, 1));
    }

    [Fact]
    public void Per_fan_overrides_apply_only_to_their_slot()
    {
        var map = Map(Tl, 78, M(0, false, new LianLiFanMounting { Fan = 1, Reverse = true }));
        Enumerable.Range(0, 26).Should().OnlyContain(j => map[j] == j);
        map[26].Should().Be(26 + 12);
        Enumerable.Range(52, 26).Should().OnlyContain(j => map[j] == j);
    }

    [Fact]
    public void Invalid_settings_are_rejected()
    {
        MountingMap.TryBuild(Tl, 78, M(45), out _, out var e1).Should().BeFalse();
        e1.Should().Contain("0, 90, 180 or 270");
        MountingMap.TryBuild(Tl, 78, M(0, false, new LianLiFanMounting { Fan = 3, Reverse = true }), out _, out var e2).Should().BeFalse();
        e2.Should().Contain("outside");
        MountingMap.TryBuild(Tl, 78, M(0, false, new LianLiFanMounting { Fan = 0, Reverse = true }, new LianLiFanMounting { Fan = 0 }), out _, out var e3)
            .Should().BeFalse();
        e3.Should().Contain("twice");
        var slv3 = FamilyDetector.ByFamily("SLv3")!;
        MountingMap.TryBuild(slv3, 160, M(180), out _, out var e4).Should().BeFalse();
        e4.Should().Contain("only identity");
        MountingMap.TryBuild(slv3, 160, new LianLiMounting(), out _, out _).Should().BeTrue();
    }

    [Fact]
    public void Apply_moves_whole_triplets_in_every_frame()
    {
        var map = Map(H2, 24, M(90));
        var src = new byte[2 * 24 * 3];
        for (var f = 0; f < 2; f++)
        {
            for (var i = 0; i < 24; i++)
            {
                var o = ((f * 24) + i) * 3;
                (src[o], src[o + 1], src[o + 2]) = ((byte)i, (byte)(100 + i), (byte)(200 + f));
            }
        }

        var dst = MountingMap.Apply(src, 24, map).ToArray();
        for (var f = 0; f < 2; f++)
        {
            for (var j = 0; j < 24; j++)
            {
                var o = ((f * 24) + map[j]) * 3;
                (dst[o], dst[o + 1], dst[o + 2]).Should().Be(((byte)j, (byte)(100 + j), (byte)(200 + f)));
            }
        }

        var wrong = () => MountingMap.Apply(src, 24, new int[12]);
        wrong.Should().Throw<ArgumentException>();
    }

    private static LianLiOptions Options(string family, int leds, LianLiMounting? mounting = null)
    {
        var o = new LianLiOptions { WireFormatVerified = true, ReceiverInterfaceGuid = FakeUsb.Guid };
        var d = new LianLiDeviceConfig { Mac = "AA-BB-CC-DD-EE-01", Family = family, Leds = leds, Channel = 8, Rx = 1 };
        if (mounting is not null)
        {
            d.Mounting = mounting;
        }

        o.Devices.Add(d);
        return o;
    }

    private static async Task<byte[]> UploadedData(LianLiAdapter a, RecordingTransport wire, ControllerId id, LoopFrames loop)
    {
        await a.UploadLoopAsync(id, LianLiAdapter.ChainZone, loop, CancellationToken.None);
        var up = Rf.Group(wire.SentFrames).Where(g => g.Packet?[1] == (byte)RfCmd.RgbSync).Select(g => g.Packet!)
            .Where(p => BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(14)) == loop.EffectIndex).ToList();
        return up.Where(p => p[18] != 0).SelectMany(p => p[20..]).Take(loop.Rgb.Length).ToArray();
    }

    private static LoopFrames Ramp(int leds, uint index) =>
        new(1, leds, 20, index, Enumerable.Range(0, leds * 3).Select(i => (byte)i).ToArray());

    [Fact]
    public async Task H2_mounting_is_applied_although_the_family_does_not_reorder_the_wire()
    {
        H2.ReordersWire.Should().BeFalse();
        var usb = new FakeUsb();
        var wire = new RecordingTransport();
        wire.EnqueueRead(Rf.MasterReply());
        await using var a = new LianLiAdapter(Options("H2", 24, M(90)), usb, _ => wire, usb, NullLogger.Instance, new IdentityEncoder(), _ => { }, () => Rf.Now);
        var d = (await a.DiscoverAsync(CancellationToken.None)).Single();
        await a.OpenAsync(d.Id, CancellationToken.None);

        var loop = Ramp(24, 1);
        var data = await UploadedData(a, wire, d.Id, loop);
        data.Should().Equal(MountingMap.Apply(loop.Rgb, 24, Map(H2, 24, M(90))).ToArray());
        data.Should().NotEqual(loop.Rgb.ToArray());
        a.LastUpload(d.Id)!.Mapping.Should().Contain("90°");

        a.SetMounting(d.Id, null).Map.Should().NotBeNull("reset returns to the configured default (90°)");
        a.MountingOf(d.Id).Settings.RotationDegrees.Should().Be(90);
        var cleared = a.SetMounting(d.Id, new LianLiMounting());
        cleared.Map.Should().BeNull();
        (await UploadedData(a, wire, d.Id, Ramp(24, 2))).Should().Equal(Ramp(24, 2).Rgb.ToArray(), "identity uploads stay byte-equal");
    }

    [Fact]
    public async Task Tl_mounting_composes_with_the_wire_order_and_bad_settings_change_nothing()
    {
        var usb = new FakeUsb();
        var wire = new RecordingTransport();
        wire.EnqueueRead(Rf.MasterReply());
        await using var a = new LianLiAdapter(Options("TL", 52), usb, _ => wire, usb, NullLogger.Instance, new IdentityEncoder(), _ => { }, () => Rf.Now);
        var d = (await a.DiscoverAsync(CancellationToken.None)).Single();
        await a.OpenAsync(d.Id, CancellationToken.None);
        var before = a.MountingOf(d.Id);
        before.Map.Should().BeNull();

        var quarter = () => a.SetMounting(d.Id, M(90));
        quarter.Should().Throw<ArgumentException>();
        a.MountingOf(d.Id).Should().BeSameAs(before, "a rejected setting leaves the revision untouched");

        var rev = a.SetMounting(d.Id, M(180));
        rev.Revision.Should().BeGreaterThan(before.Revision);
        var loop = Ramp(52, 3);
        var expected = MountingMap.Apply(Tl.ToWireOrder(loop.Rgb, 52), 52, rev.Map).ToArray();
        (await UploadedData(a, wire, d.Id, loop)).Should().Equal(expected, "W then O, each exactly once");

        (await a.DiscoverAsync(CancellationToken.None)).Should().ContainSingle();
        a.MountingOf(d.Id).Should().BeSameAs(rev, "rediscovery keeps the GUI override's revision");
    }

    [Fact]
    public async Task Chain_reversal_is_a_new_revision_but_leaves_the_adapters_wire_path_unmapped()
    {
        // WP-13: whole-chain fan order is applied upstream when a group body is sliced, never by the adapter's wire map
        var usb = new FakeUsb();
        var wire = new RecordingTransport();
        wire.EnqueueRead(Rf.MasterReply());
        await using var a = new LianLiAdapter(Options("TL", 52), usb, _ => wire, usb, NullLogger.Instance, new IdentityEncoder(), _ => { }, () => Rf.Now);
        var d = (await a.DiscoverAsync(CancellationToken.None)).Single();
        await a.OpenAsync(d.Id, CancellationToken.None);
        var before = a.MountingOf(d.Id);

        var rev = a.SetMounting(d.Id, new LianLiMounting { ReverseChain = true });
        rev.Revision.Should().BeGreaterThan(before.Revision);
        (rev.Map, rev.Description, a.MountingOf(d.Id).Settings.ReverseChain).Should().Be(((IReadOnlyList<int>?)null, "fan order reversed", true));
        var loop = Ramp(52, 3);
        (await UploadedData(a, wire, d.Id, loop)).Should().Equal(Tl.ToWireOrder(loop.Rgb, 52).ToArray(), "no fan-local permutation");

        a.SetMounting(d.Id, null).Settings.ReverseChain.Should().BeFalse("reset returns to the configured default");
    }

    [Fact]
    public async Task Unknown_devices_cannot_be_mounted_and_invalid_config_falls_back_to_identity()
    {
        var usb = new FakeUsb();
        var wire = new RecordingTransport();
        await using var a = new LianLiAdapter(Options("TL", 52, M(270)), usb, _ => wire, usb, NullLogger.Instance, new IdentityEncoder(), _ => { }, () => Rf.Now);
        var d = (await a.DiscoverAsync(CancellationToken.None)).Single();
        a.MountingOf(d.Id).Map.Should().BeNull("a TL quarter turn in appsettings is logged and treated as identity");
        var unknown = () => a.SetMounting(new ControllerId("lianli:rf:00-00-00-00-00-01"), M(180));
        unknown.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(39, 55.0)]
    [InlineData(25, 20.0)]
    [InlineData(7, 33.0)]
    public void Compress_resamples_odd_loops_over_the_original_period(int frames, double interval)
    {
        var enc = new SizedEncoder(n => n > 480 * ((frames + 1) / 2) ? 20_000 : 100);
        var loop = new LoopFrames(frames, 160, interval, 1, TinyuzEncoder.SampleAnimation(frames * 160 * 3));
        var p = LianLiAdapter.Compress(enc, loop, 12288);
        var kept = (frames + 1) / 2;
        (p.Decimations, p.Animation.TotalFrames).Should().Be((1, kept));
        var period = frames * interval;
        p.Animation.IntervalMs.Should().Be(Math.Round(period / kept * 100) / 100);
        p.PeriodErrorMs.Should().BeApproximately((kept * p.Animation.IntervalMs) - period, 1e-9);
        Math.Abs(p.PeriodErrorMs).Should().BeLessThanOrEqualTo(kept * 0.005 + 1e-9, "only the 0.01 ms header rounding remains");
    }
}
