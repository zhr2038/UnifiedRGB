using UnifiedRgb.Devices;
using UnifiedRgb.Devices.LianLi;
using UnifiedRgb.Transports.WinUsb;

namespace UnifiedRgb.Devices.Tests;

#pragma warning disable VSTHRD003 // Test-owned barriers and tasks select the disposal/read interleaving.
public sealed class RfSenderLifetimeTests
{
    [Fact]
    public async Task Dispose_waits_for_an_inflight_read_and_retired_ticks_send_nothing()
    {
        var raw = new BlockingRead();
        var sender = new RfSender(new UsbDeviceInfo("fake-only", 0, 0, "test"), raw, new DeviceState(), 8);
        var reading = Task.Run(sender.Tick);
        await raw.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var closing = Task.Run(sender.Dispose);
        try
        {
            await Task.Delay(40);
            raw.Disposed.Should().BeFalse("the read still owns the transport");
            closing.IsCompleted.Should().BeFalse();
        }
        finally { raw.Release.TrySetResult(); }
        await Task.WhenAll(reading, closing).WaitAsync(TimeSpan.FromSeconds(3));
        raw.Disposed.Should().BeTrue();
        raw.DisposedDuringRead.Should().BeFalse();
        var sent = raw.Sends;
        sender.Tick(); sender.Dispose();
        raw.Sends.Should().Be(sent);
        var lateRead = () => sender.ReadMaster();
        lateRead.Should().Throw<ObjectDisposedException>();
    }

    private sealed class BlockingRead : ITransport, IDisposable
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _reading;
        public volatile bool Disposed;
        public bool DisposedDuringRead { get; private set; }
        public int Sends;
        public ValueTask SendAsync(ReadOnlyMemory<byte> frame, CancellationToken ct) { Interlocked.Increment(ref Sends); return ValueTask.CompletedTask; }
        public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct) => new(ReadAsync());
        private async Task<int> ReadAsync()
        {
            Interlocked.Exchange(ref _reading, 1); Entered.TrySetResult();
            try { await Release.Task.WaitAsync(TimeSpan.FromSeconds(5)); return 0; }
            finally { Interlocked.Exchange(ref _reading, 0); }
        }
        public void Dispose() { DisposedDuringRead = Volatile.Read(ref _reading) != 0; Disposed = true; }
    }
}
