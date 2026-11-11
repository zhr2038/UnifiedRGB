using UnifiedRgb.Devices.AsusRog;
using UnifiedRgb.Transports.Hid;

namespace UnifiedRgb.Devices.Tests;

public sealed class RogMouseStartupRetryTests
{
    [Theory]
    [InlineData((byte)2)]
    [InlineData((byte)3)]
    public async Task Initial_mouse_timeout_closes_old_owner_and_fresh_discovery_reads_before_any_output(byte route)
    {
        var hid = new StartupHid { MouseRoute = route, MouseAsleep = true };
        await using var adapter = Adapter(hid);
        var mouse = (await adapter.DiscoverAsync(default)).Single(d => d.DeviceType == DeviceType.Mouse);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var open = () => adapter.OpenAsync(mouse.Id, default);
            var failure = (await open.Should().ThrowAsync<RogMouseStartupNotReadyException>()).Which;
            failure.ControllerId.Should().Be(mouse.Id.Value);
            failure.ReportId.Should().Be(route);
            hid.ActiveChannels.Should().Be(0, "the old session must close and join before declaring readiness waiting");
            hid.Writes.Should().OnlyContain(p => p[1] == 0xA0 || p[1] == 0x12);
            await adapter.DiscoverAsync(default);
        }
        hid.MouseAsleep = false;
        await adapter.OpenAsync(mouse.Id, default);
        adapter.WriteFrame(mouse.Id, new Rgb[][] { [new(8, 9, 10)] }).Should().BeTrue();
        hid.Writes.Last().Take(10).Should().Equal(route, 0x51, 0x28, 0, 0, 0, 73, 8, 9, 10);
        hid.OpensWhileOldOwnerLive.Should().Be(0);
    }

    [Fact]
    public async Task Malformed_static_snapshot_stays_terminal()
    {
        var hid = new StartupHid { MalformedStatic = true };
        await using var adapter = Adapter(hid);
        var mouse = (await adapter.DiscoverAsync(default)).Single(d => d.DeviceType == DeviceType.Mouse);
        var act = () => adapter.OpenAsync(mouse.Id, default);
        await act.Should().ThrowAsync<InvalidDataException>();
        hid.Writes.Should().OnlyContain(p => p[1] == 0xA0 || p[1] == 0x12);
    }

    [Fact]
    public async Task Pairing_wrong_model_never_becomes_mouse_readiness()
    {
        var hid = new StartupHid { WrongMouse = true, MouseAsleep = true };
        await using var adapter = Adapter(hid);
        var mouse = (await adapter.DiscoverAsync(default)).Single(d => d.DeviceType == DeviceType.Mouse);
        var act = () => adapter.OpenAsync(mouse.Id, default);
        await act.Should().ThrowAsync<DeviceBlockedException>();
        hid.Writes.Should().ContainSingle().Which[1].Should().Be(0xA0);
    }

    [Fact]
    public async Task Caller_cancellation_never_becomes_readiness()
    {
        var hid = new StartupHid { MouseAsleep = true };
        await using var adapter = Adapter(hid, timeout: 500);
        var mouse = (await adapter.DiscoverAsync(default)).Single(d => d.DeviceType == DeviceType.Mouse);
        using var cancel = new CancellationTokenSource();
        hid.MouseReadStarted = () => cancel.Cancel();
        var act = () => adapter.OpenAsync(mouse.Id, cancel.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task An_already_opened_receiver_cannot_reclassify_a_later_timeout_as_startup()
    {
        var hid = new StartupHid();
        await using var adapter = Adapter(hid);
        var mouse = (await adapter.DiscoverAsync(default)).Single(d => d.DeviceType == DeviceType.Mouse);
        await adapter.OpenAsync(mouse.Id, default);
        await adapter.CloseAsync(mouse.Id);
        hid.MouseAsleep = true;
        var act = () => adapter.OpenAsync(mouse.Id, default);
        await act.Should().ThrowAsync<TimeoutException>();
    }

    [Fact]
    public async Task Failed_mutation_cannot_become_initial_readiness_even_after_rediscovery()
    {
        var hid = new StartupHid();
        await using var adapter = Adapter(hid);
        var mouse = (await adapter.DiscoverAsync(default)).Single(d => d.DeviceType == DeviceType.Mouse);
        await adapter.OpenAsync(mouse.Id, default);
        hid.FailMutation = true;
        var write = () => adapter.WriteFrame(mouse.Id, new Rgb[][] { [Rgb.White] });
        write.Should().Throw<IOException>();
        await adapter.CloseAsync(mouse.Id);
        await adapter.DiscoverAsync(default);
        hid.MouseAsleep = true;
        var open = () => adapter.OpenAsync(mouse.Id, default);
        await open.Should().ThrowAsync<TimeoutException>();
    }

    [Fact]
    public async Task Bringup_sibling_prevents_automatic_receiver_replacement()
    {
        var hid = new StartupHid { MouseAsleep = true };
        await using var adapter = Adapter(hid);
        var found = await adapter.DiscoverAsync(default);
        await adapter.OpenForBringupAsync(found.Single(d => d.DeviceType == DeviceType.Unknown).Id, default);
        var open = () => adapter.OpenAsync(found.Single(d => d.DeviceType == DeviceType.Mouse).Id, default);
        await open.Should().ThrowAsync<TimeoutException>();
    }

    [Fact]
    public async Task Successful_keyboard_sibling_prevents_automatic_receiver_replacement()
    {
        var hid = new StartupHid { MouseAsleep = true };
        var key = RogReceiver.Discover(hid).Single().Key;
        await using var adapter = new RogAdapter(hid, new RogOptions
        {
            HidTimeoutMs = 30,
            Keyboards = [new() { ReceiverKey = key, ReportId = 2, LedIds = [1], ControlVerified = true }],
            Mice = [new() { ReceiverKey = key, ReportId = 3, ControlVerified = true }],
        });
        var found = await adapter.DiscoverAsync(default);
        await adapter.OpenAsync(found.Single(d => d.DeviceType == DeviceType.Keyboard).Id, default);
        var open = () => adapter.OpenAsync(found.Single(d => d.DeviceType == DeviceType.Mouse).Id, default);
        await open.Should().ThrowAsync<TimeoutException>();
    }

    private static RogAdapter Adapter(StartupHid hid, int timeout = 30)
    {
        var key = RogReceiver.Discover(hid).Single().Key;
        return new(hid, new RogOptions { HidTimeoutMs = timeout, Mice = [new() { ReceiverKey = key, ReportId = hid.MouseRoute, ControlVerified = true }] });
    }

    private sealed class StartupHid : IHidReportDevices
    {
        public byte MouseRoute { get; init; } = 3;
        public bool MouseAsleep { get; set; }
        public bool MalformedStatic { get; init; }
        public bool WrongMouse { get; init; }
        public bool FailMutation { get; set; }
        public Action? MouseReadStarted { get; set; }
        public List<byte[]> Writes { get; } = [];
        public int ActiveChannels { get; private set; }
        public int OpensWhileOldOwnerLive { get; private set; }
        public IReadOnlyList<HidCollectionInfo> Enumerate(ushort vid, ushort pid) => pid == 0x1ACE ? RogPeripheralTests.ReceiverCollections("startup-mouse") : [];
        public IHidReportChannel Open(HidCollectionInfo collection)
        {
            // A new receiver owner starts with pairing/RID1; its later RID3 channel is part of that same owner.
            if (collection.UsagePage == 0xFF02 && ActiveChannels != 0) OpensWhileOldOwnerLive++;
            ActiveChannels++;
            return new Channel(this);
        }

        private sealed class Channel(StartupHid owner) : IHidReportChannel
        {
            private byte[] _request = [];
            private bool _disposed;
            public ValueTask WriteAsync(ReadOnlyMemory<byte> report, CancellationToken ct)
            {
                ct.ThrowIfCancellationRequested();
                _request = report.ToArray(); owner.Writes.Add(_request);
                if (owner.FailMutation && _request[1] == 0x51) throw new IOException("mutation failure fixture");
                return ValueTask.CompletedTask;
            }
            public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
            {
                var reply = (byte[])_request.Clone();
                if (reply[1] == 0xA0)
                {
                    reply = new byte[64];
                    new byte[] { 1, 0xA0, 0, 2, 0, 0x25, 0x1C, 2, 4, 0x7C, 0x1D, 3, 5 }.CopyTo(reply, 0);
                    if (owner.MouseRoute == 2) { reply[5] = 0x7C; reply[6] = 0x1D; reply[8] = 5; reply[9] = 0x25; reply[10] = 0x1C; reply[12] = 4; }
                    if (owner.WrongMouse) reply[owner.MouseRoute == 2 ? 5 : 9] = 0x01;
                }
                else if (reply[1] == 0x12 && reply[2] == 3)
                {
                    owner.MouseReadStarted?.Invoke();
                    if (owner.MouseAsleep) await Task.Delay(Timeout.Infinite, ct);
                    reply = RogProtocol.MouseStaticReport(owner.MouseRoute, new(73, new(4, 5, 6)));
                    reply[1] = 0x12; reply[2] = 3;
                    if (owner.MalformedStatic) reply[6] = 255;
                }
                else if (reply[1] == 0x12 && reply[2] == 0x12) reply[5] = 1;
                reply.CopyTo(buffer); return reply.Length;
            }
            public void Dispose() { if (!_disposed) { _disposed = true; owner.ActiveChannels--; } }
        }
    }
}
