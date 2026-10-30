using System.Buffers.Binary;
using UnifiedRgb.Devices.AsusRog;
using UnifiedRgb.Transports.Hid;

namespace UnifiedRgb.Devices.Tests;

/// <summary>
/// Synthetic in-memory fixtures only: no physical header, build or image is asserted. Same-family identities and GYM1 builds
/// connect on an unchanged configured anchor; identity layout, report grammar, geometry, lifecycle and generation stay strict.
/// </summary>
public sealed partial class RuntimeCapabilityCompatibilityTests
{
    private const string Wired24 = "001200000024000100", Wired25 = "001200000025000100", Wired26 = "001200000026000100";
    private const string Wireless24 = "0800070024000100", Wireless25 = "0800070025000100", Wireless26 = "0800070026000100";
    private static readonly string Key = new('A', 64);

    [Theory]
    [InlineData(Wired24, 0x24)] [InlineData(Wired24, 0x25)] [InlineData(Wired24, 0x26)]
    [InlineData(Wired25, 0x24)] [InlineData(Wired25, 0x25)] [InlineData(Wired25, 0x26)]
    [InlineData(Wired26, 0x24)] [InlineData(Wired26, 0x25)] [InlineData(Wired26, 0x26)]
    public void Wired_same_family_headers_connect_on_any_configured_anchor_without_repinning(string anchor, byte patch)
    {
        using var io = new NoIo();
        using var connection = new RogWiredOledConnection(io, () => true, () => Key, () => anchor);
        var identity = WiredIdentity(patch);
        ((Action)(() => connection.RequireFirmwareIdentity(identity, anchor))).Should().NotThrow();
        connection.FirmwareHeader.Should().Be(anchor, "the configured pin stays the anchor; an observed header is never adopted");
        connection.ObservedFirmwareHeader.Should().Be(Convert.ToHexString(identity.AsSpan(0, 9)));
        io.Commands.Should().Be(0);
    }

    [Theory]
    [InlineData(Wireless24, 8, 0x24)] [InlineData(Wireless24, 8, 0x26)] [InlineData(Wireless24, 9, 0x25)]
    [InlineData(Wireless25, 8, 0x24)] [InlineData(Wireless25, 7, 0x26)] [InlineData(Wireless26, 8, 0x25)]
    public void Wireless_same_family_receiver_and_keyboard_connect_without_repinning(string anchor, byte receiverPatch, byte keyboardPatch)
    {
        using var io = new NoIo();
        using var connection = Wireless(io, anchor);
        ((Action)(() => connection.RequireFirmwareIdentity(WirelessIdentity(receiverPatch, keyboardPatch), anchor))).Should().NotThrow();
        connection.FirmwareHeader.Should().Be(anchor);
        connection.ObservedFirmwareHeader.Should().Be(Convert.ToHexString(new byte[] { receiverPatch, 0, 7, 0, keyboardPatch, 0, 1, 0 }));
        io.Commands.Should().Be(0);
    }

    [Theory]
    [InlineData("major")] [InlineData("minor")] [InlineData("build")] [InlineData("command")] [InlineData("prefix")]
    [InlineData("length")] [InlineData("other-anchor")]
    public void Wired_identity_checks_grammar_and_connection_generation_without_version_pins(string fault)
    {
        using var io = new NoIo();
        using var connection = new RogWiredOledConnection(io, () => true, () => Key, () => Wired24);
        var identity = WiredIdentity(0x25);
        var expected = Wired24;
        switch (fault)
        {
            case "major": identity[7] = 2; break;
            case "minor": identity[6] = 1; break;
            case "build": identity[8] = 1; break;
            case "command": identity[1] = 0x13; break;
            case "prefix": identity[2] = 1; break;
            case "length": identity = identity[..64]; break;
            default: expected = Wired25; break;
        }
        Action verify = () => connection.RequireFirmwareIdentity(identity, expected);
        if (fault is "command" or "prefix" or "length")
        {
            verify.Should().Throw<InvalidDataException>().WithMessage("*BasicInfo grammar*");
            connection.ObservedFirmwareHeader.Should().BeNull();
        }
        else
        {
            verify.Should().NotThrow("OEM fields and configured notes do not decide first admission");
            connection.ObservedFirmwareHeader.Should().Be(Convert.ToHexString(identity.AsSpan(0, 9)));
            identity[7] ^= 1;
            verify.Should().Throw<InvalidDataException>().WithMessage("*generation changed*");
        }
        connection.FirmwareHeader.Should().Be(Wired24);
        io.Commands.Should().Be(0);
    }

    [Theory]
    [InlineData("receiver-major", "*firmware differs*")] [InlineData("receiver-minor", "*firmware differs*")]
    [InlineData("keyboard-major", "*firmware differs*")] [InlineData("keyboard-minor", "*firmware differs*")]
    [InlineData("envelope", "*envelope is invalid*")] [InlineData("anchor", "*pin is invalid*")]
    [InlineData("asleep", "*identity is unavailable*")]
    public void Wireless_identity_checks_live_envelope_presence_and_generation(string fault, string message)
    {
        using var io = new NoIo();
        using var connection = Wireless(io, Wireless25);
        var identity = WirelessIdentity(8, 0x25);
        var anchor = Wireless25;
        switch (fault)
        {
            case "receiver-major": identity[7] = 8; break;
            case "receiver-minor": identity[6] = 1; break;
            case "keyboard-major": identity[14] = 2; break;
            case "keyboard-minor": identity[13] = 1; break;
            case "envelope": identity[1] = 0x13; break;
            case "anchor": anchor = "08000700250001"; break;
            default: identity.AsSpan(12, 4).Clear(); break;
        }
        Action verify = () => connection.RequireFirmwareIdentity(identity, anchor);
        if (fault is "envelope" or "asleep")
        {
            verify.Should().Throw<InvalidDataException>().WithMessage(message);
            connection.ObservedFirmwareHeader.Should().BeNull();
        }
        else
        {
            verify.Should().NotThrow("OEM bytes and configured notes are diagnostic");
            var observed = connection.ObservedFirmwareHeader;
            identity[14] ^= 1;
            verify.Should().Throw<InvalidDataException>().WithMessage("*generation changed*");
            connection.ObservedFirmwareHeader.Should().Be(observed);
        }
        io.Commands.Should().Be(0);
    }

    [Fact]
    public void A_generation_change_within_one_connection_is_refused_and_a_fresh_owner_negotiates_again()
    {
        using var io = new NoIo();
        using var wired = new RogWiredOledConnection(io, () => true, () => Key, () => Wired24);
        wired.RequireFirmwareIdentity(WiredIdentity(0x25), Wired24);
        ((Action)(() => wired.RequireFirmwareIdentity(WiredIdentity(0x26), Wired24))).Should().Throw<InvalidDataException>().WithMessage("*generation changed*");
        ((Action)(() => wired.RequireFirmwareIdentity(WiredIdentity(0x25), Wired24))).Should().NotThrow();
        wired.ObservedFirmwareHeader.Should().Be("001200000025000100");
        using var fresh = new RogWiredOledConnection(io, () => true, () => Key, () => Wired24);
        ((Action)(() => fresh.RequireFirmwareIdentity(WiredIdentity(0x26), Wired24))).Should().NotThrow();

        using var wireless = Wireless(io, Wireless24);
        wireless.RequireFirmwareIdentity(WirelessIdentity(8, 0x25), Wireless24);
        ((Action)(() => wireless.RequireFirmwareIdentity(WirelessIdentity(8, 0x26), Wireless24))).Should().Throw<InvalidDataException>().WithMessage("*generation changed*");
        ((Action)(() => wireless.RequireFirmwareIdentity(WirelessIdentity(9, 0x25), Wireless24))).Should().Throw<InvalidDataException>().WithMessage("*generation changed*");
        io.Commands.Should().Be(0);
    }

    [Fact]
    public void Legacy_one_argument_connection_accepts_its_family_but_never_adopts_another_anchor()
    {
        using var io = new NoIo(); using var connection = new RogWiredOledConnection(io, () => true);
        ((Action)(() => connection.RequireFirmwareIdentity(WiredIdentity(0x26), Wired26))).Should().NotThrow();
        ((Action)(() => connection.RequireFirmwareIdentity(WiredIdentity(0x26), RogWiredOledProtocol.LegacyFirmwareHeader))).Should().NotThrow();
        connection.FirmwareHeader.Should().Be(RogWiredOledProtocol.LegacyFirmwareHeader); io.Commands.Should().Be(0);
    }

    [Fact]
    public void Concrete_factory_keeps_exact_device_and_collection_identity_while_accepting_same_family_firmware()
    {
        var devices = new FakeDevices(); var opens = 0;
        var factory = new RogWiredOledFactory(devices, new Resolver(), _ => { opens++; return new NoIo(); });
        factory.TryOpen(new string('B', 64), Wired25).Should().BeNull("another device key is never substituted"); opens.Should().Be(0);
        devices.Collection = devices.Collection with { Interface = 0 };
        factory.TryOpen(RogWiredOledProtocol.DeviceKey(devices.Collection), Wired25).Should().BeNull(); opens.Should().Be(0);
        devices.Collection = devices.Collection with { Interface = 1 };
        using var connection = factory.TryOpen(RogWiredOledProtocol.DeviceKey(devices.Collection), Wired25);
        connection!.FirmwareHeader.Should().Be(Wired25); opens.Should().Be(1);
        ((Action)(() => connection.RequireFirmwareIdentity(WiredIdentity(0x24), Wired25))).Should().NotThrow();
        devices.SharedOpens.Should().Be(0);
    }

    [Fact]
    public async Task Same_family_session_keeps_profile_selector_content_and_brightness_grammar()
    {
        var device = new WiredDevice { Patch = 0x26, Brightness = 40 };
        using var connection = new RogWiredOledConnection(device, () => true, () => Key, () => Wired24);
        var session = new RogWiredOledSession(connection);
        (await session.SnapshotAsync(Wired24, default)).Should().Be(40);
        session.VerifiedProfileToken.Should().Be(6);
        await session.StartOutputAsync(100, default); device.Brightness.Should().Be(100);
        await session.RestoreAsync(40, default); device.Brightness.Should().Be(40);
    }

    [Theory]
    [InlineData("profile")] [InlineData("selector")] [InlineData("content")] [InlineData("brightness")] [InlineData("identity-envelope")]
    public async Task Same_family_number_does_not_substitute_for_reply_grammar(string fault)
    {
        var device = new WiredDevice { Patch = 0x25 };
        switch (fault)
        {
            case "profile": device.ActiveProfile = 7; break;
            case "selector": device.Selector = 2; break;
            case "content": device.Content = 0; break;
            case "brightness": device.Brightness = 101; break;
            default: device.ProfileCount = 0; break;
        }
        using var connection = new RogWiredOledConnection(device, () => true, () => Key, () => Wired24);
        var session = new RogWiredOledSession(connection);
        await ((Func<Task>)(() => session.SnapshotAsync(Wired24, default))).Should().ThrowAsync<InvalidDataException>();
        device.Mutations.Should().Be(0); session.Touched.Should().BeFalse();
    }

    [Theory]
    [InlineData("generation")] [InlineData("profile")]
    public async Task Changed_generation_or_profile_after_snapshot_blocks_every_write(string change)
    {
        var device = new WiredDevice { Patch = 0x25 };
        using var connection = new RogWiredOledConnection(device, () => true, () => Key, () => Wired24);
        var session = new RogWiredOledSession(connection);
        await session.SnapshotAsync(Wired24, default);
        if (change == "generation") device.Patch = 0x26; else device.ActiveProfile = 5;
        await ((Func<Task>)(() => session.StartOutputAsync(100, default))).Should().ThrowAsync<InvalidDataException>();
        await ((Func<Task>)(() => session.RestoreAsync(25, default))).Should().ThrowAsync<InvalidDataException>();
        device.Mutations.Should().Be(0); session.Touched.Should().BeFalse();
    }

    [Theory]
    [InlineData(0x49, 0x24, 1)] [InlineData(0x49, 0x25, 1)] [InlineData(0x49, 0x26, 2)]
    [InlineData(0x50, 0x24, 2)] [InlineData(0x50, 0x25, 1)] [InlineData(0x50, 0x26, 1)]
    [InlineData(0x51, 0x25, 1)] [InlineData(0x47, 0x24, 2)] [InlineData(0x48, 0x00, 1)]
    public void Valid_GYM1_capability_is_accepted_on_any_oem_header_and_build_tag(byte build, byte oem, byte transport)
    {
        var capability = RogNativeMenuCodec.ParseCapability(Capability(build, oem, transport), 0x12, transport);
        capability.Mode.Should().Be(RogNativeMenuCodec.NativeMode); capability.Transport.Should().Be(transport);
        RogNativeMenuCodec.SupportsInEpochArtwork(capability).Should().BeTrue();
        RogNativeMenuCodec.ArmRequest(capability, 0x1122334455667788)[25].Should().Be(RogNativeMenuCodec.NativeMode);
    }

    [Theory]
    [InlineData(64)] [InlineData(128)] [InlineData(192)]
    public void Unknown_optional_capability_bits_are_ignored_and_not_surfaced(byte unknown)
    {
        var idle = RogNativeMenuCodec.ParseCapability(Capability(flags: (byte)(48 | unknown)), 0x12, 1);
        idle.Flags.Should().Be(48);
        var armed = RogNativeMenuCodec.ParseCapability(Capability(status: 1, flags: (byte)(49 | unknown), command: 0x71), 0x71, 1);
        armed.Flags.Should().Be(49);
        var state = new RogNativeMenuState();
        state.Arm(idle, armed, armed.Cookie);
        state.Matches(armed).Should().BeTrue();
    }

    [Theory]
    [InlineData("length")] [InlineData("command")] [InlineData("magic")] [InlineData("proto1")] [InlineData("status3")]
    [InlineData("mode10")] [InlineData("profile6")] [InlineData("transport")] [InlineData("missing-artwork")]
    [InlineData("width")] [InlineData("height")] [InlineData("chunk")] [InlineData("layout")] [InlineData("padding")]
    [InlineData("epoch0")] [InlineData("epochmax")] [InlineData("sequencemax")] [InlineData("idle-cookie")] [InlineData("idle-lifecycle-bit")]
    public void Capability_grammar_geometry_and_lifecycle_stay_fail_closed(string fault)
    {
        var p = Capability();
        byte command = 0x12, transport = 1;
        switch (fault)
        {
            case "length": p = p[..64]; break;
            case "command": command = 0x71; break;
            case "magic": p[8] = (byte)'2'; break;
            case "proto1": p[9] = 1; break;
            case "status3": p[10] = 3; break;
            case "mode10": p[11] = 10; break;
            case "profile6": p[12] = 6; break;
            case "transport": transport = 2; break;
            case "missing-artwork": p[14] = 16; break;
            case "width": p[33] = 207; break;
            case "height": p[35] = 63; break;
            case "chunk": p[36] = 59; break;
            case "layout": p[37] = 2; break;
            case "padding": p[^1] = 1; break;
            case "epoch0": p.AsSpan(15, 4).Clear(); break;
            case "epochmax": p.AsSpan(15, 4).Fill(0xFF); break;
            case "sequencemax": p[27] = p[28] = 0xFF; break;
            case "idle-cookie": p[19] = 1; break;
            default: p[14] |= 1; break;
        }
        ((Action)(() => RogNativeMenuCodec.ParseCapability(p, command, transport))).Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void Native_notice_grammar_is_unchanged_and_ordinary_keyboard_reports_are_never_blocked()
    {
        var tagged = Notice(flags: 1);
        RogNativeMenuCodec.ParseNotice(tagged, 1, out var notice).Should().Be(RogNativeNoticeKind.Tagged); notice.Flags.Should().Be(1);
        RogNativeMenuCodec.ParseNotice(Notice(flags: 9), 1, out _).Should().Be(RogNativeNoticeKind.Malformed, "notice flags are message grammar, not optional capability bits");
        var ordinary = new byte[21]; ordinary[0] = 3; ordinary[1] = 0x72;
        RogNativeMenuCodec.ParseNotice(ordinary, 1, out _).Should().Be(RogNativeNoticeKind.Ignore);
        var key = new byte[21]; key[0] = 3; key[1] = 0x01; key[5] = 0xFF;
        RogNativeMenuCodec.ParseNotice(key, 1, out _).Should().Be(RogNativeNoticeKind.Ignore);
    }

    [Theory]
    [InlineData(0x24)] [InlineData(0x25)] [InlineData(0x26)] [InlineData(0x51)] [InlineData(0x01)]
    public void Native_family_anchor_admits_any_live_patch_and_reports_the_true_version(byte patch)
    {
        var anchor = RogRuntimeCompatibility.NativeFamilyAnchor("wired")!;
        using var io = new NoIo();
        using var wired = new RogWiredOledConnection(io, () => true, () => Key, () => anchor);
        ((Action)(() => wired.RequireFirmwareIdentity(WiredIdentity(patch), anchor))).Should().NotThrow();
        wired.ObservedFirmwareHeader.Should().Be($"0012000000{patch:X2}000100", "the observed version stays truthful");
        wired.FirmwareHeader.Should().Be(anchor);
        var rf = RogRuntimeCompatibility.NativeFamilyAnchor("wireless")!;
        using var wireless = Wireless(io, rf);
        ((Action)(() => wireless.RequireFirmwareIdentity(WirelessIdentity(8, patch), rf))).Should().NotThrow();
        wireless.ObservedFirmwareHeader.Should().Be($"08000700{patch:X2}000100");
        io.Commands.Should().Be(0);
    }

    [Theory]
    [InlineData("wired-major")] [InlineData("wired-minor")] [InlineData("receiver-major")] [InlineData("keyboard-major")] [InlineData("asleep")]
    public void Native_transport_anchor_accepts_oem_fields_but_rejects_changed_live_generation(string fault)
    {
        using var io = new NoIo();
        if (fault.StartsWith("wired", StringComparison.Ordinal))
        {
            var anchor = RogRuntimeCompatibility.NativeFamilyAnchor("wired")!;
            using var wired = new RogWiredOledConnection(io, () => true, () => Key, () => anchor);
            var identity = WiredIdentity(0x25); identity[fault == "wired-major" ? 7 : 6] ^= 1;
            wired.RequireFirmwareIdentity(identity, anchor);
            identity[5] ^= 1;
            ((Action)(() => wired.RequireFirmwareIdentity(identity, anchor))).Should().Throw<InvalidDataException>().WithMessage("*generation changed*");
            return;
        }
        var rf = RogRuntimeCompatibility.NativeFamilyAnchor("wireless")!;
        using var wireless = Wireless(io, rf);
        var reply = WirelessIdentity(8, 0x25);
        switch (fault)
        {
            case "receiver-major": reply[7] = 8; break;
            case "keyboard-major": reply[14] = 2; break;
            default: reply.AsSpan(12, 4).Clear(); break;
        }
        if (fault == "asleep")
        {
            ((Action)(() => wireless.RequireFirmwareIdentity(reply, rf))).Should().Throw<InvalidDataException>().WithMessage("*unavailable*");
            wireless.ObservedFirmwareHeader.Should().BeNull();
        }
        else
        {
            wireless.RequireFirmwareIdentity(reply, rf);
            reply[5] ^= 1;
            ((Action)(() => wireless.RequireFirmwareIdentity(reply, rf))).Should().Throw<InvalidDataException>().WithMessage("*generation changed*");
        }
    }

    [Theory]
    [InlineData("wired", Wired25, true)] [InlineData("wired", Wired24, true)] [InlineData("wired", "001200000051000100", true)]
    [InlineData("wireless", Wireless25, true)] [InlineData("wireless", "0900070024000100", true)]
    [InlineData("wired", "001200000025000200", true)] [InlineData("wired", Wireless25, false)] [InlineData("wireless", Wired25, false)]
    [InlineData("wireless", "0800080025000100", true)] [InlineData("wireless", "0800070025000200", true)]
    public void Native_family_anchor_reads_journals_of_any_former_route_label_of_that_family(string kind, string recorded, bool compatible)
    {
        RogRuntimeCompatibility.IsCompatibleAnchor(recorded, RogRuntimeCompatibility.NativeFamilyAnchor(kind)).Should().Be(compatible);
        RogRuntimeCompatibility.NativeFamilyAnchor("bluetooth").Should().BeNull(); RogRuntimeCompatibility.NativeFamilyAnchor(null).Should().BeNull();
    }

    [Theory]
    [InlineData(Wired24, Wired25, true)] [InlineData(Wired26, Wired24, true)] [InlineData(Wired25, "001200000025000100", true)]
    [InlineData(Wired24, "001200000024000200", true)] [InlineData(Wired24, "001200000024010100", true)]
    [InlineData(Wireless24, Wireless26, true)] [InlineData(Wireless25, "0900070024000100", true)]
    [InlineData(Wireless24, "0800080024000100", true)] [InlineData(Wireless24, "0800070024000200", true)]
    [InlineData(Wired24, Wireless24, false)] [InlineData(Wireless24, Wired24, false)]
    [InlineData(Wired24, "00120000002G000100", false)] [InlineData(Wired24, "001300000024000100", false)]
    [InlineData(Wired24, "", false)] [InlineData(null, Wired24, false)] [InlineData(null, null, false)]
    public void Recorded_anchor_fingerprint_binds_transport_format_not_oem_bytes(string? recorded, string? current, bool compatible)
    {
        RogRuntimeCompatibility.IsCompatibleAnchor(recorded, current).Should().Be(compatible);
        RogRuntimeCompatibility.IsCompatibleAnchor(current, recorded).Should().Be(compatible);
    }

    private static byte[] WiredIdentity(byte patch, byte major = 1)
    { var b = new byte[65]; b[1] = 0x12; b[5] = patch; b[7] = major; b[9] = 6; b[10] = 0; b[11] = 6; return b; }

    private static byte[] WirelessIdentity(byte receiverPatch, byte keyboardPatch)
    { var b = new byte[65]; b[1] = 0x12; b[5] = receiverPatch; b[7] = 7; b[9] = 6; b[11] = 6; b[12] = keyboardPatch; b[14] = 1; return b; }

    private static RogWirelessOledConnection Wireless(IHidReportChannel io, string anchor) =>
        new(new RogReceiver(Key, 0x1ACE, new Dictionary<byte, HidCollectionInfo>()), io, anchor, () => true);

    private static byte[] Capability(byte build = 0x49, byte oem = 0x25, byte transport = 1, byte flags = 48, byte status = 0, byte command = 0x12)
    {
        var p = new byte[65]; p[1] = command; p[2] = 0x7D; "GYM1"u8.CopyTo(p.AsSpan(5));
        p[9] = 2; p[10] = status; p[11] = RogNativeMenuCodec.NativeMode; p[12] = 2; p[13] = transport; p[14] = flags;
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(15), status == 0 ? 7u : 8u);
        if (status != 0) BinaryPrimitives.WriteUInt64LittleEndian(p.AsSpan(19), 0x1122334455667788);
        p[29] = build; p[30] = 0x4D; p[31] = 0x31; p[32] = oem;
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(33), 208); p[35] = 64; p[36] = transport == 1 ? (byte)60 : (byte)59; p[37] = 3;
        return p;
    }

    private static byte[] Notice(byte flags)
    {
        var p = new byte[21]; p[0] = 3; p[1] = 0x72; p[2] = 2; p[3] = 0xD5; p[4] = RogNativeMenuCodec.NativeMode; p[5] = 2; p[6] = flags;
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(7), 8); BinaryPrimitives.WriteUInt64LittleEndian(p.AsSpan(11), 0x1122334455667788);
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(19), 1);
        return p;
    }

    private sealed class WiredDevice : IHidReportChannel
    {
        private readonly Queue<byte[]> _replies = new();
        public byte Patch { get; set; } = 0x24;
        public byte Major { get; set; } = 1;
        public byte ProfileCount { get; set; } = 6;
        public byte ActiveProfile { get; set; } = 6;
        public byte Selector { get; set; } = 3;
        public byte Content { get; set; } = 1;
        public byte Brightness { get; set; } = 25;
        public int Mutations { get; private set; }
        public ValueTask WriteAsync(ReadOnlyMemory<byte> report, CancellationToken ct)
        {
            var p = report.ToArray(); var b = new byte[65]; p.AsSpan(0, 5).CopyTo(b);
            switch (p[1], p[2])
            {
                case (0x12, 0): b[5] = Patch; b[7] = Major; b[9] = ProfileCount; b[11] = ActiveProfile; break;
                case (0x21, 0): b[5] = Selector; break;
                case (0x23, 2): b[5] = Content; break;
                case (0x23, 3): b[5] = Brightness; break;
                case (0x61, 0): Mutations++; break;
                case (0x68, 0): Mutations++; Brightness = p[5]; b = p; break;
                default: throw new InvalidOperationException("Unexpected synthetic packet.");
            }
            _replies.Enqueue(b); return ValueTask.CompletedTask;
        }
        public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
        {
            var reply = _replies.Count > 0 ? _replies.Dequeue() : throw new InvalidOperationException("No unsolicited synthetic reply.");
            reply.CopyTo(buffer); return ValueTask.FromResult(reply.Length);
        }
        public void Dispose() { }
    }

    private sealed class FakeDevices : IHidReportDevices
    {
        public HidCollectionInfo Collection { get; set; } = new("synthetic-runtime-mi_01", 0x0B05, 0x1C24, 1, 0xFF00, 1, 65, 65, 0);
        public int SharedOpens { get; private set; }
        public IReadOnlyList<HidCollectionInfo> Enumerate(ushort vid, ushort pid) => [Collection];
        public IHidReportChannel Open(HidCollectionInfo collection) { SharedOpens++; throw new InvalidOperationException("Shared owner forbidden"); }
    }

    private sealed class Resolver : IHidContainerResolver
    {
        private readonly HidPhysicalContainer _container = HidPhysicalContainer.FromPlatformContainerId(Guid.NewGuid(), TimeProvider.System.GetTimestamp());
        public HidContainerResolution Resolve(HidCollectionInfo collection) => HidContainerResolution.Resolved(_container);
    }

    private sealed class NoIo : IHidReportChannel
    {
        public int Commands { get; private set; }
        public ValueTask WriteAsync(ReadOnlyMemory<byte> report, CancellationToken ct) { Commands++; throw new InvalidOperationException("No I/O in identity fixture"); }
        public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct) { Commands++; throw new InvalidOperationException("No I/O in identity fixture"); }
        public void Dispose() { }
    }
}
