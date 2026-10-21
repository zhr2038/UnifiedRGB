using UnifiedRgb.Core.Effects;
using UnifiedRgb.Core.Effects.LianLi;
using UnifiedRgb.Devices.LianLi;
using UnifiedRgb.Devices.LianLi.Codec;
using UnifiedRgb.Devices.LianLi.Protocol;
using UnifiedRgb.Devices.LianLi.Protocol.Families;

namespace UnifiedRgb.Devices.Tests;

/// <summary>
/// WP-15 (WORKORDER-STACKING-FIX, test 7): the fan-aligned Stacking plan for the reported group order and saved mounting
/// through the real codec, both directions. An absent codec fails loudly; set URGB_ALLOW_MISSING_CODEC=1 to accept the
/// absence explicitly (the run then reports UNAVAILABLE, it does not pass silently as a measurement).
/// </summary>
public class LianLiStackingAlignmentCodecTests
{
    private static readonly PacketAssembler Tl = FamilyDetector.ByFamily("TL")!;
    private static readonly PacketAssembler H2 = FamilyDetector.ByFamily("H2")!;

    [LocalCodecFact]
    public void Fan_aligned_stacking_round_trips_the_real_codec_for_the_reported_group_and_mounting()
    {
        if (!File.Exists(YuzCodecTests.DllPath))
        {
            var message = $"UNAVAILABLE (prerequisite): real codec {YuzCodecTests.DllPath} not present; WP-15 codec check not measured";
            Console.WriteLine(message);
            Environment.GetEnvironmentVariable("URGB_ALLOW_MISSING_CODEC").Should().Be("1", message);
            return;
        }

        using var enc = TinyuzEncoder.Open(YuzCodecTests.DllPath);

        // bottom LCD, vertical TL140 (identity), top TL120 (ReverseChain only: slicing, no wire map), H2 (rotation 180,
        // mirror, offset −2, FaceOnFill, whole-step)
        LConnectZone[] zones = [new("TL", 3, 78), new("TL", 3, 78), new("TL", 3, 78, ReverseChain: true), new("H2", 1, 24, FaceOnFill: true)];
        var mountings = new[]
        {
            new LianLiMounting(),
            new LianLiMounting(),
            new LianLiMounting { ReverseChain = true },
            new LianLiMounting { RotationDegrees = 180, Reverse = true, RingOffsetLeds = -2, FaceOnFill = true },
        };
        PacketAssembler[] families = [Tl, Tl, Tl, H2];
        var maps = new int[]?[4];
        for (var i = 0; i < 4; i++)
        {
            MountingMap.TryBuild(families[i], zones[i].LedCount, mountings[i], out maps[i], out var error).Should().BeTrue(error);
        }

        (maps[0], maps[1], maps[2]).Should().Be((null, null, null), "TL chains have identity wire maps");
        maps[3].Should().NotBeNull("the H2's saved rotation/mirror/offset is a wire map");

        Rgb[] red = [new(255, 0, 0)];
        var report = new List<string> { $"codec {enc.CodecHash} at {YuzCodecTests.DllPath}" };
        foreach (var dir in new[] { Direction.Reverse, Direction.Forward })
        {
            var plan = LConnectLoops.TryPlan("Stacking", new EffectParams(red, 0.25f, 1f, dir), red, zones)!;
            plan.Variant.Should().StartWith("UnifiedRgb uploaded UiMode7, fan-aligned Stacking over 132 source dots");
            for (var i = 0; i < 4; i++)
            {
                var z = plan.Zones[i];
                var leds = zones[i].LedCount;
                (z.Frames, z.IntervalMs).Should().Be((792, 72.0), $"{dir} zone {i}");
                var wire = MountingMap.Apply(families[i].ToWireOrder(z.Rgb, leds), leds, maps[i]).ToArray();
                wire.Length.Should().Be(792 * leds * 3);
                var p = LianLiAdapter.Compress(enc, new LoopFrames(792, leds, z.IntervalMs, 1, wire), LianLiWire.MaxCompressedAnimation);
                var fallback = p.Decimations > LianLiAdapter.MaxDecimations;
                (p.Decimations, fallback, p.Animation.TotalFrames, p.RawBytes).Should().Be((0, false, 792, wire.Length), $"{dir} zone {i}: every frame kept");
                p.Animation.IntervalMs.Should().BeApproximately(72.0, 0.005, $"{dir} zone {i}");
                p.Compressed.Length.Should().BeLessThanOrEqualTo(LianLiWire.MaxCompressedAnimation);
                var roundTrip = enc.Decode(p.Compressed, wire.Length).AsSpan().SequenceEqual(wire);
                roundTrip.Should().BeTrue($"{dir} zone {i}: byte-identical decode of the mounted wire input");
                report.Add($"Stacking {dir} z{i} 792x{leds} @ {z.IntervalMs} ms: raw {wire.Length} -> {p.Compressed.Length} B (limit {LianLiWire.MaxCompressedAnimation}), frames {p.Animation.TotalFrames}, decimations {p.Decimations}, static fallback {fallback}, round-trip {roundTrip}");
            }
        }

        File.WriteAllLines(Path.Combine(Path.GetTempPath(), "urgb-wp15-stacking.txt"), report);
        Console.WriteLine(string.Join(Environment.NewLine, report));
    }
}
