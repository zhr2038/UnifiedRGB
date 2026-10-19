using UnifiedRgb.Core.Effects;
using UnifiedRgb.Core.Effects.LianLi;
using UnifiedRgb.Core.Engine;
using UnifiedRgb.Devices.LianLi;
using UnifiedRgb.Devices.LianLi.Codec;
using UnifiedRgb.Devices.LianLi.Protocol;
using UnifiedRgb.Devices.LianLi.Protocol.Families;

namespace UnifiedRgb.Devices.Tests;

/// <summary>
/// STACK-RETURN (design/OPUS-STACKING-WAVE-RETURN-20261018.md): profile11's accepted path plus the 600 ms hold and 4 s
/// reverse-flow drain, planned over all nine profile11 zones, then the four RF loops through the owner's pinned private CPU
/// codec (tools/lianli-codec/yuz.dll, SHA-256 checked) with the accepted wire mountings. Offline only: nothing is sent.
/// Every 1090-frame loop must keep all frames under the 12,288 B limit and decode byte-exactly; the forward-only baseline is
/// measured beside it and must equal the recorded live profile11 payload sizes. An absent codec fails loudly unless URGB_ALLOW_MISSING_CODEC=1. Actual bytes go to the report.
/// </summary>
public class LianLiStackingReturnCodecTests
{
    private static readonly PacketAssembler Tl = FamilyDetector.ByFamily("TL")!;
    private static readonly PacketAssembler H2 = FamilyDetector.ByFamily("H2")!;

    private static string OwnedCodec => Path.GetFullPath(Path.Combine(Fixture.RepoRoot(), YuzCodecTests.OwnedCodecRelative));

    private static ZoneRef Z(string id, int zone = 0) => new(new ControllerId(id), new ZoneId(zone));

    [LocalOwnedCodecFact]
    public void Profile11_with_the_reverse_flow_return_keeps_every_frame_through_the_owned_real_codec()
    {
        if (!File.Exists(OwnedCodec))
        {
            var message = $"UNAVAILABLE (prerequisite): owned codec {OwnedCodec} not present; Stacking return budget not measured";
            Console.WriteLine(message);
            Environment.GetEnvironmentVariable("URGB_ALLOW_MISSING_CODEC").Should().Be("1", message);
            return;
        }

        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(OwnedCodec))).Should().Be(YuzCodecTests.OwnedCodecSha256);
        using var enc = TinyuzEncoder.Open(OwnedCodec);

        // profile11's targets (side, top RC, H2, bottom, GPU vertical, ROG, RAM71, RAM73, GPU horizontal) and accepted path
        var side = Z("lianli:rf:02-00-00-00-00-01");
        var top = Z("lianli:rf:02-00-00-00-00-02");
        var h2 = Z("lianli:rf:02-00-00-00-00-03");
        var bottom = Z("lianli:rf:02-00-00-00-00-04");
        var gpu = Z("msigpu:0:53011462");
        var gpuH = Z("msigpu:0:53011462", 1);
        var rog = Z("asusaura:19AF:0");
        var ram71 = Z("enedram:p0:0x71");
        var ram73 = Z("enedram:p0:0x73");
        ZoneRef[] targets = [side, top, h2, bottom, gpu, rog, ram71, ram73, gpuH];
        LConnectZone[] zones =
        [
            new("TL", 3, 78), new("TL", 3, 78, ReverseChain: true), new("H2", 1, 24, FaceOnFill: true), new("TL", 3, 78),
            new("Streamed", 0, 12), new("Streamed", 0, 2), new("Streamed", 0, 8), new("Streamed", 0, 8), new("Streamed", 0, 4),
        ];
        var path = new SpatialPath([
            new SpatialStage(36, [new SpatialBinding(top, true)]),
            new SpatialStage(36, [new SpatialBinding(side, true)]),
            new SpatialStage(36, [new SpatialBinding(bottom, true)]),
            new SpatialStage(12, [new SpatialBinding(gpu), new SpatialBinding(gpuH)]),
            new SpatialStage(24, [new SpatialBinding(h2, true), new SpatialBinding(ram71), new SpatialBinding(ram73), new SpatialBinding(rog)]),
        ]);
        var resolved = path.Resolve();
        var spans = targets.Select(t => resolved[t]).ToArray();

        // the accepted wire mountings: side/bottom identity, top ReverseChain only, H2 rotation 180 / mirror / offset 0 / FaceOnFill
        var tl = new LianLiMounting();
        var rc = new LianLiMounting { ReverseChain = true };
        var ring = new LianLiMounting { RotationDegrees = 180, Reverse = true, RingOffsetLeds = 0, FaceOnFill = true };
        (string Name, LianLiMounting Mounting, PacketAssembler Family)[] chains = [("side", tl, Tl), ("top RC", rc, Tl), ("H2", ring, H2), ("bottom", tl, Tl)];

        Rgb[] red = [new(255, 0, 0)];
        var report = new List<string> { $"codec {enc.CodecHash} at {OwnedCodec} (owned copy; offline compress/decode only)" };
        var compressed = new Dictionary<(string Chain, string Run), int>();
        foreach (var (run, loopReturn, frames) in new (string, LoopReturn?, int)[] { ("forward only", null, 936), ("with return", new LoopReturn(4000, 600, 24, 0.25), 1090) })
        {
            var plan = LConnectLoops.TryPlan("Stacking", new EffectParams(red, 0.75f, 1f, Direction.Forward), red, zones, spans, loopReturn, out var why);
            plan.Should().NotBeNull(why);
            plan!.Zones.Should().OnlyContain(z => z.Frames == frames && z.IntervalMs == 48, $"{run}: one shared timeline");
            for (var i = 0; i < chains.Length; i++)
            {
                var (name, mounting, family) = chains[i];
                var z = plan.Zones[i];
                var leds = zones[i].LedCount;
                MountingMap.TryBuild(family, leds, mounting, out var map, out var error).Should().BeTrue(error);
                var wire = MountingMap.Apply(family.ToWireOrder(z.Rgb, leds), leds, map).ToArray();
                wire.Length.Should().Be(frames * leds * 3);
                var p = LianLiAdapter.Compress(enc, new LoopFrames(frames, leds, z.IntervalMs, 1, wire), LianLiWire.MaxCompressedAnimation);
                var fallback = p.Decimations > LianLiAdapter.MaxDecimations;
                var tag = $"{run} {name}";
                (p.Decimations, fallback, p.Animation.TotalFrames, p.RawBytes).Should().Be((0, false, frames, wire.Length), $"{tag}: every frame kept");
                p.Animation.IntervalMs.Should().BeApproximately(48, 0.005, tag);
                p.Compressed.Length.Should().BeLessThanOrEqualTo(LianLiWire.MaxCompressedAnimation, tag);
                var roundTrip = enc.Decode(p.Compressed, wire.Length).AsSpan().SequenceEqual(wire);
                roundTrip.Should().BeTrue($"{tag}: byte-identical decode of the mounted wire input");
                compressed[(name, run)] = p.Compressed.Length;
                report.Add($"Stacking profile11 {tag} {frames}x{leds} @ {z.IntervalMs} raw ticks: raw {wire.Length} -> {p.Compressed.Length} B (limit {LianLiWire.MaxCompressedAnimation}), frames {p.Animation.TotalFrames}, decimations {p.Decimations}, static fallback {fallback}, round-trip {roundTrip}");
            }
        }

        // the forward-only loops are the accepted profile11 payloads: Root's recorded live uploads
        // (bringup/morning-native-20261018/inner-stack-full-deployment.json) were side 1711, top 2272, H2 193, bottom 1056 B
        chains.Select(c => compressed[(c.Name, "forward only")]).Should().Equal(1711, 2272, 193, 1056);
        foreach (var (name, _, _) in chains)
        {
            report.Add($"{name}: return adds {compressed[(name, "with return")] - compressed[(name, "forward only")]} B over the forward-only loop");
        }

        File.WriteAllLines(Path.Combine(Path.GetTempPath(), "urgb-stacking-return-codec.txt"), report);
        Console.WriteLine(string.Join(Environment.NewLine, report));
    }
}
