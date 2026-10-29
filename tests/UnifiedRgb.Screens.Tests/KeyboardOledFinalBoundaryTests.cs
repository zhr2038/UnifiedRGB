using Microsoft.Extensions.Logging.Abstractions;
using UnifiedRgb.Contracts;
using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Screens.Keyboard;
using UnifiedRgb.Screens.Media;

namespace UnifiedRgb.Screens.Tests;

/// <summary>
/// The final acceptance boundary of keyboard preparation and preview sampling (Astra P2 #2): cancellation, the elapsed budget and
/// the source's presence are checked again after the last frame is composed and encoded, so a late or partial result is never
/// returned. One-frame (PNG) and multi-frame (GIF) assets, through the real compositor and encoder on an STA. No device is opened.
/// </summary>
public sealed class KeyboardOledFinalBoundaryTests
{
    private const string Round = KeyboardDisplayAnimationEncodings.LumaRound;

    [Theory]
    [InlineData(1, false)]
    [InlineData(3, false)]
    [InlineData(1, true)]
    [InlineData(3, true)]
    public async Task Cancellation_during_the_last_frame_refuses_the_whole_result(int frames, bool sample)
    {
        using var store = await Store.WithAsync(frames);
        using var cts = new CancellationTokenSource();
        var checks = 0;
        var (result, error) = await Sta.RunAsync(() => Capture(() => Run(store, sample, 30_000, () =>
        {
            if (++checks == frames) cts.Cancel(); // after the last frame's own check, while it is composed and encoded
            return TimeSpan.Zero;
        }, cts.Token)));
        result.Should().BeNull("a result completed after cancellation is not returned");
        error.Should().BeAssignableTo<OperationCanceledException>();
        checks.Should().Be(frames, "every frame was composed; only the final acceptance refused the result");
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(3, false)]
    [InlineData(1, true)]
    [InlineData(3, true)]
    public async Task Expiry_during_the_last_frame_refuses_the_whole_result(int frames, bool sample)
    {
        using var store = await Store.WithAsync(frames);
        var checks = 0;
        var (result, error) = await Sta.RunAsync(() => Capture(() => Run(store, sample, 500,
            () => TimeSpan.FromMilliseconds(++checks <= frames ? 0 : 501), default)));
        result.Should().BeNull("a result completed after the time budget is not returned");
        error.Should().BeOfType<KeyboardOledPrepareException>().Which.Message.Should().Contain("500 ms").And.Contain("未缓存任何帧");
        checks.Should().Be(frames + 1, "the budget is checked before every frame and once more after the last");
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(3, false)]
    [InlineData(1, true)]
    [InlineData(3, true)]
    public async Task A_source_removed_before_the_result_is_accepted_is_refused(int frames, bool sample)
    {
        using var store = await Store.WithAsync(frames);
        var checks = 0;
        var removed = false;
        var (result, error) = await Sta.RunAsync(() => Capture(() => Run(store, sample, 30_000, () =>
        {
            if (++checks == frames + 1) removed = store.Assets.Delete(store.Id); // after the last frame was composed from it
            return TimeSpan.Zero;
        }, default)));
        result.Should().BeNull("pixels of a source that left the library are not returned");
        error.Should().BeOfType<KeyboardOledPrepareException>().Which.Message.Should().Contain("已从素材库移除").And.Contain("未缓存任何帧");
        removed.Should().BeTrue();
    }

    [Fact]
    public async Task Within_its_bounds_the_result_is_complete_and_the_final_check_runs_once()
    {
        using var store = await Store.WithAsync(3);
        var checks = 0;
        var (result, error) = await Sta.RunAsync(() => Capture(() => Run(store, false, 30_000, () => { checks++; return TimeSpan.Zero; }, default)));
        error.Should().BeNull();
        result.Should().Be(3);
        checks.Should().Be(4);
    }

    [Fact]
    public async Task A_cancelled_keyboard_request_completes_only_after_its_running_work_has_ended()
    {
        // The premise the Host's one-worker bound relies on: the keyboard media task tracks the actual STA work, never only its token.
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ended = 0;
        using var cts = new CancellationTokenSource();
        using var sta = new StaThread("keyboard oled contract");
        var request = sta.InvokeAsync(() =>
        {
            started.SetResult();
            release.Task.GetAwaiter().GetResult(); // an uninterruptible decode
            Volatile.Write(ref ended, 1);
            return 1;
        }, cts.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cts.Cancel();
        (await Task.WhenAny(request, Task.Delay(300))).Should().NotBeSameAs(request, "cancellation never completes a request whose work still runs");
        release.SetResult();
        (await request.WaitAsync(TimeSpan.FromSeconds(10))).Should().Be(1, "the caller decides to drop the late result");
        Volatile.Read(ref ended).Should().Be(1);
    }

    /// <summary>Prepare (frame count) or Sample (sample count) through the real preparer.</summary>
    private static int Run(Store store, bool sample, int timeoutMs, Func<TimeSpan> elapsed, CancellationToken ct) => sample
        ? KeyboardOledFramePreparer.Sample(store.Assets, store.Id, "contain", Round, 100, 8, timeoutMs, ct, elapsed).Count
        : KeyboardOledFramePreparer.Prepare(store.Assets, store.Id, "contain", Round, new KeyboardOledLimits(1 << 20, 100, timeoutMs), ct, elapsed).Count;

    private static (int? Result, Exception? Error) Capture(Func<int> work)
    {
        try
        {
            return (work(), null);
        }
        catch (Exception ex)
        {
            return (null, ex);
        }
    }

    /// <summary>A throwaway asset store holding one PNG (one frame) or a GIF of distinct frames, imported like an upload.</summary>
    private sealed class Store : IDisposable
    {
        private Store(string root, AssetStore assets, string id)
        {
            Root = root;
            Assets = assets;
            Id = id;
        }

        public string Root { get; }

        public AssetStore Assets { get; }

        public string Id { get; }

        public static async Task<Store> WithAsync(int frames)
        {
            var data = frames == 1
                ? await Images.PngAsync(Images.Solid(2, 2, 255, 255, 255))
                : Images.Gif(2, 2, [.. Enumerable.Range(0, frames).Select(i => GifFrame.Fill(0, 0, 2, 2, (byte)(1 + (i % 3))))]);
            var root = Path.Combine(Path.GetTempPath(), "urgb-screens-tests", Guid.NewGuid().ToString("N"));
            var store = new AssetStore(root, 1 << 26, 16, NullLogger.Instance);
            var import = await Sta.RunAsync(() => ImageImporter.Import(data, ImportLimits.Default));
            var id = store.Commit(AssetStore.IdOf(data), "t", import).Id;
            store.Find(id)!.FrameCount.Should().Be(frames);
            return new Store(root, store, id);
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, true);
            }
        }
    }
}
