using FluentAssertions;
using UnifiedRgb.Host.Services;
using Xunit;

namespace UnifiedRgb.Host.Tests;

public sealed class CodexTaskViewProviderTests
{
    [Fact]
    public async Task Disabled_feature_reads_no_task_store_and_keeps_quota_pixels()
    {
        var clock = new TaskClock(); var reader = new TaskReader(clock); var display = new CodexTaskDisplay(clock);
        var quota = new byte[6656];
        using var provider = new CodexTaskViewProvider(reader, display, () => quota, () => false, clock);
        await provider.RefreshAsync(default);
        reader.Calls.Should().Be(0); provider.Pixels.Should().BeSameAs(quota);
    }

    [Fact]
    public async Task Browse_uses_cache_without_store_IO_then_worker_renders_and_polls_every_five_seconds()
    {
        var clock = new TaskClock(); var reader = new TaskReader(clock); var display = new CodexTaskDisplay(clock);
        var quota = new byte[6656];
        using var provider = new CodexTaskViewProvider(reader, display, () => quota, () => true, clock);
        await provider.RefreshAsync(default);
        provider.Pixels.Should().BeSameAs(quota); reader.Calls.Should().Be(1);
        provider.Browse(1); reader.Calls.Should().Be(1);
        await provider.RefreshAsync(default);
        provider.Pixels.Should().HaveCount(6656).And.NotBeSameAs(quota);
        reader.Calls.Should().Be(1);
        clock.Advance(TimeSpan.FromSeconds(4)); await provider.RefreshAsync(default); reader.Calls.Should().Be(1);
        clock.Advance(TimeSpan.FromSeconds(1)); await provider.RefreshAsync(default); reader.Calls.Should().Be(2);
    }

    [Fact]
    public async Task Store_failure_is_unavailable_not_no_tasks_and_disabling_returns_to_quota()
    {
        var clock = new TaskClock(); var reader = new TaskReader(clock) { Fail = true };
        var display = new CodexTaskDisplay(clock); var enabled = true; var quota = new byte[6656];
        using var provider = new CodexTaskViewProvider(reader, display, () => quota, () => enabled, clock);
        provider.Browse(1); await provider.RefreshAsync(default);
        display.View(clock.GetUtcNow()).Kind.Should().Be(CodexTaskViewKind.Unavailable);
        enabled = false; await provider.RefreshAsync(default);
        display.IsTaskView.Should().BeFalse(); provider.Pixels.Should().BeSameAs(quota);
        provider.Dispose(); provider.Browse(1); display.IsTaskView.Should().BeFalse();
    }

    private sealed class TaskReader(TaskClock clock) : ICodexTaskReader
    {
        public int Calls { get; private set; }
        public bool Fail { get; init; }
        public Task<CodexTaskSnapshot> ReadAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); Calls++;
            if (Fail) throw new IOException("Fake-only store unavailable");
            return Task.FromResult(new CodexTaskSnapshot("Ready", [new("one", "正在开发的任务", clock.GetUtcNow() - TimeSpan.FromMinutes(5))], clock.GetUtcNow()));
        }
    }

    private sealed class TaskClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 2, 10, 0, 0, TimeSpan.Zero);
        private long _ticks;
        public override DateTimeOffset GetUtcNow() => _now;
        public override long GetTimestamp() => _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public void Advance(TimeSpan elapsed) { _now += elapsed; _ticks += elapsed.Ticks; }
    }
}
