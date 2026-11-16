using UnifiedRgb.Screens.Media;

namespace UnifiedRgb.Screens.Tests;

public sealed class MediaLifecycleAuditTests
{
    [Fact]
    public async Task STA_rejects_overflow_without_blocking_and_does_not_run_cancelled_queued_work()
    {
        using var sta = new StaThread("media queue test");
        using var release = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = sta.InvokeAsync(() => { entered.TrySetResult(); release.Wait(TimeSpan.FromSeconds(10)); return 1; }, default);
        Task<int>[] pending;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            pending = Enumerable.Range(0, 32).Select(_ => sta.InvokeAsync<int>(() =>
                throw new InvalidOperationException("cancelled work must not run"), cancellation.Token)).ToArray();
            Action overflow = () => _ = sta.InvokeAsync(() => 0, default);
            overflow.Should().Throw<InvalidOperationException>().WithMessage("*队列已满*");
            cancellation.Cancel();
        }
        finally { release.Set(); }
        (await first).Should().Be(1);
        await FluentActions.Awaiting(() => Task.WhenAll(pending)).Should().ThrowAsync<OperationCanceledException>();
        (await sta.InvokeAsync(() => 2, default)).Should().Be(2);
    }

    [Fact]
    public void Scratch_cleanup_cannot_hide_the_original_decode_failure()
    {
        var root = Path.Combine(Path.GetTempPath(), "urgb-audit-scratch", Guid.NewGuid().ToString("N"));
        FileStream? scanner = null;
        string? used = null;
        var failure = new InvalidDataException("original decode failure");
        try
        {
            var import = () => Mp4Importer.WithInput<ImportResult>([1, 2, 3], root, path =>
            {
                used = path;
                File.ReadAllBytes(path).Should().Equal(1, 2, 3);
                scanner = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                throw failure;
            });
            import.Should().Throw<InvalidDataException>().Which.Should().BeSameAs(failure);
            used.Should().StartWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar);
            File.Exists(used).Should().BeTrue("a scanner without delete sharing keeps this failed scratch file for later cleanup");
        }
        finally { scanner?.Dispose(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Sta_dispose_from_worker_releases_the_queue_when_the_worker_eventually_exits()
    {
        var sta = new StaThread("audit-self-dispose");
        await sta.InvokeAsync(() => { sta.Dispose(); return 1; }, default);
        sta.Dispose(); // idempotent, including the caller arriving before worker finally
        var call = () => sta.InvokeAsync(() => 2, default);
        await call.Should().ThrowAsync<ObjectDisposedException>();
    }
}
