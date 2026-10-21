using UnifiedRgb.Devices.OpenRgbSdk;

namespace UnifiedRgb.Devices.Tests;

public sealed class OpenRgbReconnectAuditTests
{
    [Fact]
    public async Task Io_failure_retries_using_monotonic_time_without_reading_wall_clock()
    {
        var time = new MonotonicOnlyTime();
        var attempts = 0;
        using var server = new FakeOpenRgbServer(4, OrgbSpec.EneDram("audit"));
        await using var adapter = new OpenRgbSdkAdapter(_ => ++attempts == 1
            ? Task.FromException<ITransport>(new IOException("first connection reset"))
            : Task.FromResult<ITransport>(server), new() { ConnectTimeout = TimeSpan.FromSeconds(1), RediscoverInterval = TimeSpan.Zero },
            time: time, delay: (duration, _) => { time.Advance(duration); return Task.CompletedTask; });
        (await adapter.DiscoverAsync(default)).Should().ContainSingle();
        attempts.Should().Be(2);
        adapter.Connections.Should().Be(1);
    }

    [Fact]
    public async Task Uncooperative_connection_has_a_deadline_and_its_late_transport_is_disposed()
    {
        var pending = new TaskCompletionSource<ITransport>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var adapter = new OpenRgbSdkAdapter(_ => pending.Task,
            new() { ConnectTimeout = TimeSpan.FromMilliseconds(50), RediscoverInterval = TimeSpan.Zero });
        var discover = () => adapter.DiscoverAsync(default);
        await discover.Should().ThrowAsync<TimeoutException>();
        using var server = new FakeOpenRgbServer(4);
        pending.SetResult(server);
        for (var i = 0; i < 100 && !server.Disposed; i++) await Task.Delay(10);
        server.Disposed.Should().BeTrue();
    }

    [Fact]
    public async Task Dispose_cancels_a_pending_connection_without_creating_a_timer()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new TaskCompletionSource<ITransport>(TaskCreationOptions.RunContinuationsAsynchronously);
        var adapter = new OpenRgbSdkAdapter(_ => { entered.SetResult(); return pending.Task; });
        var discovering = adapter.DiscoverAsync(default);
        await entered.Task;
        await adapter.DisposeAsync();
        var failed = async () => await discovering;
        await failed.Should().ThrowAsync<OperationCanceledException>();
        var again = () => adapter.DiscoverAsync(default);
        await again.Should().ThrowAsync<ObjectDisposedException>();
        using var server = new FakeOpenRgbServer(4);
        pending.SetResult(server);
    }

    private sealed class MonotonicOnlyTime : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => _ticks;
        public override DateTimeOffset GetUtcNow() => throw new InvalidOperationException("Wall clock must not be used for reconnect deadlines.");
        public void Advance(TimeSpan duration) => _ticks += (long)duration.TotalMilliseconds;
    }
}
