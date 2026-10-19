using UnifiedRgb.Core.Effects;
using UnifiedRgb.Core.Effects.LianLi;
using UnifiedRgb.Devices.LianLi;
using UnifiedRgb.Devices.LianLi.Codec;
using UnifiedRgb.Devices.LianLi.Protocol;
using UnifiedRgb.Devices.LianLi.Protocol.Families;

namespace UnifiedRgb.Devices.Tests;

/// <summary>
/// WP-13 (DEBUG-SESSION-3 §4/§6/§7): final wire order, the fan-local mirror composed with the group bodies, the separate
/// whole-chain reversal, and the strict real-codec Stacking measurement for the four mounted chains.
/// </summary>
public class LianLiChainOrderTests
{
    private static readonly PacketAssembler Tl = FamilyDetector.ByFamily("TL")!;
    private static readonly PacketAssembler H2 = FamilyDetector.ByFamily("H2")!;
    private static readonly Rgb[] RedGreen = [new(255, 0, 0), new(0, 255, 0)];

    private static LConnectZone[] Group(bool reverseTl120 = false) =>
        [new("TL", 3, 78), new("TL", 3, 78, reverseTl120), new("H2", 1, 24), new("TL", 3, 78)];

    private static int[] MirrorMap(PacketAssembler family, int leds)
    {
        MountingMap.TryBuild(family, leds, new LianLiMounting { Reverse = true }, out var map, out var error).Should().BeTrue(error);
        return map!;
    }

    /// <summary>The TL120 loop as it goes on the wire: assembler order, then the optional mounting map.</summary>
    private static byte[] Wire(LConnectZoneLoop z, int[]? map) =>
        MountingMap.Apply(Tl.ToWireOrder(z.Rgb, 78), 78, map).ToArray();

    [Fact]
    public void Tl_wire_order_is_fan_major_then_edge_then_k()
    {
        // canonical (row, fan·13 + k) → wire fan·26 + row·13 + k, derived from the TL packet layout, not from ToWireOrder
        var canonical = new byte[78 * 3];
        for (var row = 0; row < 2; row++)
        {
            for (var p = 0; p < 39; p++)
            {
                canonical[((row * 39) + p) * 3] = (byte)row;
                canonical[(((row * 39) + p) * 3) + 1] = (byte)p;
                canonical[(((row * 39) + p) * 3) + 2] = 0xA5;
            }
        }

        var wire = Tl.ToWireOrder(canonical, 78).ToArray();
        for (var fan = 0; fan < 3; fan++)
        {
            for (var row = 0; row < 2; row++)
            {
                for (var k = 0; k < 13; k++)
                {
                    var o = ((fan * 26) + (row * 13) + k) * 3;
                    (wire[o], wire[o + 1], wire[o + 2]).Should().Be(((byte)row, (byte)((fan * 13) + k), (byte)0xA5), $"fan {fan} row {row} k {k}");
                }
            }
        }

        H2.ToWireOrder(canonical.AsMemory(0, 24 * 3), 24).ToArray().Should().Equal(canonical[..(24 * 3)], "the H2 ring is already in wire order");
    }

    /// <summary>First frame in which a wire fan (26 LEDs) of the TL120 loop shows red only, on one edge (13 LEDs).</summary>
    private static int[] WireOnsets(byte[] wire, int frames, int row) =>
        Enumerable.Range(0, 3).Select(fan => Enumerable.Range(0, frames).First(f => Enumerable.Range(0, 13).Any(k =>
        {
            var o = ((f * 78) + (fan * 26) + (row * 13) + k) * 3;
            return wire[o] > 0 && wire[o + 1] == 0 && wire[o + 2] == 0;
        }))).ToArray();

    private static int[] FanHistogram(byte[] wire, int f, int fan) =>
        Enumerable.Range(0, 26).Select(i => ((f * 78) + (fan * 26) + i) * 3).Select(o => (wire[o] << 16) | (wire[o + 1] << 8) | wire[o + 2]).Order().ToArray();

    [Theory]
    [InlineData("Meteor")]
    [InlineData("CoverCycle")]
    public void Mounting_mirror_preserves_fan_onsets_and_histograms_while_chain_reversal_reorders_fans(string effect)
    {
        var mirror = MirrorMap(Tl, 78);
        foreach (var (dir, expected, chain) in new[]
        {
            (Direction.Forward, new[] { 40, 53, 66 }, false),
            (Direction.Reverse, new[] { 90, 77, 64 }, false),
            (Direction.Reverse, new[] { 64, 77, 90 }, true),
            (Direction.Forward, new[] { 66, 53, 40 }, true),
        })
        {
            var plan = LConnectLoops.TryPlan(effect, new EffectParams(RedGreen, 0.5f, 1f, dir), RedGreen, Group(chain))!;
            var z = plan.Zones[1];
            var plain = Wire(z, null);
            var mirrored = Wire(z, mirror);
            foreach (var row in new[] { 0, 1 })
            {
                WireOnsets(plain, z.Frames, row).Should().Equal(expected, $"{effect} {dir} chain {chain} edge {row}, mirror off");
                WireOnsets(mirrored, z.Frames, row).Should().Equal(expected, $"{effect} {dir} chain {chain} edge {row}, mirror on");
            }

            for (var f = 0; f < z.Frames; f++)
            {
                for (var fan = 0; fan < 3; fan++)
                {
                    FanHistogram(mirrored, f, fan).Should().Equal(FanHistogram(plain, f, fan), $"{effect} {dir} frame {f} fan {fan}: the mirror is fan-local");
                }
            }
        }
    }

    [Fact]
    public void Chain_reversal_is_separate_from_the_wire_map_and_visible_in_the_description()
    {
        var chainOnly = new LianLiMounting { ReverseChain = true };
        MountingMap.TryBuild(Tl, 78, chainOnly, out var map, out _).Should().BeTrue();
        map.Should().BeNull("whole-chain order is applied at slicing, never in the wire map");
        MountingMap.Describe(chainOnly).Should().Be("fan order reversed");
        MountingMap.Describe(new LianLiMounting { Reverse = true, ReverseChain = true }).Should().Be("0° mirrored; fan order reversed");
        MountingMap.Describe(new LianLiMounting { Reverse = true }).Should().Be("0° mirrored");

        MountingMap.TryBuild(Tl, 78, new LianLiMounting { Reverse = true, ReverseChain = true }, out var both, out _).Should().BeTrue();
        both.Should().Equal(MirrorMap(Tl, 78), "the chain flag does not alter the fan-local mirror");
        new LianLiMounting { ReverseChain = true }.Clone().ReverseChain.Should().BeTrue();
    }

    /// <summary>
    /// §6.1/§7: Stacking (explicit red) through the real codec for all four chains with the persisted mounting (TL120 and H2
    /// mirrored), both directions: zero decimations, 792 frames (WP-15 fan-aligned plan) at the vendor interval, ≤ 12,288 B,
    /// byte-identical decode.
    /// </summary>
    [LocalCodecFact]
    public void Stacking_compresses_losslessly_for_all_four_mounted_chains_with_the_real_codec()
    {
        if (!File.Exists(YuzCodecTests.DllPath))
        {
            Console.WriteLine($"SKIPPED (prerequisite unavailable): {YuzCodecTests.DllPath} not present");
            return;
        }

        using var enc = TinyuzEncoder.Open(YuzCodecTests.DllPath);
        var zones = Group();
        PacketAssembler[] families = [Tl, Tl, H2, Tl];
        int[]?[] maps = [null, MirrorMap(Tl, 78), MirrorMap(H2, 24), null];
        Rgb[] red = [new(255, 0, 0)];
        var report = new List<string> { $"codec {enc.CodecHash}" };
        foreach (var dir in new[] { Direction.Forward, Direction.Reverse })
        {
            var plan = LConnectLoops.TryPlan("Stacking", new EffectParams(red, 0.5f, 1f, dir), red, zones)!;
            for (var i = 0; i < 4; i++)
            {
                var z = plan.Zones[i];
                var leds = zones[i].LedCount;
                z.Frames.Should().Be(792);
                var wire = MountingMap.Apply(families[i].ToWireOrder(z.Rgb, leds), leds, maps[i]).ToArray();
                wire.Length.Should().Be(792 * leds * 3);
                var p = LianLiAdapter.Compress(enc, new LoopFrames(792, leds, z.IntervalMs, 1, wire), LianLiWire.MaxCompressedAnimation);
                (p.Decimations, p.Animation.TotalFrames, p.RawBytes).Should().Be((0, 792, wire.Length), $"{dir} zone {i}");
                p.Animation.IntervalMs.Should().BeApproximately(z.IntervalMs, 0.005, $"{dir} zone {i}");
                p.Compressed.Length.Should().BeLessThanOrEqualTo(LianLiWire.MaxCompressedAnimation);
                enc.Decode(p.Compressed, wire.Length).Should().Equal(wire, $"{dir} zone {i}: byte-identical decode of the mounted wire input");
                report.Add($"Stacking {dir} z{i} 792x{leds}: raw {wire.Length} -> {p.Compressed.Length} B, decimations {p.Decimations}");
            }
        }

        File.WriteAllLines(Path.Combine(Path.GetTempPath(), "urgb-wp13-stacking.txt"), report);
        Console.WriteLine(string.Join(Environment.NewLine, report));
    }
}
