using UnifiedRgb.Devices.LianLi.Codec;
using UnifiedRgb.Core.Effects;
using UnifiedRgb.Core.Effects.LianLi;
using UnifiedRgb.Core.Engine;
using UnifiedRgb.Devices.LianLi;
using UnifiedRgb.Devices.LianLi.Protocol.Families;

namespace UnifiedRgb.Devices.Tests;

public class ManagedCodecTests
{
    [Fact]
    public void Fuzzed_input_and_malformed_streams_are_bounded_and_concurrent_calls_share_no_state()
    {
        var codec = new ManagedTinyuzEncoder();
        Parallel.For(0, 128, seed =>
        {
            var random = new Random(seed);
            var bytes = new byte[random.Next(1, 8192)]; random.NextBytes(bytes);
            if ((seed & 1) == 0)
            {
                var period = random.Next(1, 100);
                for (var i = period; i < bytes.Length; i++) { bytes[i] = bytes[i % period]; }
            }
            ManagedTinyuzEncoder.Decode(codec.Encode(bytes), bytes.Length).Should().Equal(bytes);
            for (var i = 0; i < 16; i++)
            {
                var malformed = new byte[random.Next(6, 64)]; random.NextBytes(malformed);
                malformed[0] = 32; malformed[1] = 0; malformed[2] = 0; malformed[3] = 0;
                try { ManagedTinyuzEncoder.Decode(malformed, 128); }
                catch (InvalidDataException) { }
            }
        });
    }

    [Fact]
    public void All_group_effects_and_directions_keep_exact_frames_under_the_RF_budget_without_a_vendor_library()
    {
        var encoder = new ManagedTinyuzEncoder();
        LConnectZone[] zones = [new("TL", 3, 78), new("TL", 3, 78, ReverseChain: true), new("H2", 1, 24, FaceOnFill: true), new("TL", 3, 78)];
        Rgb[] colors = [new(255, 0, 0), new(24, 128, 255), new(255, 200, 32)];
        foreach (var effect in LConnectLoops.GroupRoutes.Keys)
        foreach (var direction in new[] { Direction.Forward, Direction.Reverse })
        {
            var plan = LConnectLoops.TryPlan(effect, new EffectParams(colors, .75f, 1, direction), colors, zones);
            plan.Should().NotBeNull(effect);
            for (var i = 0; i < zones.Length; i++)
            {
                var zone = plan!.Zones[i];
                var family = FamilyDetector.ByFamily(zones[i].Family)!;
                var mounting = i == 1 ? new LianLiMounting { ReverseChain = true }
                    : i == 2 ? new LianLiMounting { RotationDegrees = 180, FaceOnFill = true } : new LianLiMounting();
                MountingMap.TryBuild(family, zones[i].LedCount, mounting, out var map, out var error).Should().BeTrue(error);
                var bytes = MountingMap.Apply(family.ToWireOrder(zone.Rgb, zones[i].LedCount), zones[i].LedCount, map).ToArray();
                var payload = LianLiAdapter.Compress(encoder, new LoopFrames(zone.Frames, zones[i].LedCount, zone.IntervalMs, 1, bytes), 12288);
                payload.Decimations.Should().Be(0, $"{effect}/{direction}/{i}: no frame loss");
                payload.Animation.TotalFrames.Should().Be(zone.Frames);
                payload.Animation.IntervalMs.Should().Be(zone.IntervalMs);
                ManagedTinyuzEncoder.Decode(payload.Compressed, bytes.Length).Should().Equal(bytes);
            }
        }
    }

    [Fact]
    public void Stacking_with_wave_return_keeps_complete_full_brightness_animation_without_private_prerequisites()
    {
        ZoneRef Z(int i) => new(new ControllerId("codec-test:" + i), new ZoneId(0));
        var refs = Enumerable.Range(0, 9).Select(Z).ToArray();
        var path = new SpatialPath([
            new(36, [new(refs[1], true)]), new(36, [new(refs[0], true)]), new(36, [new(refs[3], true)]),
            new(12, [new(refs[4]), new(refs[8])]), new(24, [new(refs[2], true), new(refs[5]), new(refs[6]), new(refs[7])])]);
        var resolved = path.Resolve();
        var spans = refs.Select(r => resolved[r]).ToArray();
        LConnectZone[] zones = [new("TL", 3, 78), new("TL", 3, 78, ReverseChain: true), new("H2", 1, 24, FaceOnFill: true), new("TL", 3, 78),
            new("Streamed", 0, 12), new("Streamed", 0, 2), new("Streamed", 0, 8), new("Streamed", 0, 8), new("Streamed", 0, 4)];
        Rgb[] red = [new(255, 0, 0)];
        var plan = LConnectLoops.TryPlan("Stacking", new EffectParams(red, .75f, 1, Direction.Forward), red, zones, spans,
            new LoopReturn(3000, 600, 40, .45), out var why);
        plan.Should().NotBeNull(why);
        var encoder = new ManagedTinyuzEncoder();
        for (var i = 0; i < 4; i++)
        {
            var zone = plan!.Zones[i];
            zone.Frames.Should().Be(1056);
            var payload = LianLiAdapter.Compress(encoder, new LoopFrames(zone.Frames, zones[i].LedCount, zone.IntervalMs, 1, zone.Rgb), 12288);
            payload.Decimations.Should().Be(0);
            ManagedTinyuzEncoder.Decode(payload.Compressed, zone.Rgb.Length).Should().Equal(zone.Rgb);
        }
    }

    [Theory]
    [InlineData("01000000f941970100", 48, "41")]
    [InlineData("01000000bd00ff6500", 480, "00")]
    [InlineData("03000000f7414243fe86030100", 480, "414243")]
    [InlineData("100000008a00000102030405060708090a0b0c0d0e0f7715100c00", 480, "000102030405060708090a0b0c0d0e0f")]
    public void Decodes_independent_reference_vectors(string packed, int size, string pattern)
    {
        var unit = Convert.FromHexString(pattern);
        var expected = Enumerable.Range(0, size).Select(i => unit[i % unit.Length]).ToArray();
        ManagedTinyuzEncoder.Decode(Convert.FromHexString(packed), size).Should().Equal(expected);
    }

    [Fact]
    public void Exact_deterministic_round_trips_including_short_noise_overlaps_and_long_matches()
    {
        var random = new Random(20261020);
        var codec = new ManagedTinyuzEncoder();
        foreach (var size in new[] { 1, 2, 3, 7, 15, 16, 127, 128, 2687, 65535, 65536, 96000, 300000 })
        {
            var noise = new byte[size]; random.NextBytes(noise);
            foreach (var bytes in new[] { noise, new byte[size], Enumerable.Range(0, size).Select(i => (byte)(i % 3)).ToArray(), TinyuzEncoder.SampleAnimation(size) })
            {
                var packed = codec.Encode(bytes);
                System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(packed).Should().BeLessThanOrEqualTo(4096,
                    "the desktop decoder accepting a larger window does not prove firmware SRAM capacity");
                ManagedTinyuzEncoder.Decode(packed, size).Should().Equal(bytes);
                codec.Encode(bytes).Should().Equal(packed);
                packed.Length.Should().BeLessThanOrEqualTo(size + ((size + 7) / 8) + 16);
            }
        }
    }

    [Fact]
    public void Rejects_corruption_truncation_length_mismatch_and_unbounded_input()
    {
        var valid = Convert.FromHexString("03000000f7414243fe86030100");
        for (var length = 0; length < valid.Length; length++)
        {
            var cut = valid[..length];
            var decode = () => ManagedTinyuzEncoder.Decode(cut, 480);
            decode.Should().Throw<InvalidDataException>();
        }
        foreach (var size in new[] { 479, 481 })
        { var wrong = () => ManagedTinyuzEncoder.Decode(valid, size); wrong.Should().Throw<InvalidDataException>(); }
        foreach (var hex in new[] { "00000000bd00ff6500", "ffffffffbd00ff6500", "010000000001", "01000000ffffffffffffffffffffffff" })
        { var bad = () => ManagedTinyuzEncoder.Decode(Convert.FromHexString(hex), 480); bad.Should().Throw<InvalidDataException>(); }
        var empty = () => new ManagedTinyuzEncoder().Encode([]);
        empty.Should().Throw<ArgumentOutOfRangeException>();
        var bound = () => ManagedTinyuzEncoder.Decode(valid, ManagedTinyuzEncoder.MaxInputBytes + 1);
        bound.Should().Throw<ArgumentOutOfRangeException>();
    }

    [LocalCodecFact]
    public void Vendor_decoder_accepts_managed_streams_and_managed_decoder_accepts_vendor_streams()
    {
        using var vendor = TinyuzEncoder.Open(YuzCodecTests.DllPath);
        var codec = new ManagedTinyuzEncoder();
        foreach (var size in new[] { 480, 24000, 96000, 255000 })
        {
            foreach (var data in new[] { new byte[size], TinyuzEncoder.SampleAnimation(size) })
            {
                vendor.Decode(codec.Encode(data), size).Should().Equal(data);
                ManagedTinyuzEncoder.Decode(vendor.Encode(data), size).Should().Equal(data);
            }
        }
        // Independent decoder checks around variable-length position, length and rolling-dictionary boundaries.
        var random = new Random(101);
        foreach (var distance in new[] { 127, 128, 129, 2687, 2688, 65534, 65535, 65536 })
        {
            var data = new byte[(distance * 2) + 19];
            random.NextBytes(data.AsSpan(0, distance));
            for (var i = distance; i < data.Length; i++) { data[i] = data[i % distance]; }
            vendor.Decode(codec.Encode(data), data.Length).Should().Equal(data);
        }
    }
}
