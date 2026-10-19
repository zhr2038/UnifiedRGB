using UnifiedRgb.Core.Effects;
using UnifiedRgb.Core.Effects.LianLi;
using UnifiedRgb.Devices.LianLi;
using UnifiedRgb.Devices.LianLi.Codec;
using UnifiedRgb.Devices.LianLi.Protocol;
using UnifiedRgb.Devices.LianLi.Protocol.Families;

namespace UnifiedRgb.Devices.Tests;

/// <summary>
/// WP-16 (wp16-smoothing-order, test 5): the interpolated Stacking plan for the reported group through the real codec with the
/// owner's saved mounting (H2 rotation 180 / mirror / offset 0 / whole-step / FaceOnFill), red and #FF3441 at .75 brightness,
/// both directions. An absent codec fails loudly; URGB_ALLOW_MISSING_CODEC=1 accepts the absence explicitly (reported as
/// UNAVAILABLE, never as a measurement).
/// </summary>
public class LianLiStackingInterpolationCodecTests
{
    private static readonly PacketAssembler Tl = FamilyDetector.ByFamily("TL")!;
    private static readonly PacketAssembler H2 = FamilyDetector.ByFamily("H2")!;

    [LocalCodecFact]
    public void Interpolated_stacking_round_trips_the_real_codec_with_the_saved_mounting()
    {
        if (!File.Exists(YuzCodecTests.DllPath))
        {
            var message = $"UNAVAILABLE (prerequisite): real codec {YuzCodecTests.DllPath} not present; WP-16 codec check not measured";
            Console.WriteLine(message);
            Environment.GetEnvironmentVariable("URGB_ALLOW_MISSING_CODEC").Should().Be("1", message);
            return;
        }

        using var enc = TinyuzEncoder.Open(YuzCodecTests.DllPath);
        LConnectZone[] zones = [new("TL", 3, 78), new("TL", 3, 78), new("TL", 3, 78, ReverseChain: true), new("H2", 1, 24, FaceOnFill: true)];
        var mountings = new[]
        {
            new LianLiMounting(),
            new LianLiMounting(),
            new LianLiMounting { ReverseChain = true },
            new LianLiMounting { RotationDegrees = 180, Reverse = true, RingOffsetLeds = 0, HalfStep = false, FaceOnFill = true },
        };
        PacketAssembler[] families = [Tl, Tl, Tl, H2];
        var maps = new int[]?[4];
        for (var i = 0; i < 4; i++)
        {
            MountingMap.TryBuild(families[i], zones[i].LedCount, mountings[i], out maps[i], out var error).Should().BeTrue(error);
        }

        maps[3].Should().NotBeNull("the H2's saved rotation/mirror is a wire map");

        var report = new List<string> { $"codec {enc.CodecHash} at {YuzCodecTests.DllPath}; H2 mounting 180 / mirror / offset 0 / whole-step" };
        var cases = new (string Name, Rgb Colour, float Brightness)[] { ("red", new Rgb(255, 0, 0), 1f), ("#FF3441@.75", new Rgb(0xFF, 0x34, 0x41), 0.75f) };
        foreach (var (name, colour, brightness) in cases)
        {
            foreach (var dir in new[] { Direction.Reverse, Direction.Forward })
            {
                var p = new EffectParams([colour], 0.25f, brightness, dir);
                var plan = LConnectLoops.TryPlan("Stacking", p, [colour], zones)!;
                plan.Variant.Should().EndWith("; H2 vertical-fill (bottom=High, interpolated)");

                // the WP-15 nearest H2 for the size comparison only
                var unfilled = LConnectLoops.TryPlan("Stacking", p, [colour], [.. zones[..3], zones[3] with { FaceOnFill = false }])!;
                var nearestH2 = RingFillMap.ApplyFrames(unfilled.Zones[3].Rgb, BottomEnd.High, false);
                for (var i = 0; i < 4; i++)
                {
                    var z = plan.Zones[i];
                    var leds = zones[i].LedCount;
                    (z.Frames, z.IntervalMs).Should().Be((792, 72.0), $"{name} {dir} zone {i}");
                    var wire = MountingMap.Apply(families[i].ToWireOrder(z.Rgb, leds), leds, maps[i]).ToArray();
                    wire.Length.Should().Be(792 * leds * 3);
                    var c = LianLiAdapter.Compress(enc, new LoopFrames(792, leds, z.IntervalMs, 1, wire), LianLiWire.MaxCompressedAnimation);
                    var fallback = c.Decimations > LianLiAdapter.MaxDecimations;
                    (c.Decimations, fallback, c.Animation.TotalFrames, c.RawBytes).Should().Be((0, false, 792, wire.Length), $"{name} {dir} zone {i}: every frame kept");
                    c.Animation.IntervalMs.Should().BeApproximately(72.0, 0.005);
                    c.Compressed.Length.Should().BeLessThanOrEqualTo(LianLiWire.MaxCompressedAnimation);
                    var roundTrip = enc.Decode(c.Compressed, wire.Length).AsSpan().SequenceEqual(wire);
                    roundTrip.Should().BeTrue($"{name} {dir} zone {i}: byte-identical decode of the mounted wire input");
                    var line = $"Stacking {name} {dir} z{i} 792x{leds} @ {z.IntervalMs} ms: raw {wire.Length} -> {c.Compressed.Length} B (limit {LianLiWire.MaxCompressedAnimation}), frames {c.Animation.TotalFrames}, decimations {c.Decimations}, static fallback {fallback}, round-trip {roundTrip}";
                    if (i == 3)
                    {
                        var nearestWire = MountingMap.Apply(families[i].ToWireOrder(nearestH2, leds), leds, maps[i]).ToArray();
                        var n = LianLiAdapter.Compress(enc, new LoopFrames(792, leds, z.IntervalMs, 1, nearestWire), LianLiWire.MaxCompressedAnimation);
                        line += $"; nearest (WP-15) H2 for comparison {n.Compressed.Length} B, decimations {n.Decimations}";
                    }

                    report.Add(line);
                }
            }
        }

        File.WriteAllLines(Path.Combine(Path.GetTempPath(), "urgb-wp16-stacking.txt"), report);
        Console.WriteLine(string.Join(Environment.NewLine, report));
    }
}
