using System.Threading.Channels;
using System.Text.Json;
using UnifiedRgb.Devices.AsusRog;
using UnifiedRgb.Transports.Hid;

namespace UnifiedRgb.Devices.Tests;

/// <summary>
/// Focused verification of the integrated RogWiredOledSession per-key path: the real actor loop
/// accepts its own route, checks scope before a key frame, sends it on its one connection and fails closed without retry.
/// Fake wired channel only; nothing here opens a device or proves optical output or restoration after direct mode.
/// </summary>
public sealed class RogWiredOledSessionPerKeyDeltaTests
{
    private static RogKeyColor[] Keys(int count) =>
        Enumerable.Range(0, count).Select(i => new RogKeyColor((byte)i, new((byte)i, (byte)(255 - i), (byte)(i * 3)))).ToArray();

    [Fact]
    public async Task The_actor_accepts_its_route_and_sends_every_key_frame_after_a_scope_check()
    {
        using var inner = new RogWiredOledTests.OledFake(); using var io = new KeyEchoFake(inner);
        using var connection = new RogWiredOledConnection(io, () => true); var session = new RogWiredOledSession(connection);
        await session.SnapshotAsync("001200000024000100", default); await session.SnapshotLightingAsync(default);
        var bridge = new RogWiredLightingBridge(); var owner = bridge.Attach(); using var stop = new CancellationTokenSource();
        var run = session.RunAsync(() => new byte[6656], () => { }, stop.Token, owner, false);
        bridge.KeyFrameTransport.Should().Be(RogPerKeyLighting.WiredTransport);
        session.LightingTouched.Should().BeFalse();

        var keys = Keys(81);
        var before = io.Operations.Count;
        await bridge.WriteKeysAsync(keys, RogPerKeyLighting.WiredTransport, default);

        io.KeyReports.Select(Convert.ToHexString).Should().Equal(RogPerKeyLighting.BoundaryFrame(keys).Select(Convert.ToHexString));
        var operations = io.Operations.Skip(before).ToList();
        var first = operations.IndexOf((0xC0, 0x81));
        first.Should().BePositive();
        operations[first - 1].Should().Be(((byte)0x27, (byte)0), "the software-control gate is read back before the first frame");
        operations.Take(first).Should().ContainInOrder(((byte)0x12, (byte)0), ((byte)0x74, (byte)0), ((byte)0x27, (byte)0));
        operations.Skip(first).Take(6).Should().OnlyContain(o => o.Command == 0xC0 && o.Key == 0x81, "one frame is sent as one uninterrupted run");
        session.LightingTouched.Should().BeTrue();
        inner.SoftwareControl.Should().Be(1);

        var writes = inner.NativeWrites;
        await bridge.WriteAsync(new(1, 2, 3), default);
        inner.NativeWrites.Should().BeGreaterThan(writes, "the aggregate colour path is unchanged");
        inner.SoftwareControl.Should().Be(0, "aggregate return restores the original software-control flag");

        stop.Cancel(); await ((Func<Task>)(() => run)).Should().ThrowAsync<OperationCanceledException>(); bridge.Detach(owner);
        await session.RestoreLightingAsync(session.OriginalLighting!, session.OriginalStaticBank!, default);
    }

    [Fact]
    public async Task A_refused_key_report_fails_the_writer_and_ends_the_actor_without_retry()
    {
        using var inner = new RogWiredOledTests.OledFake(); using var io = new KeyEchoFake(inner) { RefuseKeys = true };
        using var connection = new RogWiredOledConnection(io, () => true); var session = new RogWiredOledSession(connection);
        await session.SnapshotAsync("001200000024000100", default); await session.SnapshotLightingAsync(default);
        var bridge = new RogWiredLightingBridge(); var owner = bridge.Attach(); using var stop = new CancellationTokenSource();
        var run = session.RunAsync(() => new byte[6656], () => { }, stop.Token, owner, false);

        await ((Func<Task>)(() => bridge.WriteKeysAsync(Keys(30), RogPerKeyLighting.WiredTransport, default))).Should().ThrowAsync<InvalidDataException>();
        await ((Func<Task>)(() => run)).Should().ThrowAsync<InvalidDataException>();
        io.KeyReports.Should().HaveCount(1);
        session.LightingTouched.Should().BeTrue("the owner must run its original-lighting restore");
        bridge.Detach(owner);
    }

    [Fact]
    public async Task An_original_enabled_flag_is_journalled_and_never_toggled_by_key_or_aggregate_output()
    {
        using var inner = new RogWiredOledTests.OledFake { SoftwareControl = 1 }; using var io = new KeyEchoFake(inner);
        using var connection = new RogWiredOledConnection(io, () => true); var session = new RogWiredOledSession(connection);
        await session.SnapshotAsync("001200000024000100", default); await session.SnapshotLightingAsync(default);
        var saved = JsonSerializer.Deserialize<RogKeyboardNativeState>(JsonSerializer.Serialize(session.OriginalLighting))!;
        saved.SoftwareControl.Should().Be(1);
        inner.Mutations.Should().Be(0, "the snapshot only reads the original boolean and lighting banks");
        var bridge = new RogWiredLightingBridge(); var owner = bridge.Attach(); using var stop = new CancellationTokenSource();
        var run = session.RunAsync(() => new byte[6656], () => { }, stop.Token, owner, false);
        await bridge.WriteKeysAsync(Keys(1), RogPerKeyLighting.WiredTransport, default);
        await bridge.WriteAsync(new(1, 2, 3), default);
        inner.SoftwareControl.Should().Be(1);
        inner.Opcodes.Should().NotContain(0x74, "unchanged software control is never toggled");
        stop.Cancel(); await ((Func<Task>)(() => run)).Should().ThrowAsync<OperationCanceledException>(); bridge.Detach(owner);
    }

    [Fact]
    public async Task A_fresh_connection_restores_the_journalled_flag_before_native_banks_after_a_key_failure()
    {
        using var inner = new RogWiredOledTests.OledFake(); using var io = new KeyEchoFake(inner) { RefuseKeys = true };
        using var connection = new RogWiredOledConnection(io, () => true); var session = new RogWiredOledSession(connection);
        await session.SnapshotAsync("001200000024000100", default); await session.SnapshotLightingAsync(default);
        var lighting = JsonSerializer.Serialize(session.OriginalLighting);
        var bank = JsonSerializer.Serialize(session.OriginalStaticBank);
        JsonSerializer.Deserialize<RogKeyboardNativeState>(lighting)!.SoftwareControl.Should().Be(0);
        inner.Mutations.Should().Be(0, "the lighting record is serializable before any SET74");
        var bridge = new RogWiredLightingBridge(); var owner = bridge.Attach(); using var stop = new CancellationTokenSource();
        var run = session.RunAsync(() => new byte[6656], () => { }, stop.Token, owner, false);
        await ((Func<Task>)(() => bridge.WriteKeysAsync(Keys(1), RogPerKeyLighting.WiredTransport, default))).Should().ThrowAsync<InvalidDataException>();
        await ((Func<Task>)(() => run)).Should().ThrowAsync<InvalidDataException>();
        inner.SoftwareControl.Should().Be(1); session.LightingTouched.Should().BeTrue(); bridge.Detach(owner);

        using var fresh = new RogWiredOledTests.OledFake { SoftwareControl = 1 };
        using var freshConnection = new RogWiredOledConnection(fresh, () => true); var recovery = new RogWiredOledSession(freshConnection);
        await recovery.SnapshotAsync("001200000024000100", default);
        await recovery.RestoreLightingAsync(JsonSerializer.Deserialize<RogKeyboardNativeState>(lighting)!, JsonSerializer.Deserialize<RogKeyboardNativeState>(bank)!, default);
        fresh.SoftwareControl.Should().Be(0);
        fresh.Opcodes.IndexOf(0x74).Should().BeLessThan(fresh.Opcodes.IndexOf(0x51));
    }

    [Fact]
    public async Task A_legacy_journal_without_the_optional_flag_leaves_software_control_unchanged()
    {
        using var io = new RogWiredOledTests.OledFake { SoftwareControl = 1 };
        var old = JsonSerializer.Serialize(new { io.Native.EffectType, io.Native.Payloads });
        var legacy = JsonSerializer.Deserialize<RogKeyboardNativeState>(old)!;
        legacy.SoftwareControl.Should().BeNull();
        using var connection = new RogWiredOledConnection(io, () => true); var session = new RogWiredOledSession(connection);
        await session.SnapshotAsync("001200000024000100", default);
        await session.RestoreLightingAsync(legacy, legacy, default);
        io.SoftwareControl.Should().Be(1); io.Opcodes.Should().NotContain(0x74);
    }

    [Fact]
    public async Task A_setter_echo_without_the_required_getter_readback_stops_before_any_key_report()
    {
        using var inner = new RogWiredOledTests.OledFake { IgnoreSoftwareControlSetter = true }; using var io = new KeyEchoFake(inner);
        using var connection = new RogWiredOledConnection(io, () => true); var session = new RogWiredOledSession(connection);
        await session.SnapshotAsync("001200000024000100", default); await session.SnapshotLightingAsync(default);
        var bridge = new RogWiredLightingBridge(); var owner = bridge.Attach(); using var stop = new CancellationTokenSource();
        var run = session.RunAsync(() => new byte[6656], () => { }, stop.Token, owner, false);
        await ((Func<Task>)(() => bridge.WriteKeysAsync(Keys(1), RogPerKeyLighting.WiredTransport, default))).Should().ThrowAsync<InvalidDataException>();
        await ((Func<Task>)(() => run)).Should().ThrowAsync<InvalidDataException>();
        io.KeyReports.Should().BeEmpty(); session.LightingTouched.Should().BeTrue(); bridge.Detach(owner);
    }

    /// <summary>Answers C0 81 with C0/82 (or FF AA) and delegates every other command to the existing OLED fake.</summary>
    private sealed class KeyEchoFake(RogWiredOledTests.OledFake inner) : IHidReportChannel
    {
        private readonly Channel<byte[]> _replies = Channel.CreateUnbounded<byte[]>();
        public List<byte[]> KeyReports { get; } = [];
        public List<(byte Command, byte Key)> Operations { get; } = [];
        public bool RefuseKeys { get; init; }

        public async ValueTask WriteAsync(ReadOnlyMemory<byte> report, CancellationToken ct)
        {
            var p = report.ToArray();
            Operations.Add((p[1], p[2]));
            if (p[1] == 0xC0 && p[2] == 0x81)
            {
                KeyReports.Add(p);
                var echo = new byte[65]; p.AsSpan(0, 5).CopyTo(echo);
                echo[2] = 0x82; echo.AsSpan(5, 23).Fill(0x5A);
                if (RefuseKeys || inner.SoftwareControl != 1) { echo[1] = 0xFF; echo[2] = 0xAA; }
                _replies.Writer.TryWrite(echo);
                return;
            }
            await inner.WriteAsync(report, ct);
            if (p[1] == 0x67 && p[2] == 2) return;
            var buffer = new byte[65];
            var length = await inner.ReadAsync(buffer, ct);
            _replies.Writer.TryWrite(buffer[..length]);
        }

        public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
        {
            var reply = await _replies.Reader.ReadAsync(ct);
            reply.CopyTo(buffer);
            return reply.Length;
        }

        public void Dispose() => _replies.Writer.TryComplete();
    }
}
