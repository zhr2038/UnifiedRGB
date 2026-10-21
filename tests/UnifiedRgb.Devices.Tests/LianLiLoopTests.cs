using System.Buffers.Binary;
using Microsoft.Extensions.Logging.Abstractions;
using UnifiedRgb.Devices.LianLi;
using UnifiedRgb.Devices.LianLi.Codec;
using UnifiedRgb.Devices.LianLi.Protocol;
using UnifiedRgb.Devices.Safety;
using UnifiedRgb.Transports.Safety;
using UnifiedRgb.Transports.WinUsb;

namespace UnifiedRgb.Devices.Tests;

/// <summary>Compresses to a fixed size per input length (callback), so the D2 §4 size fallback can be driven.</summary>
internal sealed class SizedEncoder(Func<int, int> size) : IRgbAnimationEncoder
{
    public List<int> Inputs { get; } = [];

    public byte[] Encode(ReadOnlySpan<byte> rgbData)
    {
        Inputs.Add(rgbData.Length);
        return new byte[size(rgbData.Length)];
    }
}

/// <summary>ARCHITECTURE-D2 §3: the real codec. yuz.dll is loaded in place from the L-Connect 3 directory, never copied.</summary>
public class YuzCodecTests
{
    /// <summary>Optional local-only codec. Public builds never acquire or ship a vendor DLL.</summary>
    public static string DllPath =>
        Environment.GetEnvironmentVariable("URGB_YUZ_DLL") is { Length: > 0 } p ? p
            : Path.Combine(Fixture.RepoRoot(), OwnedCodecRelative);

    [LocalCodecFact]
    public void Real_yuz_dll_passes_the_self_check_and_round_trips_loops()
    {
        if (!File.Exists(DllPath))
        {
            // Skip-and-report only when absent; on the owner's machine the dll is present and this runs for real.
            Console.WriteLine($"SKIPPED: {DllPath} not present");
            return;
        }

        using var enc = TinyuzEncoder.Open(DllPath);
        enc.Sha256.Should().MatchRegex("^[0-9A-F]{64}$");
        ((IRgbAnimationEncoder)enc).CodecHash.Should().Be(enc.Sha256);
        enc.Path.Should().Be(Path.GetFullPath(DllPath));
        enc.SelfCheck();

        foreach (var bytes in new[] { 480, 600 * 3, 200 * 160 * 3, 200 * 255 * 3 })
        {
            var x = TinyuzEncoder.SampleAnimation(bytes);
            var packed = enc.Encode(x);
            packed.Length.Should().BeInRange(1, bytes);
            enc.Decode(packed, bytes).Should().Equal(x);
        }

        var still = new byte[200 * 160 * 3];
        Array.Fill(still, (byte)0x40);
        enc.Encode(still).Length.Should().BeLessThan(LianLiWire.MaxCompressedAnimation, "a static 200-frame loop compresses far below 12 KiB");
        // The only repo copy is the owner's pinned codec asset (tools/lianli-codec, PROVENANCE.md, 2026-10-17); nothing else.
        var owned = Path.GetFullPath(Path.Combine(Fixture.RepoRoot(), OwnedCodecRelative));
        Directory.GetFiles(Fixture.RepoRoot(), "yuz.dll", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(f => !string.Equals(Path.GetFullPath(f), owned, StringComparison.OrdinalIgnoreCase))
            .Should().BeEmpty("no copy other than the pinned tools/lianli-codec asset");
        if (File.Exists(owned))
        {
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(owned)))
                .Should().Be(OwnedCodecSha256, "the owned copy is byte-identical to the deployed codec");
        }
    }

    /// <summary>The owner's local codec copy (tools/lianli-codec/PROVENANCE.md); same bytes as the deployed L-Connect codec.</summary>
    public const string OwnedCodecRelative = "tools/lianli-codec/yuz.dll";

    public const string OwnedCodecSha256 = "8131CA25583A238289556D2B36FA8CB35096E2AB8264BA82AA0170BD4C593A55";

    [LocalCodecFact]
    public void Real_codec_drives_the_adapter_compress_path()
    {
        if (!File.Exists(DllPath))
        {
            Console.WriteLine($"SKIPPED: {DllPath} not present");
            return;
        }

        using var enc = TinyuzEncoder.Open(DllPath);
        var rgb = TinyuzEncoder.SampleAnimation(200 * 160 * 3);
        var p = LianLiAdapter.Compress(enc, new LoopFrames(200, 160, 20, 1, rgb), LianLiWire.MaxCompressedAnimation);
        p.Compressed.Length.Should().BeLessThanOrEqualTo(LianLiWire.MaxCompressedAnimation);
        p.Animation.CompressedLength.Should().Be(p.Compressed.Length);
        var frames = p.Animation.TotalFrames;
        enc.Decode(p.Compressed, frames * 480).Should().HaveCount(frames * 480);
    }

    [Fact]
    public void Missing_or_foreign_files_are_blocked_not_loaded()
    {
        var missing = () => YuzNative.Load(null);
        missing.Should().Throw<DeviceBlockedException>().Which.Reason.Should().StartWith("yuz.dll missing");
        var nowhere = () => TinyuzEncoder.Open(Path.Combine(Path.GetTempPath(), "urgb-none", "yuz.dll"));
        nowhere.Should().Throw<DeviceBlockedException>().Which.Reason.Should().StartWith("yuz.dll missing");

        var notPe = Path.Combine(Path.GetTempPath(), $"urgb-{Guid.NewGuid():N}.dll");
        File.WriteAllBytes(notPe, new byte[256]);
        try
        {
            var bad = () => YuzNative.Load(notPe);
            bad.Should().Throw<DeviceBlockedException>().Which.Reason.Should().Contain("not an x64 PE");
        }
        finally
        {
            File.Delete(notPe);
        }
    }

    [Fact]
    public void Pe_machine_reads_the_coff_header()
    {
        var image = new byte[0x200];
        (image[0], image[1]) = ((byte)'M', (byte)'Z');
        BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(0x3C), 0x80);
        "PE\0\0"u8.CopyTo(image.AsSpan(0x80));
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x84), 0x8664);
        YuzNative.PeMachine(image).Should().Be(YuzNative.MachineAmd64);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x84), 0x014C);
        YuzNative.PeMachine(image).Should().Be(0x014C, "x86");
        BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(0x3C), 0x7FFF_0000);
        YuzNative.PeMachine(image).Should().Be(0);
        YuzNative.PeMachine(new byte[10]).Should().Be(0);
        if (File.Exists(YuzCodecTests.DllPath))
        {
            YuzNative.PeMachine(File.ReadAllBytes(YuzCodecTests.DllPath)).Should().Be(YuzNative.MachineAmd64);
        }
    }
}

public class LianLiLoopTests
{
    private static LoopFrames Loop(int frames, int leds = 160, double interval = 20, uint index = 7) =>
        new(frames, leds, interval, index, TinyuzEncoder.SampleAnimation(frames * leds * 3));

    [Fact]
    public void Compress_keeps_a_loop_that_fits()
    {
        var enc = new SizedEncoder(n => n / 10);
        var p = LianLiAdapter.Compress(enc, Loop(200), 12288);
        (p.Decimations, p.Animation.TotalFrames, p.Animation.IntervalMs, p.RawBytes, p.Compressed.Length).Should().Be((0, 200, 20.0, 96_000, 9_600));
    }

    [Fact]
    public void Compress_halves_frames_and_doubles_the_interval_until_it_fits()
    {
        var enc = new SizedEncoder(n => n / 2);
        var loop = Loop(200);
        var p = LianLiAdapter.Compress(enc, loop, 12288);
        enc.Inputs.Should().Equal(96_000, 48_000, 24_000);
        (p.Decimations, p.Animation.TotalFrames, p.Animation.IntervalMs, p.RawBytes).Should().Be((2, 50, 80.0, 24_000));
        (p.Animation.TotalFrames * p.Animation.IntervalMs).Should().Be(loop.Frames * loop.IntervalMs, "the period is preserved");
    }

    [Fact]
    public void Compress_falls_back_to_a_static_frame_after_three_decimations()
    {
        var enc = new SizedEncoder(n => n <= 480 ? 100 : 20_000);
        var p = LianLiAdapter.Compress(enc, Loop(200), 12288);
        enc.Inputs.Should().Equal(96_000, 48_000, 24_000, 12_000, 480);
        (p.Decimations, p.Animation.TotalFrames, p.RawBytes, p.Compressed.Length).Should().Be((LianLiAdapter.MaxDecimations + 1, 1, 480, 100));

        var hopeless = () => LianLiAdapter.Compress(new SizedEncoder(_ => 20_000), Loop(4), 12288);
        hopeless.Should().Throw<InvalidDataException>();
    }

    /// <summary>WP-12 §9: the long vendor loops (Meteor 153, Stacking 792 frames per TL slice since WP-15) through the size policy.</summary>
    [Theory]
    [InlineData("Meteor", 200, 2)]
    [InlineData("Stacking", 792, 6)]
    public void Long_vendor_loops_decimate_preserving_the_period_and_flag_a_static_fallback(string effect, int frames, int ratio)
    {
        var zones = new[] { new Core.Effects.LianLi.LConnectZone("TL", 3, 78), new Core.Effects.LianLi.LConnectZone("TL", 3, 78), new Core.Effects.LianLi.LConnectZone("H2", 1, 24), new Core.Effects.LianLi.LConnectZone("TL", 3, 78) };
        var plan = Core.Effects.LianLi.LConnectLoops.TryPlan(effect, new Core.Effects.EffectParams([new Rgb(255, 0, 0), new Rgb(0, 0, 255)], 0.5f), [new Rgb(255, 0, 0), new Rgb(0, 0, 255)], zones)!;
        var z = plan.Zones[0];
        z.Frames.Should().Be(frames);
        var loop = new LoopFrames(z.Frames, 78, z.IntervalMs, 1, z.Rgb);

        var fits = LianLiAdapter.Compress(new SizedEncoder(n => n / 20), loop, LianLiWire.MaxCompressedAnimation);
        (fits.Decimations, fits.Animation.TotalFrames).Should().Be((0, frames), "a loop that fits keeps every vendor frame");

        var halved = LianLiAdapter.Compress(new SizedEncoder(n => n / ratio), loop, LianLiWire.MaxCompressedAnimation);
        halved.Decimations.Should().BeInRange(1, LianLiAdapter.MaxDecimations);
        (halved.Animation.TotalFrames * halved.Animation.IntervalMs).Should().BeApproximately(frames * z.IntervalMs, 0.5 * halved.Animation.TotalFrames / 100.0 + 1e-6, "period preserved to the header's 0.01 ms");
        Math.Abs(halved.PeriodErrorMs).Should().BeLessThan(1);

        var still = LianLiAdapter.Compress(new SizedEncoder(n => n <= 78 * 3 ? 100 : 20_000), loop, LianLiWire.MaxCompressedAnimation);
        (still.Decimations > LianLiAdapter.MaxDecimations, still.Animation.TotalFrames).Should().Be((true, 1), "the static fallback is detectable");
    }

    /// <summary>WP-12 §9: measured with the real codec (owner's machine); numbers are printed for SELF_AUDIT.</summary>
    [LocalCodecFact]
    public void Long_vendor_loops_measured_with_the_real_codec()
    {
        if (!File.Exists(YuzCodecTests.DllPath))
        {
            Console.WriteLine($"SKIPPED: {YuzCodecTests.DllPath} not present");
            return;
        }

        using var enc = TinyuzEncoder.Open(YuzCodecTests.DllPath);
        var zones = new[] { new Core.Effects.LianLi.LConnectZone("TL", 3, 78), new Core.Effects.LianLi.LConnectZone("TL", 3, 78), new Core.Effects.LianLi.LConnectZone("H2", 1, 24), new Core.Effects.LianLi.LConnectZone("TL", 3, 78) };
        var report = new List<string>();
        foreach (var effect in Core.Effects.LianLi.LConnectLoops.GroupRoutes.Keys)
        {
            var colors = new[] { new Rgb(255, 0, 0), new Rgb(0, 255, 0), new Rgb(0, 0, 255), new Rgb(255, 255, 0) };
            var plan = Core.Effects.LianLi.LConnectLoops.TryPlan(effect, new Core.Effects.EffectParams(colors, 0.5f), colors, zones)!;
            foreach (var zi in new[] { 0, 2 })
            {
                var z = plan.Zones[zi];
                var leds = zones[zi].LedCount;
                var p = LianLiAdapter.Compress(enc, new LoopFrames(z.Frames, leds, z.IntervalMs, 1, z.Rgb), LianLiWire.MaxCompressedAnimation);
                p.Compressed.Length.Should().BeLessThanOrEqualTo(LianLiWire.MaxCompressedAnimation);
                p.Decimations.Should().BeLessThanOrEqualTo(LianLiAdapter.MaxDecimations, $"{effect} zone {zi} must not fall back to a static frame");
                enc.Decode(p.Compressed, p.Animation.TotalFrames * leds * 3).Length.Should().Be(p.Animation.TotalFrames * leds * 3);
                report.Add($"{effect} z{zi} {z.Frames}x{leds}: raw {z.Rgb.Length} -> {p.Compressed.Length} B, decimations {p.Decimations}, frames {p.Animation.TotalFrames}");
            }
        }

        File.WriteAllLines(Path.Combine(Path.GetTempPath(), "urgb-wp12-compression.txt"), report);
        Console.WriteLine(string.Join(Environment.NewLine, report));
    }

    [Theory]
    [InlineData(200, 160, 12288, 57, 0)]
    [InlineData(0, 160, 100, 2, 2)]
    [InlineData(65536, 160, 100, 2, 2)]
    [InlineData(10, 159, 100, 2, 2)]
    [InlineData(10, 160, 0, 1, 2)]
    [InlineData(10, 160, 12289, 57, 2)]
    [InlineData(10, 160, 221, 2, 2)]
    [InlineData(10, 160, 221, 3, 0)]
    public void Loop_header_validator_checks_frames_leds_length_and_packet_count(int frames, int leds, int length, int packets, int expect)
    {
        var rules = new LoopHeaderRules(160, LianLiWire.MaxCompressedAnimation, LianLiWire.UploadChunk);
        var verdict = FrameValidator.ValidateLoopHeader(new LoopHeader(frames, leds, length, packets), rules, new DeviceState());
        verdict.Should().Be(expect == 0 ? FrameRejectReason.None : FrameRejectReason.LoopHeaderInvalid);
    }

    [Fact]
    public void Loop_header_validator_rejects_offline_devices_and_over_255_leds()
    {
        var state = new DeviceState(online: false);
        var ok = new LoopHeader(1, 160, 480, 4);
        FrameValidator.ValidateLoopHeader(ok, new LoopHeaderRules(160, 12288, 220), state).Should().Be(FrameRejectReason.DeviceOffline);
        FrameValidator.ValidateLoopHeader(new LoopHeader(1, 256, 480, 4), new LoopHeaderRules(256, 12288, 220), new DeviceState())
            .Should().Be(FrameRejectReason.LoopHeaderInvalid);
        var act = () => FrameValidator.EnsureLoopHeader(new LoopHeader(1, 1, 480, 9), new LoopHeaderRules(160, 12288, 220), new DeviceState());
        act.Should().Throw<FrameRejectedException>().Which.Reason.Should().Be(FrameRejectReason.LoopHeaderInvalid);
    }

    [Fact]
    public void Save_config_packet_is_a_broadcast_with_the_master_mac()
    {
        var p = RfPackets.SaveConfig(Rf.Master);
        p.Should().HaveCount(LianLiWire.RfPacketLength);
        (p[0], p[1]).Should().Be((OpcodeLayout.RfPacketTag, (byte)21));
        p[2..8].Should().OnlyContain(b => b == 0xFF);
        RfMac.Read(p.AsSpan(8)).Should().Be(Rf.Master);
        (p[14], p[15], p[16]).Should().Be(((byte)0xFF, (byte)0, (byte)0));
        var control = () => RfPackets.Control(RfCmd.SaveConfig, RfMac.Parse("AA-BB-CC-DD-EE-01"), Rf.Master, new RfAddress(8, 1), 1, 1, 0);
        control.Should().Throw<ArgumentException>("RF 21 is built only by SaveConfig");
    }

    [Fact]
    public async Task Save_config_is_gated_on_verification_a_completed_upload_and_sixty_seconds()
    {
        var now = Rf.Now;
        var usb = new FakeUsb();
        var wire = new RecordingTransport();
        wire.EnqueueRead(Rf.MasterReply());
        var options = new LianLiOptions { WireFormatVerified = true, ReceiverInterfaceGuid = FakeUsb.Guid };
        options.Devices.Add(new LianLiDeviceConfig { Mac = "AA-BB-CC-DD-EE-01", Family = "SLv3", Leds = 160, Channel = 8, Rx = 1 });
        await using var a = new LianLiAdapter(options, usb, _ => wire, usb, NullLogger.Instance, new IdentityEncoder(), _ => { }, () => now);
        var d = (await a.DiscoverAsync(CancellationToken.None)).Single();
        await a.OpenAsync(d.Id, CancellationToken.None);
        List<byte[]> Saves() => Rf.Group(wire.SentFrames).Where(g => g.Packet?[1] == 21).Select(g => g.Packet!).ToList();

        var early = () => a.SaveConfigAsync("owner", CancellationToken.None);
        await early.Should().ThrowAsync<InvalidOperationException>().WithMessage("*completed loop upload*");
        Saves().Should().BeEmpty("nothing is ever sent automatically");

        await a.UploadLoopAsync(d.Id, LianLiAdapter.ChainZone, Loop(2), CancellationToken.None);
        Saves().Should().BeEmpty("an upload never saves");
        (await a.SaveConfigAsync("owner", CancellationToken.None)).Should().Be(1);
        var saves = Saves();
        saves.Should().ContainSingle().Which.Should().Equal(RfPackets.SaveConfig(Rf.Master));
        Rf.Group(wire.SentFrames).Single(g => g.Packet?[1] == 21).Frames.Should().OnlyContain(f => f[2] == 8 && f[3] == 0xFF);

        now = now.AddSeconds(59);
        var again = () => a.SaveConfigAsync("owner", CancellationToken.None);
        await again.Should().ThrowAsync<InvalidOperationException>().WithMessage("*60 s*");
        now = now.AddSeconds(1);
        (await a.SaveConfigAsync("owner", CancellationToken.None)).Should().Be(1);
        Saves().Should().HaveCount(2);

        var seq = new RelaySequence();
        foreach (var f in wire.SentFrames)
        {
            seq.Check(f, OpcodeLayout.LianLi);
        }
    }

    [Fact]
    public async Task Relay_packets_are_budgeted_at_100_per_second_after_a_256_burst()
    {
        var sleeps = new List<TimeSpan>();
        var usb = new FakeUsb();
        var wire = new RecordingTransport();
        wire.EnqueueRead(Rf.MasterReply());
        var options = new LianLiOptions { WireFormatVerified = true, ReceiverInterfaceGuid = FakeUsb.Guid };
        options.Devices.Add(new LianLiDeviceConfig { Mac = "AA-BB-CC-DD-EE-01", Family = "SLv3", Leds = 160, Channel = 8, Rx = 1 });
        await using var a = new LianLiAdapter(options, usb, _ => wire, usb, NullLogger.Instance, new IdentityEncoder(), t => { sleeps.Add(t); Thread.Sleep(t); }, () => Rf.Now);
        var d = (await a.DiscoverAsync(CancellationToken.None)).Single();
        await a.OpenAsync(d.Id, CancellationToken.None);
        (RateLimits.RfRelayPacketsPerSecond, RateLimits.RfRelayPacketBurst).Should().Be((100.0, 256.0));

        // 25 frames × 480 B = 12000 B identity → 55 data packets + 4 header sends = 59 relays per upload; 8 uploads = 472, more than the burst plus the refill during the 20 ms header gaps.
        for (var i = 0; i < 8; i++)
        {
            await a.UploadLoopAsync(d.Id, LianLiAdapter.ChainZone, new LoopFrames(25, 160, 20, (uint)i, new byte[25 * 480]), CancellationToken.None);
        }

        sleeps.Where(t => t < TimeSpan.FromMilliseconds(20)).Should().NotBeEmpty("the packet budget throttled the relay once the burst was spent");
    }
}
