using System.Buffers.Binary;
using UnifiedRgb.Devices.OpenRgbSdk;
using UnifiedRgb.Devices.Safety;
using UnifiedRgb.Transports.OpenRgb;

namespace UnifiedRgb.Devices.Tests;

/// <summary>
/// Binary fixtures in tests/Fixtures/openrgb/&lt;name&gt;.bin. Controller blobs come from the test-side spec encoder
/// (<see cref="OrgbSpec"/>); packet fixtures pin the product encoder's bytes. Regenerate with URGB_UPDATE_FIXTURES=1.
/// </summary>
internal static class BinFixture
{
    public static byte[] Load(string name, Func<byte[]> generate)
    {
        var path = Path.Combine(Fixture.RepoRoot(), "tests", "Fixtures", "openrgb", name + ".bin");
        if (Environment.GetEnvironmentVariable("URGB_UPDATE_FIXTURES") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, generate());
        }

        File.Exists(path).Should().BeTrue($"fixture {path} must exist");
        return File.ReadAllBytes(path);
    }
}

public class OpenRgbProtocolTests
{
    [Theory]
    [InlineData(3u)]
    [InlineData(4u)]
    public void Ene_dram_blob_parses(uint protocol)
    {
        var blob = BinFixture.Load($"ene_dram_v{protocol}", () => OrgbSpec.Controller(OrgbSpec.EneDram("I2C: PIIX4, address 0x70"), protocol));
        var d = OpenRgbProtocol.ParseControllerData(blob, protocol);
        d.Type.Should().Be(1);
        d.Name.Should().Be("ENE DRAM");
        d.Vendor.Should().Be("ENE");
        d.Location.Should().Be("I2C: PIIX4, address 0x70");
        d.Modes.Should().HaveCount(10);
        d.Modes[0].Name.Should().Be("Direct");
        d.Modes[2].Colors.Should().Equal(0x0000FFu);
        d.Zones.Should().ContainSingle().Which.LedsCount.Should().Be(8u);
        d.Leds.Should().HaveCount(8);
        d.Colors.Should().HaveCount(8);
    }

    [Fact]
    public void Asus_mainboard_blob_parses_with_four_zones()
    {
        var blob = BinFixture.Load("asus_mb_v4", () => OrgbSpec.Controller(OrgbSpec.AsusMb(), 4));
        var d = OpenRgbProtocol.ParseControllerData(blob, 4);
        d.Type.Should().Be(0);
        d.Vendor.Should().Be("ASUS");
        d.Zones.Select(z => (z.Name, z.LedsCount)).Should().Equal(
            ("Aura Mainboard", 2u), ("Aura Addressable 1", 20u), ("Aura Addressable 2", 20u), ("Aura Addressable 3", 20u));
        d.Leds.Should().HaveCount(62);
    }

    [Fact]
    public void Matrix_zone_parses_map()
    {
        var c = new SpecController(4, "Panel", "X", "loc", ["Direct"], [new("M", 2, 6, 3, 2)]);
        var d = OpenRgbProtocol.ParseControllerData(OrgbSpec.Controller(c, 4), 4);
        var z = d.Zones.Single();
        (z.MatrixWidth, z.MatrixHeight).Should().Be((3u, 2u));
        z.MatrixMap.Should().Equal(0u, 1u, 2u, 3u, 4u, 5u);
        OpenRgbSdkAdapter.MapZone(z).Should().Be(ZoneLayout.Matrix(3, 2));
    }

    [Fact]
    public void Truncated_or_oversized_blob_is_rejected()
    {
        var blob = OrgbSpec.Controller(OrgbSpec.EneDram("x"), 4);
        var truncated = () => OpenRgbProtocol.ParseControllerData(blob.AsSpan(0, blob.Length - 5), 4);
        truncated.Should().Throw<InvalidDataException>();
        var big = (byte[])blob.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(big, (uint)big.Length + 100);
        var oversized = () => OpenRgbProtocol.ParseControllerData(big, 4);
        oversized.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void Update_zone_leds_matches_fixture_and_span_encoder()
    {
        var colors = new[] { new Rgb(255, 0, 0), new Rgb(0, 255, 0), new Rgb(0, 0, 255) };
        var triplets = colors.SelectMany(c => new[] { c.R, c.G, c.B }).ToArray();
        var packet = OpenRgbProtocol.UpdateZoneLeds(3, 1, triplets);
        packet.Should().Equal(BinFixture.Load("update_zone_leds", () => packet));
        Convert.ToHexString(packet).Should().Be(
            "4F524742" + "03000000" + "1B040000" + "16000000" + "16000000" + "01000000" + "0300" + "FF000000" + "00FF0000" + "0000FF00");
        var buf = new byte[OpenRgbProtocol.UpdateZoneLedsLength(3)];
        OpenRgbProtocol.WriteUpdateZoneLeds(buf, 3, 1, colors).Should().Be(packet.Length);
        buf.Should().Equal(packet);
    }

    [Fact]
    public void Update_mode_round_trips_the_direct_mode_block()
    {
        var d = OpenRgbProtocol.ParseControllerData(OrgbSpec.Controller(OrgbSpec.EneDram("x"), 4), 4);
        var packet = OpenRgbProtocol.UpdateMode(0, 0, d.Modes[0], 4);
        packet.Should().Equal(BinFixture.Load("update_mode_direct", () => packet));
        BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(8)).Should().Be(OpenRgbPacketIds.UpdateMode);
        BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(16)).Should().Be((uint)(packet.Length - 16));
        BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(20)).Should().Be(0);
    }

    [Fact]
    public void Handshake_negotiates_and_names_the_client()
    {
        var name = OpenRgbProtocol.SetClientName("UnifiedRgb");
        name.Should().Equal(BinFixture.Load("set_client_name", () => name));
        var version = OpenRgbProtocol.RequestProtocolVersion();
        version.Should().Equal(BinFixture.Load("request_protocol_version", () => version));
    }

    [Fact]
    public async Task Client_rejects_old_server_and_skips_notifications()
    {
        using var old = new OpenRgbSdkClient(new FakeOpenRgbServer(2));
        var act = () => old.HandshakeAsync("t", CancellationToken.None);
        await act.Should().ThrowAsync<NotSupportedException>();

        var server = new FakeOpenRgbServer(5, OrgbSpec.EneDram("a"));
        using var c = new OpenRgbSdkClient(server);
        await c.HandshakeAsync("UnifiedRgb", CancellationToken.None);
        c.Protocol.Should().Be(4u);
        server.ClientName.Should().Be("UnifiedRgb");
        server.Notify();
        (await c.GetControllerDataAsync(0, CancellationToken.None)).Name.Should().Be("ENE DRAM");
        c.DeviceListChanged.Should().BeTrue();
    }
}

public class OpenRgbSdkAdapterTests
{
    private static readonly OpenRgbSdkOptions Manual = new() { RediscoverInterval = TimeSpan.Zero, ConnectTimeout = TimeSpan.Zero };

    private static SpecController[] Rig() =>
        [OrgbSpec.EneDram("I2C: PIIX4, address 0x70"), OrgbSpec.EneDram("I2C: PIIX4, address 0x71"), OrgbSpec.MsiGpu(), OrgbSpec.AsusMb(), OrgbSpec.LianLi()];

    private sealed class Rigged
    {
        public Rigged(params SpecController[] controllers)
        {
            Server = new FakeOpenRgbServer(4, controllers);
            Adapter = new OpenRgbSdkAdapter(_ => Task.FromResult<ITransport>(Server), Manual);
        }

        public FakeOpenRgbServer Server { get; set; }

        public OpenRgbSdkAdapter Adapter { get; }
    }

    [Fact]
    public async Task Only_asus_mainboard_and_ene_dram_are_exposed()
    {
        var r = new Rigged(Rig());
        var found = await r.Adapter.DiscoverAsync(CancellationToken.None);
        found.Select(d => d.Name).Should().Equal("ENE DRAM", "ENE DRAM", "ASUS ROG STRIX B850-F GAMING WIFI");
        found.Should().OnlyContain(d => d.Class == DeviceClass.OpenRgb && d.Capabilities.MaxFps == 60);
        found.Select(d => d.Id).Should().OnlyHaveUniqueItems();
        found[0].Id.Value.Should().Be("openrgb:ene-dram@i2c--piix4--address-0x70");
        found[0].Zones.Single().Layout.Should().Be(ZoneLayout.Linear(8));
        found[2].DeviceType.Should().Be(DeviceType.Motherboard);
        found[2].Zones.Select(z => z.LedCount).Should().Equal(2, 20, 20, 20);
        r.Server.ClientName.Should().Be("UnifiedRgb");
    }

    [Fact]
    public async Task Open_forces_direct_and_frames_become_update_zone_leds()
    {
        var r = new Rigged(Rig());
        var mb = (await r.Adapter.DiscoverAsync(CancellationToken.None))[2];
        await r.Adapter.OpenAsync(mb.Id, CancellationToken.None);
        var mode = r.Server.Sent(OpenRgbPacketIds.UpdateMode).Single();
        mode.Dev.Should().Be(3u, "the MB is controller index 3 on the server");
        BinaryPrimitives.ReadInt32LittleEndian(mode.Payload.AsSpan(4)).Should().Be(0, "Direct is mode 0");

        var colors = Enumerable.Range(0, 20).Select(i => new Rgb((byte)i, 0, 0)).ToArray();
        r.Adapter.WriteFrame(mb.Id, new ZoneId(2), colors);
        var w = r.Server.Sent(OpenRgbPacketIds.UpdateZoneLeds).Single();
        w.Dev.Should().Be(3u);
        BinaryPrimitives.ReadUInt32LittleEndian(w.Payload.AsSpan(4)).Should().Be(2u);
        BinaryPrimitives.ReadUInt16LittleEndian(w.Payload.AsSpan(8)).Should().Be(20);
        w.Payload[10 + (4 * 7)].Should().Be(7);

        var wrong = () => r.Adapter.WriteFrame(mb.Id, new ZoneId(0), colors);
        wrong.Should().Throw<ArgumentException>();
        r.Server.Received.Select(p => p.Id).Should().OnlyContain(id => OpenRgbPacketIds.Sendable.Contains(id));
    }

    [Fact]
    public async Task Controller_without_direct_mode_is_not_opened()
    {
        var noDirect = OrgbSpec.EneDram("x") with { Modes = ["Static", "Off"] };
        var r = new Rigged(noDirect);
        var d = (await r.Adapter.DiscoverAsync(CancellationToken.None)).Single();
        var open = () => r.Adapter.OpenAsync(d.Id, CancellationToken.None);
        await open.Should().ThrowAsync<NotSupportedException>();
        r.Server.Sent(OpenRgbPacketIds.UpdateMode).Should().BeEmpty();
    }

    [Fact]
    public async Task Lost_connection_reconnects_reasserts_direct_and_keeps_ids()
    {
        var r = new Rigged(Rig());
        var first = r.Server;
        var connects = 0;
        var adapter = new OpenRgbSdkAdapter(_ =>
        {
            connects++;
            return Task.FromResult<ITransport>(r.Server);
        }, Manual);
        var dram = (await adapter.DiscoverAsync(CancellationToken.None))[1];
        await adapter.OpenAsync(dram.Id, CancellationToken.None);

        first.Dead = true;
        var write = () => adapter.WriteFrame(dram.Id, new ZoneId(0), new Rgb[8]);
        write.Should().Throw<IOException>();

        // OpenRGB restarted and enumerated in a different order.
        r.Server = new FakeOpenRgbServer(4, OrgbSpec.AsusMb(), OrgbSpec.EneDram("I2C: PIIX4, address 0x71"), OrgbSpec.EneDram("I2C: PIIX4, address 0x70"));
        await adapter.RediscoverAsync(CancellationToken.None);
        connects.Should().Be(2);
        first.Disposed.Should().BeTrue();
        r.Server.Sent(OpenRgbPacketIds.UpdateMode).Single().Dev.Should().Be(1u, "Direct is re-asserted at the new index");

        adapter.WriteFrame(dram.Id, new ZoneId(0), Enumerable.Repeat(Rgb.White, 8).ToArray());
        r.Server.Sent(OpenRgbPacketIds.UpdateZoneLeds).Single().Dev.Should().Be(1u);
    }

    [Fact]
    public async Task Device_list_change_raises_hotplug_and_marks_offline()
    {
        var r = new Rigged(OrgbSpec.EneDram("a"), OrgbSpec.EneDram("b"));
        var events = new List<HotplugEvent>();
        r.Adapter.Hotplug += events.Add;
        var found = await r.Adapter.DiscoverAsync(CancellationToken.None);
        await r.Adapter.OpenAsync(found[1].Id, CancellationToken.None);

        await r.Adapter.RediscoverAsync(CancellationToken.None);
        events.Should().BeEmpty("nothing changed");

        r.Server.Controllers.RemoveAt(1);
        r.Server.Notify();
        await r.Adapter.RediscoverAsync(CancellationToken.None);
        events.Should().Equal(new HotplugEvent(found[1].Id, HotplugKind.Removed));
        var write = () => r.Adapter.WriteFrame(found[1].Id, new ZoneId(0), new Rgb[8]);
        write.Should().Throw<FrameRejectedException>().Which.Reason.Should().Be(FrameRejectReason.DeviceOffline);

        r.Server.Controllers.Add(OrgbSpec.EneDram("b"));
        await r.Adapter.RediscoverAsync(CancellationToken.None);
        events.Should().HaveCount(2);
        events[1].Should().Be(new HotplugEvent(found[1].Id, HotplugKind.Arrived));
        r.Adapter.WriteFrame(found[1].Id, new ZoneId(0), new Rgb[8]);
    }

    [Theory]
    [InlineData(0, "ASUS", "ASUS ROG STRIX B850-F GAMING WIFI", true)]
    [InlineData(1, "ENE", "ENE DRAM", true)]
    [InlineData(0, "Gigabyte", "B850 AORUS", false)]
    [InlineData(1, "Corsair", "Vengeance RGB", false)]
    [InlineData(2, "MSI", "MSI GeForce RTX 5090 SUPRIM", false)]
    [InlineData(2, "ASUS", "ASUS TUF RTX", false)]
    [InlineData(4, "Lian Li", "Lian Li Uni Hub - SL V2", false)]
    [InlineData(3, "ENE", "ENE cooler", false)]
    public void Detector_allow_list(int type, string vendor, string name, bool allowed)
    {
        var d = OpenRgbProtocol.ParseControllerData(
            OrgbSpec.Controller(new SpecController(type, name, vendor, "loc", ["Direct"], [new("Z", 1, 1)]), 4), 4);
        DetectorAllowList.IsAllowed(d).Should().Be(allowed);
    }
}
