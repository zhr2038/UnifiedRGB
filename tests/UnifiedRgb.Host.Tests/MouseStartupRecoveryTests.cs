using Microsoft.Extensions.Logging.Abstractions;
using UnifiedRgb.Core.Clock;
using UnifiedRgb.Core.Engine;
using UnifiedRgb.Devices;
using UnifiedRgb.Devices.AsusRog;
using UnifiedRgb.Host.Services;
using UnifiedRgb.Transports.Hid;

namespace UnifiedRgb.Host.Tests;

/// <summary>Real ROG discovery/session/adapter over raw HID fixtures, driven through the existing guard recheck path.</summary>
public sealed class MouseStartupRecoveryTests
{
    [Fact]
    public async Task Guard_rechecks_recover_only_the_initial_mouse_snapshot_and_do_not_retry_an_unrelated_terminal_keyboard()
    {
        var hid = new MouseHid { MouseAsleep = true, BadKeyboard = true };
        var adapter = Adapter(hid);
        using var bus = new FrameBus();
        await using var host = Host(adapter, bus, new Guard());
        await host.StartAsync(default);
        var keyboardReads = hid.KeyboardReads;
        keyboardReads.Should().Be(1);
        Mouse(host).IsActive.Should().BeFalse();
        hid.ActiveMouseChannels.Should().Be(0);

        for (var cycle = 0; cycle < 4; cycle++)
        {
            await host.RecheckAsync(default);
            Mouse(host).IsActive.Should().BeFalse();
            hid.ActiveMouseChannels.Should().Be(0);
        }

        hid.MouseReads.Should().Be(5);
        hid.KeyboardReads.Should().Be(keyboardReads, "a typed mouse startup wait must not reopen another terminal controller");
        hid.Writes.Should().OnlyContain(p => p.Report[1] == 0xA0 || p.Report[1] == 0x12);

        hid.MouseAsleep = false;
        await host.RecheckAsync(default);
        Mouse(host).IsActive.Should().BeTrue();
        host.Controllers.Single(e => e.Descriptor.DeviceType == DeviceType.Keyboard).IsActive.Should().BeFalse();
        var mouseReads = hid.MouseReads;
        await host.RecheckAsync(default);
        hid.MouseReads.Should().Be(mouseReads, "the readiness retry is removed after the first successful writer open");
        hid.KeyboardReads.Should().Be(keyboardReads);
        hid.NewMouseOwnersBeforeOldClosed.Should().Be(0);
    }

    [Fact]
    public async Task Malformed_mouse_static_read_is_terminal_and_is_not_retried_by_guard_checks()
    {
        var hid = new MouseHid { MalformedMouse = true, BadKeyboard = true };
        using var bus = new FrameBus();
        await using var host = Host(Adapter(hid), bus, new Guard());
        await host.StartAsync(default);
        var writes = hid.Writes.Count;
        for (var i = 0; i < 4; i++) await host.RecheckAsync(default);
        hid.Writes.Count.Should().Be(writes);
        Mouse(host).IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task Pending_mouse_wait_never_bypasses_the_current_writer_guard()
    {
        var hid = new MouseHid { MouseAsleep = true, BadKeyboard = true };
        var guard = new Guard();
        using var bus = new FrameBus();
        await using var host = Host(Adapter(hid), bus, guard);
        await host.StartAsync(default);
        var mouseReads = hid.MouseReads;
        guard.Blocked = true;
        hid.MouseAsleep = false;
        await host.RecheckAsync(default);
        hid.MouseReads.Should().Be(mouseReads);
        Mouse(host).IsActive.Should().BeFalse();
        guard.Blocked = false;
        await host.RecheckAsync(default);
        hid.MouseReads.Should().Be(mouseReads + 1);
        Mouse(host).IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task Caller_cancelled_initial_open_does_not_leave_an_automatic_mouse_retry()
    {
        var hid = new MouseHid { MouseAsleep = true, BadKeyboard = true };
        using var cancel = new CancellationTokenSource();
        hid.MouseReadStarted = () => cancel.Cancel();
        using var bus = new FrameBus();
        await using var host = Host(Adapter(hid, timeout: 500), bus, new Guard());
        var start = () => host.StartAsync(cancel.Token);
        await start.Should().ThrowAsync<OperationCanceledException>();
        hid.MouseReadStarted = null;
        hid.MouseAsleep = false;
        var mouseReads = hid.MouseReads;
        await host.RecheckAsync(default);
        hid.MouseReads.Should().Be(mouseReads);
    }

    private static ControllerEntry Mouse(AdapterHost host) => host.Controllers.Single(e => e.Descriptor.DeviceType == DeviceType.Mouse);

    private static RogAdapter Adapter(MouseHid hid, int timeout = 30)
    {
        var receivers = RogReceiver.Discover(hid);
        return new(hid, new RogOptions
        {
            HidTimeoutMs = timeout,
            Mice = [new() { ReceiverKey = receivers.Single(r => r.Channels[3].Path.Contains("mouseA", StringComparison.Ordinal)).Key, ReportId = 3, ControlVerified = true }],
            Keyboards = [new() { ReceiverKey = receivers.Single(r => r.Channels[2].Path.Contains("keyboardB", StringComparison.Ordinal)).Key, ReportId = 2, LedIds = [1], ControlVerified = true }],
        });
    }

    private static AdapterHost Host(RogAdapter adapter, FrameBus bus, Guard guard) => new([new Factory(adapter)], null!, guard,
        bus, () => new HybridSleeper(), NullLogger<AdapterHost>.Instance, new UnifiedRgbOptions());

    private sealed class Factory(RogAdapter adapter) : IDeviceAdapterFactory
    {
        public DeviceClass Class => DeviceClass.AsusRog;
        public IDeviceAdapter Create(IServiceProvider services) => adapter;
    }

    private sealed class Guard : IWriterGuard
    {
        public bool Blocked { get; set; }
        public GuardVerdict Check(DeviceClass cls) => Blocked ? GuardVerdict.Blocked("other current writer") : GuardVerdict.Clear;
        public bool IsTakeoverEnabled(DeviceClass cls) => true;
        public Task<TakeoverResult> SetTakeoverAsync(DeviceClass cls, bool enabled, CancellationToken ct) => Task.FromResult(new TakeoverResult(cls, enabled, [], [], null));
        public Task<TakeoverResult> TakeoverAsync(DeviceClass cls, CancellationToken ct) => Task.FromResult(new TakeoverResult(cls, true, [], [], null));
        public Task RestoreAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class MouseHid : IHidReportDevices
    {
        public bool MouseAsleep { get; set; }
        public bool MalformedMouse { get; init; }
        public bool BadKeyboard { get; init; }
        public Action? MouseReadStarted { get; set; }
        public List<(string Peer, byte[] Report)> Writes { get; } = [];
        public int MouseReads { get; private set; }
        public int KeyboardReads { get; private set; }
        public int ActiveMouseChannels { get; private set; }
        public int NewMouseOwnersBeforeOldClosed { get; private set; }
        public IReadOnlyList<HidCollectionInfo> Enumerate(ushort vid, ushort pid) => pid == 0x1ACE
            ? new[] { "mouseA", "keyboardB" }.SelectMany(peer => new byte[] { 1, 2, 3 }.Select(rid => Collection(peer, rid))).ToArray() : [];
        private static HidCollectionInfo Collection(string peer, byte rid) => new(
            $@"\\?\hid#vid_0b05&pid_1ace&mi_02&col0{rid}#8&{peer}&0&000{rid - 1}#{{00111111-2222-3333-4444-555555555555}}",
            0x0B05, 0x1ACE, 2, rid switch { 1 => (ushort)0xFF02, 2 => (ushort)0xFF00, _ => (ushort)0xFF01 }, 1, 64, 64, 0);
        public IHidReportChannel Open(HidCollectionInfo collection)
        {
            var mouse = collection.Path.Contains("mouseA", StringComparison.Ordinal);
            if (mouse)
            {
                if (collection.UsagePage == 0xFF02 && ActiveMouseChannels > 0) NewMouseOwnersBeforeOldClosed++;
                ActiveMouseChannels++;
            }
            return new Channel(this, mouse ? "mouseA" : "keyboardB");
        }

        private sealed class Channel(MouseHid owner, string peer) : IHidReportChannel
        {
            private byte[] _request = [];
            private bool _closed;
            public ValueTask WriteAsync(ReadOnlyMemory<byte> report, CancellationToken ct)
            {
                ct.ThrowIfCancellationRequested();
                _request = report.ToArray(); owner.Writes.Add((peer, _request));
                return ValueTask.CompletedTask;
            }
            public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
            {
                var reply = (byte[])_request.Clone();
                if (reply[1] == 0xA0)
                {
                    reply = new byte[64];
                    new byte[] { 1, 0xA0, 0, 2, 0, 0x25, 0x1C, 2, 4, 0x7C, 0x1D, 3, 5 }.CopyTo(reply, 0);
                }
                else if (reply[1] == 0x12 && reply[2] == 3)
                {
                    owner.MouseReads++;
                    owner.MouseReadStarted?.Invoke();
                    if (owner.MouseAsleep) await Task.Delay(Timeout.Infinite, ct);
                    reply = RogProtocol.MouseStaticReport(3, new(81, Rgb.Black)); reply[1] = 0x12; reply[2] = 3;
                    if (owner.MalformedMouse) reply[6] = 255;
                }
                else if (reply[1] == 0x12 && reply[2] == 0x12)
                {
                    owner.KeyboardReads++;
                    if (!owner.BadKeyboard) reply[5] = 1;
                }
                reply.CopyTo(buffer); return reply.Length;
            }
            public void Dispose() { if (!_closed) { _closed = true; if (peer == "mouseA") owner.ActiveMouseChannels--; } }
        }
    }
}
