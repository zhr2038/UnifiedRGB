using FluentAssertions;
using UnifiedRgb.Host.Services;
using UnifiedRgb.Transports.Process;

namespace UnifiedRgb.Host.Tests;

public sealed class ProcessSnapshotCachingTests
{
    [Fact]
    public void CachedSystemSnapshot_caches_processes_and_services_for_1s_and_invalidates_on_demand()
    {
        var time = new MutableTimeProvider(new DateTimeOffset(2026, 11, 17, 2, 0, 0, TimeSpan.Zero));
        var inner = new CountingSnapshot();
        var cached = new CachedSystemSnapshot(inner, TimeSpan.FromSeconds(1), time);

        cached.Processes();
        cached.Processes();
        cached.Services();
        cached.Services();
        inner.ProcessCalls.Should().Be(1);
        inner.ServiceCalls.Should().Be(1);

        time.Advance(TimeSpan.FromMilliseconds(900));
        cached.Processes();
        cached.Services();
        inner.ProcessCalls.Should().Be(1);
        inner.ServiceCalls.Should().Be(1);

        time.Advance(TimeSpan.FromMilliseconds(150));
        cached.Processes();
        cached.Services();
        inner.ProcessCalls.Should().Be(2);
        inner.ServiceCalls.Should().Be(2);

        cached.Invalidate();
        cached.Processes();
        cached.Services();
        inner.ProcessCalls.Should().Be(3);
        inner.ServiceCalls.Should().Be(3);
    }

    [Fact]
    public async Task WriterGuard_invalidates_cached_snapshot_on_takeover_and_restore()
    {
        var time = new MutableTimeProvider(new DateTimeOffset(2026, 11, 17, 2, 0, 0, TimeSpan.Zero));
        var counting = new CountingSnapshot();
        var cached = new CachedSystemSnapshot(counting, TimeSpan.FromSeconds(1), time);
        var scm = new FakeSystem().Service("LConnectService");
        var tempTakeover = Path.Combine(Path.GetTempPath(), $"urgb-takeover-{Guid.NewGuid():N}.json");
        try
        {
            using var guard = new WriterGuard(new ProcessScanner(cached), scm, new TakeoverStore(tempTakeover));

            guard.Check(UnifiedRgb.Devices.DeviceClass.LianLi);
            guard.Check(UnifiedRgb.Devices.DeviceClass.LianLi);
            counting.ProcessCalls.Should().Be(1);
            counting.ServiceCalls.Should().Be(1);

            // SetTakeoverAsync mutates SCM / processes and must invalidate the cache so the very next Check() rescans immediately.
            await guard.SetTakeoverAsync(UnifiedRgb.Devices.DeviceClass.LianLi, false, CancellationToken.None);
            guard.Check(UnifiedRgb.Devices.DeviceClass.LianLi);
            counting.ServiceCalls.Should().Be(2);

            await guard.RestoreAsync(CancellationToken.None);
            guard.Check(UnifiedRgb.Devices.DeviceClass.LianLi);
            counting.ServiceCalls.Should().Be(3);
        }
        finally
        {
            if (File.Exists(tempTakeover)) File.Delete(tempTakeover);
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        private long _timestamp;

        public void Advance(TimeSpan delta)
        {
            _now += delta;
            _timestamp += (long)(delta.TotalSeconds * TimestampFrequency);
        }

        public override DateTimeOffset GetUtcNow() => _now;
        public override long GetTimestamp() => _timestamp;
    }

    private sealed class CountingSnapshot : ISystemSnapshot
    {
        public int ProcessCalls { get; private set; }
        public int ServiceCalls { get; private set; }

        public IReadOnlyList<ProcessInfo> Processes()
        {
            ProcessCalls++;
            return [new ProcessInfo(101, "Dummy")];
        }

        public IReadOnlyList<ServiceInfo> Services()
        {
            ServiceCalls++;
            return [new ServiceInfo("LConnectService", Running: false, StartType: "Manual")];
        }
    }
}
