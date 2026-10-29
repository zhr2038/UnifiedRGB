using UnifiedRgb.Devices.AsusRog;
using UnifiedRgb.Transports.Hid;

namespace UnifiedRgb.Devices.Tests;

public sealed class ConfiguredFirmwareRegressionTests
{
    // Synthetic wired .25 fixture only; this does not assert a physical wired .25 query.
    private const string SyntheticWired25 = "001200000025000100";

    [Fact]
    public void Explicitly_configured_wired_identity_can_match_its_exact_nonlegacy_header()
    {
        using var io = new NoIo();
        using var connection = new RogWiredOledConnection(io, () => true, () => new string('A', 64), () => SyntheticWired25);
        var identity = new byte[65]; Convert.FromHexString(SyntheticWired25).CopyTo(identity, 0);
        ((Action)(() => connection.RequireFirmwareIdentity(identity, SyntheticWired25))).Should().NotThrow();
        io.Commands.Should().Be(0);
    }

    [Fact]
    public void Receiver_reply_without_keyboard_firmware_is_unavailable_rather_than_a_version_mismatch()
    {
        using var io = new NoIo();
        using var connection = new RogWirelessOledConnection(new(new string('A', 64), 0x1ACE, new Dictionary<byte, HidCollectionInfo>()), io, "0800070025000100", () => true);
        var identity = new byte[65]; identity[1] = 0x12; identity[5] = 8; identity[7] = 7;
        ((Action)(() => connection.RequireFirmwareIdentity(identity, "0800070025000100"))).Should().Throw<InvalidDataException>()
            .WithMessage("*identity*unavailable*");
        io.Commands.Should().Be(0);
    }

    [Fact]
    public void Concrete_factory_passes_the_exact_configured_header_without_replacing_legacy_one_argument_metadata()
    {
        var devices = new FakeDevices(); var resolver = new Resolver(); var opens = 0;
        var factory = new RogWiredOledFactory(devices, resolver, c => { c.Should().Be(devices.Collection); opens++; return new NoIo(); });
        var key = RogWiredOledProtocol.DeviceKey(devices.Collection);
        using (var configured = factory.TryOpen(key, SyntheticWired25))
        {
            configured.Should().NotBeNull(); configured!.FirmwareHeader.Should().Be(SyntheticWired25);
            var identity = new byte[65]; Convert.FromHexString(SyntheticWired25).CopyTo(identity, 0);
            configured.RequireFirmwareIdentity(identity, SyntheticWired25); configured.SameDevice().Should().BeTrue();
            identity[5] = 0x24; // Same family, but a changed generation within this one connection.
            ((Action)(() => configured.RequireFirmwareIdentity(identity, SyntheticWired25))).Should().Throw<InvalidDataException>()
                .WithMessage("*generation changed*");
            identity[5] = 0x25;
            ((Action)(() => configured.RequireFirmwareIdentity(identity, RogWiredOledProtocol.LegacyFirmwareHeader))).Should().Throw<InvalidDataException>();
        }
        using var legacy = factory.TryOpen(key);
        legacy!.FirmwareHeader.Should().Be("001200000024000100"); opens.Should().Be(2); devices.SharedOpens.Should().Be(0);
    }

    [Fact]
    public void A_legacy_factory_implementation_keeps_its_one_argument_contract_and_cannot_adopt_a_new_header()
    {
        var legacy = new LegacyFactory(); IRogWiredOledFactory factory = legacy;
        factory.TryOpen("legacy-key", "001200000024000100").Should().BeNull(); legacy.Opens.Should().Be(1);
        ((Action)(() => factory.TryOpen("legacy-key", SyntheticWired25))).Should().Throw<InvalidOperationException>();
        legacy.Opens.Should().Be(1);
    }

    [Fact]
    public void Legacy_connection_cannot_adopt_a_new_header_from_a_matching_reply_alone()
    {
        using var io = new NoIo(); using var connection = new RogWiredOledConnection(io, () => true);
        var identity = new byte[65]; Convert.FromHexString(SyntheticWired25).CopyTo(identity, 0);
        ((Action)(() => connection.RequireFirmwareIdentity(identity, SyntheticWired25))).Should().Throw<InvalidDataException>();
        connection.FirmwareHeader.Should().Be("001200000024000100"); io.Commands.Should().Be(0);
    }

    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData("0012000000250001")] [InlineData("00120000002500010000")]
    [InlineData("001300000025000100")] [InlineData("001200000000000000")] [InlineData("00120000002G000100")]
    [InlineData("0800070025000100")] [InlineData("000000000000000000")] [InlineData("0012000000250001  ")]
    public void Invalid_or_zero_wired_pins_fail_before_enumeration_or_open(string? header)
    {
        var devices = new FakeDevices(); var opens = 0;
        var factory = new RogWiredOledFactory(devices, new Resolver(), _ => { opens++; return new NoIo(); });
        ((Action)(() => factory.TryOpen(new string('A', 64), header!))).Should().Throw<InvalidOperationException>();
        RogWiredOledProtocol.IsConfiguredFirmwareHeader(header).Should().BeFalse(); devices.Enumerations.Should().Be(0); opens.Should().Be(0);
        // The public default constructor also rejects these pins before it can reach native enumeration.
        ((Action)(() => new RogWiredOledFactory().TryOpen(new string('A', 64), header!))).Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData("model")] [InlineData("vid")] [InlineData("interface")] [InlineData("usage")]
    [InlineData("input")] [InlineData("output")] [InlineData("feature")] [InlineData("error")]
    public void Configured_firmware_does_not_relax_the_existing_collection_model(string change)
    {
        var devices = new FakeDevices(); var c = devices.Collection;
        devices.Collection = change switch {
            "model" => c with { Pid = 0x1C25 }, "vid" => c with { Vid = 0x1234 }, "interface" => c with { Interface = 0 },
            "usage" => c with { UsagePage = 0xFFC0 }, "input" => c with { InputReportLength = 64 },
            "output" => c with { OutputReportLength = 64 }, "feature" => c with { FeatureReportLength = 65 },
            _ => c with { Error = "unavailable" } };
        var opens = 0; var factory = new RogWiredOledFactory(devices, new Resolver(), _ => { opens++; return new NoIo(); });
        factory.TryOpen(RogWiredOledProtocol.DeviceKey(devices.Collection), SyntheticWired25).Should().BeNull(); opens.Should().Be(0);
    }

    [Fact]
    public void Physical_container_and_exact_path_remain_required_after_configured_open()
    {
        var devices = new FakeDevices(); var resolver = new Resolver();
        var factory = new RogWiredOledFactory(devices, resolver, _ => new NoIo());
        using var connection = factory.TryOpen(RogWiredOledProtocol.DeviceKey(devices.Collection), SyntheticWired25);
        connection!.SameDevice().Should().BeTrue(); resolver.Replace(); connection.SameDevice().Should().BeFalse();
        resolver.Unavailable = true;
        ((Action)(() => factory.TryOpen(RogWiredOledProtocol.DeviceKey(devices.Collection), SyntheticWired25))).Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Unavailable_keyboard_firmware_stops_the_actual_session_after_one_read_and_no_mutation(bool receiverAlsoDiffers)
    {
        var identity = new byte[65]; identity[1] = 0x12; identity[5] = receiverAlsoDiffers ? (byte)9 : (byte)8; identity[7] = 7;
        using var io = new IdentityChannel(identity);
        using var connection = new RogWirelessOledConnection(new(new string('A', 64), 0x1ACE, new Dictionary<byte, HidCollectionInfo>()), io, "0800070025000100", () => true);
        var session = new RogWiredOledSession(connection);
        await ((Func<Task>)(() => session.SnapshotAsync("0800070025000100", default))).Should().ThrowAsync<InvalidDataException>()
            .WithMessage("*identity*unavailable*");
        io.Writes.Should().Be(1); io.Reads.Should().Be(1); session.Touched.Should().BeFalse();
    }

    [Fact]
    public async Task Genuine_nonzero_keyboard_firmware_mismatch_keeps_the_existing_fail_closed_exception()
    {
        // Was keyboard patch 0x24 under a .25 anchor; that is the same 01 family and now connects without repinning
        // (see reports/EXPECTATION-CHANGES.en.md). A genuinely different keyboard family (02) still fails closed.
        var identity = new byte[65]; identity[1] = 0x12; identity[5] = 8; identity[7] = 7; identity[9] = 6; identity[11] = 6; identity[12] = 0x24; identity[14] = 2;
        using var io = new IdentityChannel(identity);
        using var connection = new RogWirelessOledConnection(new(new string('A', 64), 0x1ACE, new Dictionary<byte, HidCollectionInfo>()), io, "0800070025000100", () => true);
        await ((Func<Task>)(() => new RogWiredOledSession(connection).SnapshotAsync("0800070025000100", default))).Should().ThrowAsync<InvalidDataException>()
            .WithMessage("*firmware differs*");
        io.Writes.Should().Be(1); io.Reads.Should().Be(1);
    }

    private sealed class LegacyFactory : IRogWiredOledFactory
    { public int Opens { get; private set; } public RogWiredOledConnection? TryOpen(string deviceKey) { Opens++; return null; } }

    private sealed class FakeDevices : IHidReportDevices
    {
        public HidCollectionInfo Collection { get; set; } = new("synthetic-wired-mi_01", 0x0B05, 0x1C24, 1, 0xFF00, 1, 65, 65, 0);
        public int Enumerations { get; private set; } public int SharedOpens { get; private set; }
        public IReadOnlyList<HidCollectionInfo> Enumerate(ushort vid, ushort pid) { Enumerations++; vid.Should().Be(0x0B05); pid.Should().Be(0x1C24); return [Collection]; }
        public IHidReportChannel Open(HidCollectionInfo collection) { SharedOpens++; throw new InvalidOperationException("Shared owner forbidden"); }
    }

    private sealed class Resolver : IHidContainerResolver
    {
        private HidPhysicalContainer _container = HidPhysicalContainer.FromPlatformContainerId(Guid.NewGuid(), TimeProvider.System.GetTimestamp());
        public bool Unavailable { get; set; }
        public void Replace() => _container = HidPhysicalContainer.FromPlatformContainerId(Guid.NewGuid(), TimeProvider.System.GetTimestamp());
        public HidContainerResolution Resolve(HidCollectionInfo collection) => Unavailable ? HidContainerResolution.Unavailable("offline unavailable fixture") : HidContainerResolution.Resolved(_container);
    }

    private sealed class IdentityChannel(byte[] identity) : IHidReportChannel
    {
        public int Writes { get; private set; } public int Reads { get; private set; }
        public ValueTask WriteAsync(ReadOnlyMemory<byte> report, CancellationToken ct)
        { Writes++; report.Span[..5].ToArray().Should().Equal(2, 0x12, 0, 0, 0); if (Writes > 1) throw new InvalidOperationException("No automatic retry"); return ValueTask.CompletedTask; }
        public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
        { Reads++; var raw = new byte[64]; raw[0] = 2; identity.AsSpan(1, 63).CopyTo(raw.AsSpan(1)); raw.CopyTo(buffer); return ValueTask.FromResult(64); }
        public void Dispose() { }
    }

    private sealed class NoIo : IHidReportChannel
    {
        public int Commands { get; private set; }
        public ValueTask WriteAsync(ReadOnlyMemory<byte> report, CancellationToken ct) { Commands++; throw new InvalidOperationException("No hardware I/O in identity fixture"); }
        public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct) { Commands++; throw new InvalidOperationException("No hardware I/O in identity fixture"); }
        public void Dispose() { }
    }
}
