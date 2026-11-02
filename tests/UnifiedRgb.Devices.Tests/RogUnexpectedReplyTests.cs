using UnifiedRgb.Devices.AsusRog;
using UnifiedRgb.Transports.Hid;

namespace UnifiedRgb.Devices.Tests;

public sealed class RogUnexpectedReplyTests
{
    private const string Header = "0800070025000100";

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Received_0225000100_is_rejected_and_poison_prevents_all_later_IO(bool nonzeroBody)
    {
        // Only the production header was captured. Neither synthetic body is claimed to be its payload or an ACK.
        var reply = Unexpected(); if (nonzeroBody) reply.AsSpan(5).Fill(0xA5);
        using var io = new ReplyChannel { Inject = _ => reply };
        using var connection = Open(io);
        var error = await ((Func<Task>)(() => connection.ExchangeAsync(RogWiredOledProtocol.Query(0x12, 0), false, default)))
            .Should().ThrowAsync<RogOledUnexpectedReplyException>();
        error.Which.Message.Should().Contain("stage=optional-renewal").And.Contain("request=0212000000")
            .And.Contain("reply=0225000100").And.Contain("length=64");
        io.Writes.Should().Be(1); io.Reads.Should().Be(1);
        connection.ObservedFirmwareHeader.Should().BeNull("unmatched bytes are not identity");
        await NoLaterIo(connection, io);
    }

    [Theory]
    [InlineData("idle", RogReadOnlyOperation.IdleScopeObservation)]
    [InlineData("pre-key", RogReadOnlyOperation.PreKeyFrameScope)]
    [InlineData("brightness", RogReadOnlyOperation.IdleBrightnessObservation)]
    public async Task Only_standalone_observation_roles_get_recoverable_retirement(string role, RogReadOnlyOperation expected)
    {
        using var io = new ReplyChannel(); using var connection = Open(io);
        var session = new RogWiredOledSession(connection);
        await session.SnapshotAsync(Header, default); await session.SnapshotLightingAsync(default);
        io.Inject = p => p[1] == (role == "brightness" ? 0x23 : 0x12) ? Unexpected() : null;
        var bridge = new RogWiredLightingBridge(); var owner = bridge.Attach(); owner.AcceptKeyFrames(RogPerKeyLighting.WirelessTransport);
        Task? keys = role == "pre-key" ? bridge.WriteKeysAsync([new(0, new(1, 2, 3))], RogPerKeyLighting.WirelessTransport, default) : null;
        try
        {
            var error = await ((Func<Task>)(() => session.RunAsync(() => new byte[6656], () => { }, default, owner, false)))
                .Should().ThrowAsync<RogReadOnlyObservationUnexpectedReplyException>();
            error.Which.Operation.Should().Be(expected); error.Which.ProfileToken.Should().Be(6);
            error.Which.DeviceKey.Should().Be(connection.DeviceKey); error.Which.ConnectionKind.Should().Be("wireless");
            error.Which.InnerException.Should().BeOfType<RogOledUnexpectedReplyException>();
            if (keys is not null) await ((Func<Task>)(() => keys)).Should().ThrowAsync<RogReadOnlyObservationUnexpectedReplyException>();
            io.Inner.Mutations.Should().Be(0);
            await NoLaterIo(connection, io);
        }
        finally { bridge.Detach(owner); }
    }

    [Theory]
    [InlineData("setter")] [InlineData("setter-readback")] [InlineData("restore-scope")]
    [InlineData("restore-verification")] [InlineData("image-chunk")] [InlineData("native-mutation")]
    public async Task Mutation_and_mandatory_verification_never_become_readonly_retryable(string role)
    {
        using var io = new ReplyChannel(); using var connection = Open(io);
        var session = new RogWiredOledSession(connection); await session.SnapshotAsync(Header, default);
        io.Inject = p => (role switch
        {
            "setter" => p[1] == 0x68,
            "setter-readback" => p[1] == 0x23 && p[2] == 3,
            "restore-scope" => p[1] == 0x12,
            "restore-verification" => p[1] == 0x21,
            "image-chunk" => p[1] == 0x67 && p[2] == 1,
            _ => p[1] == 0x51,
        }) ? Unexpected() : null;
        Func<Task> action = role switch
        {
            "restore-scope" or "restore-verification" => () => session.RestoreAsync(25, default),
            "image-chunk" => () => session.RunAsync(() => new byte[6656], () => { }, default),
            "native-mutation" => () => connection.ExchangeAsync(NativeSetter(), false, default),
            _ => () => session.StartOutputAsync(100, default),
        };
        var error = await action.Should().ThrowAsync<RogOledUnexpectedReplyException>();
        error.Which.Should().NotBeAssignableTo<RogReadOnlyObservationException>();
        await NoLaterIo(connection, io);
    }

    [Theory]
    [InlineData("short")] [InlineData("rid")] [InlineData("negative")] [InlineData("bad-negative")]
    [InlineData("eof")] [InlineData("renewal-body")] [InlineData("bad-capability")]
    [InlineData("profile")] [InlineData("generation")] [InlineData("brightness-value")]
    public async Task Malformed_negative_EOF_and_semantic_failures_are_not_reclassified(string kind)
    {
        using var io = new ReplyChannel(); using var connection = Open(io);
        var session = new RogWiredOledSession(connection); await session.SnapshotAsync(Header, default);
        if (kind is "profile" or "generation" or "brightness-value")
            io.Transform = reply =>
            {
                if (kind == "profile" && reply[1] == 0x12) reply[11] = 5;
                if (kind == "generation" && reply[1] == 0x12) reply[12]++;
                if (kind == "brightness-value" && reply[1] == 0x23) reply[5] = 101;
            };
        else io.Inject = _ => StrictFailure(kind);
        var error = await ((Func<Task>)(() => session.RunAsync(() => new byte[6656], () => { }, default, display: false)))
            .Should().ThrowAsync<Exception>();
        error.Which.Should().NotBeAssignableTo<RogReadOnlyObservationException>();
        error.Which.Should().NotBeOfType<RogOledUnexpectedReplyException>();
        error.Which.Should().BeOfType(kind switch
        {
            "negative" => typeof(RogOledEnvelopeRefusedException),
            "eof" => typeof(RogOledConnectionLostException),
            _ => typeof(InvalidDataException),
        });
        // Semantic failures are rejected by the session, not transport poisoning. Do not broaden that contract here.
        if (kind is not ("profile" or "generation" or "brightness-value")) await NoLaterIo(connection, io);
    }

    [Fact]
    public async Task Unmatched_predrain_and_lease_reports_remain_strict_not_standalone_exchange_failures()
    {
        foreach (var lease in new[] { false, true })
        {
            using var io = new ReplyChannel(); using var connection = Open(io);
            await connection.ExchangeAsync(RogWiredOledProtocol.Query(0x12, 0), false, default);
            io.Pending = Unexpected();
            Func<Task> action = lease ? () => connection.LeaseAsync(() => false, () => { }, default)
                : () => connection.ExchangeAsync(RogWiredOledProtocol.Query(0x12, 0), false, default);
            await action.Should().ThrowAsync<InvalidDataException>(); await NoLaterIo(connection, io);
        }
    }

    private static byte[] Unexpected() { var p = new byte[64]; p[0] = 2; p[1] = 0x25; p[3] = 1; return p; }
    private static byte[] NativeSetter()
    { var p = new byte[65]; RogProtocol.KeyboardNativeReports(2, RogProtocol.KeyboardColourFrame(new(1, 2, 3), 25, 80))[0].CopyTo(p, 0); p[0] = 0; return p; }
    private static byte[] StrictFailure(string kind)
    {
        var p = Unexpected();
        switch (kind)
        {
            case "short": return p[..63];
            case "rid": p[0] = 3; break;
            case "negative": case "bad-negative": p[1] = 0xFF; p[2] = 0xAA; p[3] = 0; if (kind == "bad-negative") p[5] = 1; break;
            case "eof": return [];
            case "renewal-body": p[1] = 0x67; p[2] = 2; p[3] = 0; p[5] = 1; break;
            case "bad-capability": p[1] = 0x12; p[2] = 0x7D; p[3] = 0; break;
        }
        return p;
    }
    private static RogWirelessOledConnection Open(ReplyChannel io) => new(new(new string('A', 64), 0x1ACE,
        new Dictionary<byte, HidCollectionInfo>()), io, Header, () => true);
    private static async Task NoLaterIo(RogWirelessOledConnection connection, ReplyChannel io)
    {
        var writes = io.Writes; var reads = io.Reads;
        await ((Func<Task>)(() => connection.ExchangeAsync(RogWiredOledProtocol.Query(0x12, 0), false, default))).Should().ThrowAsync<IOException>().WithMessage("*poisoned*");
        await ((Func<Task>)(() => connection.ExchangeAsync(RogWiredOledProtocol.Brightness(25), false, default))).Should().ThrowAsync<IOException>().WithMessage("*poisoned*");
        await ((Func<Task>)(() => connection.LeaseAsync(() => false, () => { }, default))).Should().ThrowAsync<IOException>().WithMessage("*poisoned*");
        await ((Func<Task>)(() => connection.WriteLightingFrameAsync([new(0, new(1, 2, 3))], default))).Should().ThrowAsync<IOException>().WithMessage("*poisoned*");
        io.Writes.Should().Be(writes); io.Reads.Should().Be(reads); io.Inner.PendingReads.Should().Be(0);
    }

    private sealed class ReplyChannel : IHidReportChannel
    {
        public RogWiredOledTests.OledFake Inner { get; } = new();
        public Func<byte[], byte[]?>? Inject { get; set; }
        public Action<byte[]>? Transform { get; set; }
        public byte[]? Pending { get; set; }
        public int Writes { get; private set; }
        public int Reads { get; private set; }
        public ValueTask WriteAsync(ReadOnlyMemory<byte> report, CancellationToken ct)
        {
            Writes++; var p = report.ToArray();
            if (Inject?.Invoke(p) is { } injected) { Pending = injected; return ValueTask.CompletedTask; }
            var wired = new byte[65]; p.AsSpan(1, 63).CopyTo(wired.AsSpan(1)); return Inner.WriteAsync(wired, ct);
        }
        public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
        {
            Reads++;
            if (Pending is { } pending) { Pending = null; pending.CopyTo(buffer); return pending.Length; }
            var wired = new byte[65]; var length = await Inner.ReadAsync(wired, ct); if (length == 0) return 0;
            var p = new byte[64]; wired.AsSpan(1, 63).CopyTo(p.AsSpan(1)); p[0] = 2;
            if (p[1] == 0x12 && p[2] == 0) { p[5] = 8; p[7] = 7; p[12] = 0x25; p[14] = 1; }
            Transform?.Invoke(p); p.CopyTo(buffer); return 64;
        }
        public void Dispose() => Inner.Dispose();
    }
}
