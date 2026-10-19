using System.Buffers.Binary;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using UnifiedRgb.Devices.LianLi;
using UnifiedRgb.Devices.LianLi.Codec;
using UnifiedRgb.Devices.LianLi.Protocol;
using UnifiedRgb.Devices.LianLi.Protocol.Families;
using UnifiedRgb.Transports.Safety;

namespace UnifiedRgb.Devices.Tests;

/// <summary>Records what the adapter hands the codec (wire-ordered rgb_data) and passes it through.</summary>
internal sealed class CapturingEncoder : IRgbAnimationEncoder
{
    public List<byte[]> Inputs { get; } = [];

    public byte[] Encode(ReadOnlySpan<byte> rgbData)
    {
        Inputs.Add(rgbData.ToArray());
        return rgbData.ToArray();
    }
}

/// <summary>
/// Synthetic three-chain/H2 discovery cases plus optional private capture provenance checks.
/// </summary>
public class LianLiOwnerTests
{
    private static readonly string ConfigDir = Path.Combine(Fixture.RepoRoot(), "analysis", "lconnect-config");

    private static readonly TlPacketAssembler Tl = new();

    private static JsonElement[] SavedDevices()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(ConfigDir, "savedDevices.config")));
        return doc.RootElement.GetProperty("$values").EnumerateArray().Select(e => e.Clone()).ToArray();
    }

    private static int[] Ints(JsonElement e, string name) => (e.GetProperty(name) is { ValueKind: JsonValueKind.Object } o ? o.GetProperty("$values") : e.GetProperty(name)).EnumerateArray().Select(x => x.GetInt32()).ToArray();

    private static LianLiDeviceConfig[] ExampleDevices() =>
    [
        new() { Mac = "02:00:00:00:00:01", Name = "TL 140 x3 (example)", RecType = 28, FanNum = 3, Leds = 78, Channel = 8, Rx = 2 },
        new() { Mac = "02:00:00:00:00:02", Name = "TL 120 x3 (example)", RecType = 28, FanNum = 3, Leds = 78, Channel = 8, Rx = 3 },
        new() { Mac = "02:00:00:00:00:03", Name = "H2 waterblock (example)", RecType = 10, DevType = 10, FanNum = 0, Leds = 24, Channel = 8, Rx = 6 },
        new() { Mac = "02:00:00:00:00:04", Name = "TL LCD x3 (example)", RecType = 28, FanNum = 3, Leds = 78, Channel = 8, Rx = 5 },
    ];

    [Fact]
    public void Tl_chain_is_a_two_row_matrix_reordered_fan_major_top_edge_first()
    {
        Tl.ChainLayout(78).Should().Be(ZoneLayout.Matrix(39, 2));
        Tl.Zones(78).Should().Equal(ZoneLayout.Matrix(39, 2));
        Tl.ReordersWire.Should().BeTrue();

        // layout index = row·39 + column; wire = fan·26 + row·13 + position (TLMode*: Up scope → [fan·26, +13), Down → [+13, +26))
        Tl.WireIndex(0, 78).Should().Be(0);
        Tl.WireIndex(12, 78).Should().Be(12);
        Tl.WireIndex(13, 78).Should().Be(26, "column 13 is the first LED of fan 2's top edge");
        Tl.WireIndex(38, 78).Should().Be(64);
        Tl.WireIndex(39, 78).Should().Be(13, "row 1 (bottom edge) of fan 1 follows its 13 top LEDs");
        Tl.WireIndex(39 + 13, 78).Should().Be(39);
        Tl.WireIndex(77, 78).Should().Be(77);
        Enumerable.Range(0, 78).Select(i => Tl.WireIndex(i, 78)).Should().BeEquivalentTo(Enumerable.Range(0, 78), "a permutation");
        for (var col = 0; col < 39; col++)
        {
            Tl.WireIndex(39 + col, 78).Should().Be(Tl.WireIndex(col, 78) + 13, "IL UpsideDown swaps the 13-LED halves of each fan");
        }

        var leds = Enumerable.Range(0, 78).Select(i => new Rgb((byte)i, (byte)(i + 100), 7)).ToArray();
        var payload = new byte[78 * 3];
        Tl.Assemble(leds, payload).Should().Be(234);
        var rgb = leds.SelectMany(c => new[] { c.R, c.G, c.B }).ToArray();
        Tl.ToWireOrder(rgb.Concat(rgb).ToArray(), 78).ToArray().Should().Equal(payload.Concat(payload));
        payload[3 * 26].Should().Be(13);

        FamilyDetector.IsDrivable(Tl, 78).Should().BeTrue();
        FamilyDetector.IsDrivable(Tl, 26).Should().BeTrue();
        FamilyDetector.IsDrivable(Tl, 104).Should().BeTrue();
        FamilyDetector.IsDrivable(Tl, 77).Should().BeFalse("whole 26-LED fans only");
        FamilyDetector.IsDrivable(Tl, 130).Should().BeFalse("the IL clamps TL modes to 4 fans");
        var h2 = FamilyDetector.ByFamily("H2")!;
        FamilyDetector.IsDrivable(h2, 24).Should().BeTrue();
        FamilyDetector.IsDrivable(h2, 48).Should().BeFalse("block + CL fan (8 centre + 16 outer) is not modelled");
        h2.ChainLayout(24).Should().Be(ZoneLayout.Ring(24));
        h2.ReordersWire.Should().BeFalse();
    }

    [LocalCaptureFact]
    public void Il_led_rules_reproduce_every_saved_device()
    {
        var saved = SavedDevices();
        saved.Should().HaveCount(4);
        foreach (var d in saved)
        {
            var mac = d.GetProperty("MacStr").GetString()!;
            var rec = Ints(d, "RecType");
            var devType = (byte)d.GetProperty("dev_type").GetInt32();
            var fans = d.GetProperty("FanNum").GetInt32();
            RfLedCounts.LedNum(devType, (byte)rec[0], fans).Should().Be(d.GetProperty("LedNum").GetInt32(), mac);
            RfLedCounts.UiDot((byte)rec[0], fans).Should().Be(d.GetProperty("UIDot").GetInt32(), mac);
            var fanTypes = Convert.FromBase64String(d.GetProperty("fans_type").GetString()!);
            for (var k = 0; k < fans; k++)
            {
                RfDeviceRecord.RecTypeOfFan(fanTypes[k]).Should().Be((byte)rec[k], $"{mac} fan {k} type {fanTypes[k]}");
            }

            RfMac.Read(Convert.FromBase64String(d.GetProperty("mac_addr").GetString()!)).Should().Be(RfMac.Parse(mac), "mac_addr is MacStr in wire order");
            // Keep the exact captured identity/byte-order assertion without publishing the local master's address.
            var canonicalMaster = RfMac.Read(Convert.FromBase64String(d.GetProperty("master_mac_addr").GetString()!)).ToString();
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(canonicalMaster)))
                .Should().Be("687B2AB8F55E5502F5388343CCCE6C498134B8542902A9ABB0B1F3853BD552E6");
            (d.GetProperty("channel").GetInt32(), d.GetProperty("BindStatus").GetInt32()).Should().Be((8, 1));
        }

        RfLedCounts.LedNum(0, 28, 3).Should().Be(78);
        RfLedCounts.UiDot(28, 3).Should().Be(39);
        RfLedCounts.LedNum(10, 10, 0).Should().Be(24);
        RfLedCounts.LedNum(10, 10, 2).Should().Be(72);
        RfLedCounts.LedNum(0, 20, 4).Should().Be(160, "SLv3 unchanged");
    }

    [Fact]
    public void Example_topology_passes_discovery_checks_without_using_a_machine_configuration()
    {
        var devices = ExampleDevices();
        devices.Should().HaveCount(4);
        devices.Select(d => LianLiDiscovery.FamilyOf(d)?.Family).Should().Equal("TL", "TL", "H2", "TL");
        devices.Should().OnlyContain(d => LianLiDiscovery.MatchesIlLedNum(d) && FamilyDetector.IsDrivable(LianLiDiscovery.FamilyOf(d)!, d.Leds));
        devices.Should().OnlyContain(d => d.Channel == 8 && d.Sender == 0);
        devices.Select(d => d.Name).Should().OnlyContain(n => n!.StartsWith(LianLiDiscovery.FamilyOf(devices.First(x => x.Name == n))!.Family + " ", StringComparison.Ordinal),
            "the GUI picks the family mode list from the name prefix");

        devices.Select(d => d.Mac).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Source_defaults_contain_no_local_RF_addresses_and_leave_hardware_output_disabled()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Fixture.RepoRoot(), "src", "UnifiedRgb.Host", "appsettings.json")));
        var options = doc.RootElement.GetProperty("UnifiedRgb");
        options.GetProperty("UseVirtualAdapter").GetBoolean().Should().BeTrue();
        foreach (var gate in new[] { "EnableLianLi", "EnableMsiGpu", "EnableAsusAura", "EnableEneDram", "EnableOpenRgb" })
        {
            options.GetProperty(gate).GetBoolean().Should().BeFalse();
        }

        var lianLi = options.GetProperty("LianLi");
        lianLi.GetProperty("Devices").EnumerateArray().Should().BeEmpty();
        lianLi.GetProperty("WireFormatVerified").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public void Discovery_resolves_families_by_rec_type_and_skips_counts_the_il_would_not_compute()
    {
        var o = new LianLiOptions { WireFormatVerified = false, ReceiverInterfaceGuid = FakeUsb.Guid };
        foreach (var d in ExampleDevices())
        {
            o.Devices.Add(d);
        }

        o.Devices.Add(new LianLiDeviceConfig { Mac = "aa:bb:cc:dd:ee:01", RecType = 28, FanNum = 3, Leds = 80, Channel = 8, Rx = 7 });
        o.Devices.Add(new LianLiDeviceConfig { Mac = "aa:bb:cc:dd:ee:02", Family = "SLv3", RecType = 28, FanNum = 3, Leds = 78, Channel = 8, Rx = 8 });
        o.Devices.Add(new LianLiDeviceConfig { Mac = "aa:bb:cc:dd:ee:03", RecType = 10, DevType = 10, FanNum = 1, Leds = 48, Channel = 8, Rx = 9 });

        var found = LianLiDiscovery.Discover(o, new FakeUsb(), NullLogger.Instance);
        found.Select(d => d.Assembler.Family).Should().Equal("TL", "TL", "H2", "TL");
        found.Select(d => (d.Address.Channel, d.Address.Rx)).Should().Equal(((byte)8, (byte)2), ((byte)8, (byte)3), ((byte)8, (byte)6), ((byte)8, (byte)5));

        var tl = LianLiAdapter.Describe(found[0]);
        tl.Name.Should().Be("TL 140 x3 (example)");
        var zone = tl.Zones.Should().ContainSingle().Subject;
        (zone.Name, zone.Layout).Should().Be(("Chain (3 fans)", ZoneLayout.Matrix(39, 2)));
        var block = LianLiAdapter.Describe(found[2]).Zones.Single();
        (block.Name, block.Layout).Should().Be(("H2", ZoneLayout.Ring(24)));
    }

    [Fact]
    public async Task A_tl_upload_hands_the_codec_wire_ordered_frames()
    {
        var o = new LianLiOptions { WireFormatVerified = true, ReceiverInterfaceGuid = FakeUsb.Guid };
        o.Devices.Add(new LianLiDeviceConfig { Mac = "02:00:00:00:00:01", RecType = 28, FanNum = 3, Leds = 78, Channel = 8, Rx = 2 });
        var usb = new FakeUsb();
        var wire = new RecordingTransport();
        wire.EnqueueRead(Rf.MasterReply());
        var enc = new CapturingEncoder();
        await using var a = new LianLiAdapter(o, usb, _ => wire, usb, NullLogger.Instance, enc, _ => { }, () => Rf.Now);
        var id = (await a.DiscoverAsync(CancellationToken.None)).Single().Id;
        await a.OpenAsync(id, CancellationToken.None);

        var rgb = new byte[2 * 78 * 3];
        for (var k = 0; k < 2; k++)
        {
            for (var i = 0; i < 78; i++)
            {
                (rgb[((k * 78) + i) * 3], rgb[(((k * 78) + i) * 3) + 1]) = ((byte)i, (byte)k);
            }
        }

        await a.UploadLoopAsync(id, LianLiAdapter.ChainZone, new LoopFrames(2, 78, 20, 43_200_000, rgb), CancellationToken.None);
        var sent = enc.Inputs.Should().ContainSingle().Subject;
        for (var k = 0; k < 2; k++)
        {
            for (var i = 0; i < 78; i++)
            {
                var o3 = ((k * 78) + Tl.WireIndex(i, 78)) * 3;
                (sent[o3], sent[o3 + 1]).Should().Be(((byte)i, (byte)k));
            }
        }

        a.LastUpload(id)!.Frames.Should().Be(2);
    }

    /// <summary>
    /// The strongest pre-live codec evidence available on this machine: L-Connect's own last uploads (slv3/config/*.effect,
    /// copied to analysis/lconnect-config) decode with yuz.dll and re-encode through our <see cref="TinyuzEncoder"/> and
    /// <see cref="LianLiAdapter.Compress"/> to the identical bytes.
    /// </summary>
    [LocalCaptureCodecFact]
    public void L_connect_last_uploads_round_trip_byte_for_byte_through_our_codec()
    {
        if (!File.Exists(YuzCodecTests.DllPath))
        {
            Console.WriteLine($"SKIPPED: {YuzCodecTests.DllPath} not present");
            return;
        }

        using var enc = TinyuzEncoder.Open(YuzCodecTests.DllPath);
        var saved = SavedDevices().ToDictionary(d => d.GetProperty("MacStr").GetString()!.Replace(":", string.Empty, StringComparison.Ordinal), StringComparer.OrdinalIgnoreCase);
        var files = Directory.GetFiles(ConfigDir, "*.effect").OrderBy(f => f, StringComparer.Ordinal).ToArray();
        files.Should().HaveCount(4);
        foreach (var f in files)
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(f));
            var e = doc.RootElement;
            var data = Convert.FromBase64String(e.GetProperty("data").GetString()!);
            var (frames, leds, interval) = (e.GetProperty("total_frame").GetInt32(), e.GetProperty("led_num").GetInt32(), e.GetProperty("interval").GetInt32());
            leds.Should().Be(saved[Path.GetFileNameWithoutExtension(f)].GetProperty("LedNum").GetInt32(), "led_num is the device's LedNum");

            var raw = enc.Decode(data, frames * leds * 3);
            enc.Encode(raw).Should().Equal(data, $"{Path.GetFileName(f)}: TuzEnc(TuzDec(x)) == x");
            var payload = LianLiAdapter.Compress(enc, new LoopFrames(frames, leds, interval, 0, raw), LianLiWire.MaxCompressedAnimation);
            payload.Compressed.Should().Equal(data, "our compress path emits L-Connect's bytes");
            (payload.Animation.TotalFrames, payload.Animation.LedCount, payload.Decimations).Should().Be((frames, leds, 0));

            // what L-Connect had uploaded: one all-black frame (UIEffect 1 / TLEffect 29 with no user colours, slv3/cache/*.set)
            (frames, interval).Should().Be((1, 60));
            raw.Should().OnlyContain(b => b == 0);
            var index = BinaryPrimitives.ReadUInt32BigEndian(Convert.FromBase64String(e.GetProperty("effect_index").GetString()!));
            index.Should().BeLessThan(86_400_000u, "GetTimeStamp is milliseconds since midnight");
        }

        // a real TL-shaped animation (39-frame rainbow over 3 fans, wire-ordered) round trips and fits one upload
        var rgb = new byte[39 * 78 * 3];
        for (var k = 0; k < 39; k++)
        {
            for (var col = 0; col < 39; col++)
            {
                var c = Core.Color.Hsv.ToRgb(((col + k) % 39) / 39f, 1f, 1f);
                foreach (var row in new[] { 0, 1 })
                {
                    var o = ((k * 78) + (row * 39) + col) * 3;
                    (rgb[o], rgb[o + 1], rgb[o + 2]) = (c.R, c.G, c.B);
                }
            }
        }

        var wire = Tl.ToWireOrder(rgb, 78).ToArray();
        var packed = enc.Encode(wire);
        packed.Length.Should().BeLessThan(LianLiWire.MaxCompressedAnimation);
        enc.Decode(packed, wire.Length).Should().Equal(wire);
    }
}
