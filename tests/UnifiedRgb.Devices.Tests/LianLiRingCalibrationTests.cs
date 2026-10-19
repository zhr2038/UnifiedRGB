using System.Buffers.Binary;
using Microsoft.Extensions.Logging.Abstractions;
using UnifiedRgb.Devices.LianLi;
using UnifiedRgb.Devices.LianLi.Protocol;
using UnifiedRgb.Devices.LianLi.Protocol.Families;
using UnifiedRgb.Transports.Safety;

namespace UnifiedRgb.Devices.Tests;

/// <summary>
/// WP-14 (H2-VERTICAL-FILL §3/§6): the ring offset in the H2 wire map, its invariants (bijective, identity → null map,
/// ring-only fields), the rev-15 maps unchanged, and the adapter's calibration overlay (revision kept on Cancel).
/// </summary>
public class LianLiRingCalibrationTests
{
    private static readonly PacketAssembler Tl = FamilyDetector.ByFamily("TL")!;
    private static readonly PacketAssembler H2 = FamilyDetector.ByFamily("H2")!;

    private static int[]? Map(PacketAssembler family, int leds, LianLiMounting m)
    {
        MountingMap.TryBuild(family, leds, m, out var map, out var error).Should().BeTrue(error);
        return map;
    }

    [Fact]
    public void Ring_offset_shifts_clockwise_after_the_mirror_and_composes_with_quarter_turns()
    {
        var map = Map(H2, 24, new LianLiMounting { RingOffsetLeds = 2 })!;
        map.Should().Equal(Enumerable.Range(0, 24).Select(i => (i + 2) % 24));
        Map(H2, 24, new LianLiMounting { RingOffsetLeds = -1 })![0].Should().Be(23);
        Map(H2, 24, new LianLiMounting { RotationDegrees = 90, RingOffsetLeds = 2 })
            .Should().Equal(Map(H2, 24, new LianLiMounting { RingOffsetLeds = 8 }), "rot90 + off2 == off8");
        Map(H2, 24, new LianLiMounting { Reverse = true, RingOffsetLeds = 1 })
            .Should().Equal(Enumerable.Range(0, 24).Select(i => (((24 - i) % 24) + 1) % 24), "mirror first, then the shift");
        MountingMap.RingLocal(5, 270, true, -3).Should().Be((((24 - 5) % 24) + 18 - 3) % 24);
    }

    [Fact]
    public void Every_ring_setting_is_a_bijection_and_identity_settings_build_no_map()
    {
        foreach (var rot in MountingMap.Rotations)
        {
            for (var off = -3; off <= 3; off++)
            {
                foreach (var mirror in new[] { false, true })
                {
                    foreach (var half in new[] { false, true })
                    {
                        var m = new LianLiMounting { RotationDegrees = rot, RingOffsetLeds = off, Reverse = mirror, HalfStep = half, FaceOnFill = true };
                        var map = Map(H2, 24, m);
                        if (rot == 0 && off == 0 && !mirror)
                        {
                            map.Should().BeNull("half-step and vertical fill are not part of the wire map");
                            m.IsIdentity.Should().BeTrue();
                            continue;
                        }

                        map!.Order().Should().Equal(Enumerable.Range(0, 24), $"rot {rot} off {off} mirror {mirror}");
                        map.Should().Equal(Map(H2, 24, new LianLiMounting { RotationDegrees = rot, RingOffsetLeds = off, Reverse = mirror }),
                            "HalfStep/FaceOnFill never change the permutation");
                    }
                }
            }
        }

        new LianLiMounting { RingOffsetLeds = 1 }.IsIdentity.Should().BeFalse();
    }

    [Fact]
    public void Ring_fields_are_validated_and_refused_on_tl_chains()
    {
        foreach (var bad in new[] { new LianLiMounting { RingOffsetLeds = 1 }, new LianLiMounting { HalfStep = true }, new LianLiMounting { FaceOnFill = true } })
        {
            MountingMap.TryBuild(Tl, 78, bad, out _, out var error).Should().BeFalse();
            error.Should().Contain("ring (H2)");
        }

        MountingMap.TryBuild(H2, 24, new LianLiMounting { RingOffsetLeds = 24 }, out _, out var range).Should().BeFalse();
        range.Should().Contain("-23..+23");
        MountingMap.TryBuild(H2, 24, new LianLiMounting { RingOffsetLeds = -23 }, out var edge, out _).Should().BeTrue();
        edge.Should().Equal(Enumerable.Range(0, 24).Select(i => (i + 1) % 24));
    }

    [Fact]
    public void Description_names_the_ring_fields_and_clone_keeps_them()
    {
        var m = new LianLiMounting { Reverse = true, RingOffsetLeds = 2, HalfStep = true, FaceOnFill = true };
        MountingMap.Describe(m).Should().Be("0° mirrored offset +2; vertical fill, half-step");
        MountingMap.Describe(new LianLiMounting { FaceOnFill = true }).Should().Be("vertical fill");
        MountingMap.Describe(new LianLiMounting { RingOffsetLeds = -1 }).Should().Be("0° offset -1");
        var c = m.Clone();
        (c.RingOffsetLeds, c.HalfStep, c.FaceOnFill).Should().Be((2, true, true));
    }

    [Fact]
    public void Rev_15_settings_keep_their_maps_and_descriptions()
    {
        // the owner's validated mounting (AGENTS.md §2): new fields at their defaults change nothing
        MountingMap.TryBuild(Tl, 78, new LianLiMounting { ReverseChain = true }, out var tl120, out _).Should().BeTrue();
        tl120.Should().BeNull();
        MountingMap.Describe(new LianLiMounting { ReverseChain = true }).Should().Be("fan order reversed");
        var h2 = Map(H2, 24, new LianLiMounting { Reverse = true })!;
        h2.Should().Equal(Enumerable.Range(0, 24).Select(i => (24 - i) % 24));
        MountingMap.Describe(new LianLiMounting { Reverse = true }).Should().Be("0° mirrored");
        MountingMap.Describe(new LianLiMounting()).Should().Be("identity");
    }

    private static LianLiOptions Options()
    {
        var o = new LianLiOptions { WireFormatVerified = true, ReceiverInterfaceGuid = FakeUsb.Guid };
        o.Devices.Add(new LianLiDeviceConfig { Mac = "AA-BB-CC-DD-EE-01", Family = "H2", Leds = 24, Channel = 8, Rx = 1 });
        return o;
    }

    private static async Task<byte[]> UploadedData(LianLiAdapter a, RecordingTransport wire, ControllerId id, LoopFrames loop)
    {
        await a.UploadLoopAsync(id, LianLiAdapter.ChainZone, loop, CancellationToken.None);
        var up = Rf.Group(wire.SentFrames).Where(g => g.Packet?[1] == (byte)RfCmd.RgbSync).Select(g => g.Packet!)
            .Where(p => BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(14)) == loop.EffectIndex).ToList();
        return up.Where(p => p[18] != 0).SelectMany(p => p[20..]).Take(loop.Rgb.Length).ToArray();
    }

    [Fact]
    public async Task Calibration_overlay_maps_uploads_without_a_revision_and_ends_cleanly()
    {
        var usb = new FakeUsb();
        var wire = new RecordingTransport();
        wire.EnqueueRead(Rf.MasterReply());
        await using var a = new LianLiAdapter(Options(), usb, _ => wire, usb, NullLogger.Instance, new IdentityEncoder(), _ => { }, () => Rf.Now);
        var d = (await a.DiscoverAsync(CancellationToken.None)).Single();
        await a.OpenAsync(d.Id, CancellationToken.None);
        var saved = a.SetMounting(d.Id, new LianLiMounting { Reverse = true });

        var overlay = a.BeginCalibration(d.Id, new LianLiMounting { Reverse = true, RingOffsetLeds = 2, FaceOnFill = true });
        overlay.Revision.Should().Be(saved.Revision, "calibration is not a new revision");
        overlay.Description.Should().EndWith("(calibrating)");
        a.IsCalibrating(d.Id).Should().BeTrue();
        a.MountingOf(d.Id).Should().BeSameAs(overlay);

        var marker = new LoopFrames(1, 24, 20, 7, Core.Effects.LianLi.RingFillMap.MarkerFrame(false)) { Geometry = "calibration" };
        var data = await UploadedData(a, wire, d.Id, marker);
        data.Should().Equal(MountingMap.Apply(marker.Rgb, 24, overlay.Map).ToArray(), "the marker goes through the candidate map");
        data.AsSpan(((24 - 0 + 2) % 24) * 3, 3).ToArray().Should().Equal(255, 0, 0);
        a.LastUpload(d.Id)!.Geometry.Should().Be("calibration");
        a.LastUpload(d.Id)!.Mapping.Should().Contain("(calibrating)");

        var bad = () => a.BeginCalibration(d.Id, new LianLiMounting { RotationDegrees = 45 });
        bad.Should().Throw<ArgumentException>();
        a.MountingOf(d.Id).Should().BeSameAs(overlay, "a refused candidate changes nothing");

        // Cancel: the saved revision comes back unchanged
        a.EndCalibration(d.Id).Should().BeSameAs(saved);
        a.IsCalibrating(d.Id).Should().BeFalse();
        a.MountingOf(d.Id).Revision.Should().Be(saved.Revision);

        // Apply = end the overlay, then one SetMounting: exactly one revision bump
        a.BeginCalibration(d.Id, new LianLiMounting { Reverse = true, RingOffsetLeds = -1, HalfStep = true, FaceOnFill = true });
        var candidate = a.MountingOf(d.Id).Settings.Clone();
        a.EndCalibration(d.Id);
        var applied = a.SetMounting(d.Id, candidate);
        applied.Revision.Should().Be(saved.Revision + 1);
        (applied.Settings.RingOffsetLeds, applied.Settings.HalfStep, applied.Settings.FaceOnFill).Should().Be((-1, true, true));

        var unknown = () => a.BeginCalibration(new ControllerId("lianli:rf:00-00-00-00-00-09"), new LianLiMounting());
        unknown.Should().Throw<ArgumentException>();
    }
}
