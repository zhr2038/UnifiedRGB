using UnifiedRgb.Core.Effects;
using UnifiedRgb.Core.Effects.LianLi;
using UnifiedRgb.Devices.LianLi;
using UnifiedRgb.Devices.LianLi.Codec;
using UnifiedRgb.Devices.LianLi.Protocol;
using UnifiedRgb.Devices.LianLi.Protocol.Families;

namespace UnifiedRgb.Devices.Tests;

/// <summary>
/// NATIVE-STACKING-MIXED-ADDENDUM §6 case 6: the padded mixed-group Stacking plans through the real private CPU codec, using
/// the owner's pinned copy (tools/lianli-codec/yuz.dll, SHA-256 checked) rather than the vendor install. Offline only: the
/// codec compresses and decodes in process; nothing is sent. An absent codec fails loudly unless URGB_ALLOW_MISSING_CODEC=1
/// (then UNAVAILABLE is reported, never a pass as a measurement). Actual bytes are written to the report for SELF_AUDIT.
/// </summary>
public class LianLiStackingMixedCodecTests
{
    private static readonly PacketAssembler Tl = FamilyDetector.ByFamily("TL")!;
    private static readonly PacketAssembler H2 = FamilyDetector.ByFamily("H2")!;

    private static string OwnedCodec => Path.GetFullPath(Path.Combine(Fixture.RepoRoot(), YuzCodecTests.OwnedCodecRelative));

    [LocalOwnedCodecFact]
    public void Padded_mixed_stacking_keeps_every_frame_through_the_owned_real_codec()
    {
        if (!File.Exists(OwnedCodec))
        {
            var message = $"UNAVAILABLE (prerequisite): owned codec {OwnedCodec} not present; mixed Stacking budget not measured";
            Console.WriteLine(message);
            Environment.GetEnvironmentVariable("URGB_ALLOW_MISSING_CODEC").Should().Be("1", message);
            return;
        }

        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(OwnedCodec))).Should().Be(YuzCodecTests.OwnedCodecSha256);
        using var enc = TinyuzEncoder.Open(OwnedCodec);

        // the literal preserved scene order (Codex review): side 06 TL140, top 1B TL120 ReverseChain, H2 EF FaceOnFill whole-step
        // with the saved calibration (rotation 180, mirror, offset 0) as its wire map, bottom 35 TL140 LCD; plus one permutation
        // (bottom, side, top RC, H2) for placement coverage. TL chains have identity wire maps.
        var tl = new LianLiMounting();
        var top = new LianLiMounting { ReverseChain = true };
        var ring = new LianLiMounting { RotationDegrees = 180, Reverse = true, RingOffsetLeds = 0, FaceOnFill = true };
        var orders = new (string Name, (LConnectZone Zone, LianLiMounting Mounting, PacketAssembler Family)[] Chains)[]
        {
            ("live 06/1B-RC/EF/35", [(new("TL", 3, 78), tl, Tl), (new("TL", 3, 78, ReverseChain: true), top, Tl), (new("H2", 1, 24, FaceOnFill: true), ring, H2), (new("TL", 3, 78), tl, Tl)]),
            ("permuted 35/06/1B-RC/EF", [(new("TL", 3, 78), tl, Tl), (new("TL", 3, 78), tl, Tl), (new("TL", 3, 78, ReverseChain: true), top, Tl), (new("H2", 1, 24, FaceOnFill: true), ring, H2)]),
        };

        var point = new LConnectZone("Streamed", 0, 1);
        var sets = new (string Name, LConnectZone[] Streamed, int Frames)[]
        {
            ("live only", [], 792),
            ("+4 Points", [point, point, point, point], 1440),
            ("+mapped 34/12/8/8 (illustrative, not discovered topology)", [new("Streamed", 0, 34), new("Streamed", 0, 12), new("Streamed", 0, 8), new("Streamed", 0, 8)], 1836),
        };

        Rgb[] red = [new(255, 0, 0)];
        var report = new List<string> { $"codec {enc.CodecHash} at {OwnedCodec} (owned copy; offline compress/decode only)" };
        var worst = 0;
        foreach (var (orderName, chains) in orders)
        {
            var live = chains.Select(c => c.Zone).ToArray();
            var maps = new int[]?[chains.Length];
            for (var i = 0; i < chains.Length; i++)
            {
                MountingMap.TryBuild(chains[i].Family, live[i].LedCount, chains[i].Mounting, out maps[i], out var error).Should().BeTrue(error);
            }

            foreach (var (name, streamed, frames) in sets)
            {
                foreach (var first in streamed.Length == 0 ? [false] : new[] { false, true })
                {
                    LConnectZone[] zones = first ? [.. streamed, .. live] : [.. live, .. streamed];
                    var at = first ? streamed.Length : 0; // index of live zone 0
                    foreach (var dir in new[] { Direction.Forward, Direction.Reverse })
                    {
                        foreach (var (speed, interval) in new[] { (1f, 36.0), (0.25f, 72.0) })
                        {
                            var plan = LConnectLoops.TryPlan("Stacking", new EffectParams(red, speed, 1f, dir), red, zones)!;
                            if (streamed.Length > 0)
                            {
                                plan.Variant.Should().Contain("streamed zone(s) padded to 12-dot deposits");
                            }

                            for (var i = 0; i < live.Length; i++)
                            {
                                var z = plan.Zones[at + i];
                                var leds = live[i].LedCount;
                                var family = chains[i].Family;
                                var placement = streamed.Length == 0 ? "no streamed" : first ? "streamed-first" : "streamed-last";
                                var tag = $"{orderName} {name} {placement} {dir} speed {speed} z{i} {live[i].Family}{(live[i].ReverseChain ? " RC" : string.Empty)}";
                                (z.Frames, z.IntervalMs).Should().Be((frames, interval), tag);
                                var wire = MountingMap.Apply(family.ToWireOrder(z.Rgb, leds), leds, maps[i]).ToArray();
                                wire.Length.Should().Be(frames * leds * 3);
                                var p = LianLiAdapter.Compress(enc, new LoopFrames(frames, leds, z.IntervalMs, 1, wire), LianLiWire.MaxCompressedAnimation);
                                var fallback = p.Decimations > LianLiAdapter.MaxDecimations;
                                (p.Decimations, fallback, p.Animation.TotalFrames, p.RawBytes).Should().Be((0, false, frames, wire.Length), $"{tag}: every frame kept");
                                p.Animation.IntervalMs.Should().BeApproximately(interval, 0.005, tag);
                                p.Compressed.Length.Should().BeLessThanOrEqualTo(LianLiWire.MaxCompressedAnimation, tag);
                                var roundTrip = enc.Decode(p.Compressed, wire.Length).AsSpan().SequenceEqual(wire);
                                roundTrip.Should().BeTrue($"{tag}: byte-identical decode of the mounted wire input");
                                worst = Math.Max(worst, p.Compressed.Length);
                                report.Add($"Stacking {tag} {frames}x{leds} @ {z.IntervalMs} ms: raw {wire.Length} -> {p.Compressed.Length} B (limit {LianLiWire.MaxCompressedAnimation}), frames {p.Animation.TotalFrames}, decimations {p.Decimations}, static fallback {fallback}, round-trip {roundTrip}");
                            }
                        }
                    }
                }
            }
        }

        report.Add($"worst compressed Lian Li payload {worst} B of {LianLiWire.MaxCompressedAnimation}");
        File.WriteAllLines(Path.Combine(Path.GetTempPath(), "urgb-native-stacking-mixed.txt"), report);
        Console.WriteLine(string.Join(Environment.NewLine, report));
    }
}
