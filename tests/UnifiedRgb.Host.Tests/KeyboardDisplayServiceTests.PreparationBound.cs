using System.Diagnostics;
using UnifiedRgb.Contracts;
using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Host.Services;
using UnifiedRgb.Screens.Keyboard;
using UnifiedRgb.Screens.Runtime;

namespace UnifiedRgb.Host.Tests;

// The one-worker bound of keyboard preparation and preview (Astra P2 #1) and the preview's final acceptance (P2 #2), on the actual
// KeyboardDisplayAnimation and the stock owner. Each media request runs on its own thread that blocks uninterruptibly until the
// test releases it, and its task completes only after that work has ended (the StaThread contract), so the counters observe actual
// live work, never only a cancelled token.
public sealed partial class KeyboardDisplayServiceTests
{
    private static readonly TimeSpan Refusal = TimeSpan.FromSeconds(3);

    [Fact]
    public async Task Eight_alternating_saves_never_run_a_second_unfinished_preparation_and_only_the_latest_binding_is_published()
    {
        var media = new HeldMedia();
        var screens = KeyboardAnimationScreens.Create(media.Source);
        using var animation = new KeyboardDisplayAnimation(Admitted(), _directory, media, new FakeSharedTimeline(), new ManualTimeProvider());
        var status = Fill(0x99);
        try
        {
            for (var i = 0; i < 8; i++)
                (await animation.SaveAsync(Enable(i % 2 == 0 ? AssetA : AssetB), screens, true, false, default)).State
                    .Should().Be(KeyboardDisplayAnimationStates.Preparing);
            await media.StartedAsync(1);
            await Task.Delay(300); // time for any further, wrongly admitted decoder to start
            media.MaxActive.Should().Be(1, "a cancelled but unfinished preparation keeps the one worker");
            (media.Started, media.Active).Should().Be((1, 1));
            media.Calls[0].Token.IsCancellationRequested.Should().BeTrue("the superseded generation is cancelled");
            var waiting = animation.Status(true, false);
            (waiting.State, waiting.Settings.AssetId).Should().Be((KeyboardDisplayAnimationStates.Preparing, AssetB));
            waiting.Detail.Should().Contain("已取消").And.Contain("等待其底层解码结束");
            animation.Select(status, true, false).Should().BeSameAs(status, "the owner shows status without waiting for the worker");

            media.Calls[0].Release(); // the uninterruptible decode ends on its own; its late frames of A are dropped
            await media.StartedAsync(2);
            media.Calls[0].Ended.Should().BeTrue();
            (media.Calls[1].Kind, media.Calls[1].AssetId).Should().Be(("prepare", AssetB), "only the latest binding was kept; the six in between never start");
            await Task.Delay(100);
            animation.Status(true, false).State.Should().Be(KeyboardDisplayAnimationStates.Preparing);
            animation.Select(status, true, false).Should().BeSameAs(status, "the retired binding's late result never plays");

            media.Calls[1].Release();
            await UntilAsync(() => animation.Status(true, false).State == KeyboardDisplayAnimationStates.Ready, "binding B prepared");
            animation.Select(status, true, false).Should().Equal(Fill(0x44));
            (media.Started, media.Active, media.MaxActive).Should().Be((2, 0, 1));
        }
        finally { media.ReleaseAll(); }
    }

    [Fact]
    public async Task Preview_and_preparation_share_the_one_live_worker_and_a_busy_refusal_starts_nothing()
    {
        var media = new HeldMedia();
        var screens = KeyboardAnimationScreens.Create(media.Source);
        using var animation = new KeyboardDisplayAnimation(Admitted(), _directory, media, new FakeSharedTimeline(), new ManualTimeProvider());
        var request = new KeyboardDisplayAnimationPreviewRequestDto(AssetB, SharedTimelineId, "contain", KeyboardDisplayAnimationEncodings.LumaRound);
        try
        {
            await animation.SaveAsync(Enable(AssetA), screens, true, false, default);
            await media.StartedAsync(1);
            (await FluentActions.Awaiting(() => animation.PreviewAsync(request, screens, default).WaitAsync(Refusal))
                .Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("预览未开始");
            (media.Started, media.MaxActive).Should().Be((1, 1), "a preview never starts beside a running preparation");
            media.Calls[0].Release();
            await UntilAsync(() => animation.Status(true, false).State == KeyboardDisplayAnimationStates.Ready, "binding A prepared");

            var preview = animation.PreviewAsync(request, screens, default);
            await media.StartedAsync(2);
            media.Calls[1].Kind.Should().Be("sample");
            (await FluentActions.Awaiting(() => animation.PreviewAsync(request, screens, default).WaitAsync(Refusal))
                .Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Be("上一个键盘预览仍在生成");
            await animation.SaveAsync(Enable(AssetB), screens, true, false, default);
            await Task.Delay(300);
            media.Started.Should().Be(2, "the new binding waits for the running preview instead of starting a second decoder");
            var waiting = animation.Status(true, false);
            waiting.State.Should().Be(KeyboardDisplayAnimationStates.Preparing);
            waiting.Detail.Should().Contain("键盘预览正在生成");

            media.Calls[1].Release();
            (await preview.WaitAsync(TimeSpan.FromSeconds(10))).Frames.Should().HaveCount(3);
            await media.StartedAsync(3);
            (media.Calls[2].Kind, media.Calls[2].AssetId).Should().Be(("prepare", AssetB));
            media.Calls[2].Release();
            await UntilAsync(() => animation.Status(true, false).State == KeyboardDisplayAnimationStates.Ready, "binding B prepared");
            (media.Started, media.Active, media.MaxActive).Should().Be((3, 0, 1));
        }
        finally { media.ReleaseAll(); }
    }

    [Fact]
    public async Task Dispose_cancels_without_waiting_and_the_unfinished_work_retires_on_its_own_without_starting_the_pending_binding()
    {
        var media = new HeldMedia();
        var screens = KeyboardAnimationScreens.Create(media.Source);
        var animation = new KeyboardDisplayAnimation(Admitted(), _directory, media, new FakeSharedTimeline(), new ManualTimeProvider());
        var status = Fill(0x99);
        try
        {
            await animation.SaveAsync(Enable(AssetA), screens, true, false, default);
            await media.StartedAsync(1);
            await animation.SaveAsync(Enable(AssetB), screens, true, false, default);
            await Task.Delay(300);
            media.Started.Should().Be(1, "binding B is pending behind the cancelled, unfinished preparation of A");

            var watch = Stopwatch.StartNew();
            animation.Dispose();
            watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1), "dispose cancels; it never waits for a decode");
            media.Calls.Should().OnlyContain(c => c.Token.IsCancellationRequested);
            media.Active.Should().Be(1, "a running decode is not killed; it retires when its own work ends");
            media.Calls[0].Release();
            await UntilAsync(() => media.Active == 0 && media.Calls[0].Ended, "the cancelled work ended");
            await Task.Delay(200);
            (media.Started, media.MaxActive).Should().Be((1, 1), "nothing starts after dispose");
            animation.Select(status, true, false).Should().BeSameAs(status);
            await FluentActions.Awaiting(() => animation.PreviewAsync(new(AssetA, SharedTimelineId, "contain", KeyboardDisplayAnimationEncodings.LumaRound),
                screens, default)).Should().ThrowAsync<ObjectDisposedException>();
            media.Started.Should().Be(1);
        }
        finally { media.ReleaseAll(); animation.Dispose(); }
    }

    [Fact]
    public async Task A_preview_cancelled_while_its_last_frame_is_encoded_is_not_accepted_and_frees_the_worker()
    {
        var media = new HeldMedia();
        var screens = KeyboardAnimationScreens.Create(media.Source);
        using var animation = new KeyboardDisplayAnimation(Admitted(), _directory, media, new FakeSharedTimeline(), new ManualTimeProvider());
        var request = new KeyboardDisplayAnimationPreviewRequestDto(AssetA, SharedTimelineId, "contain", KeyboardDisplayAnimationEncodings.LumaRound);
        using var cts = new CancellationTokenSource();
        media.OnEnd = _ => cts.Cancel(); // the request is cancelled after the last frame, just before its samples are returned
        try
        {
            var cancelled = animation.PreviewAsync(request, screens, cts.Token);
            await media.StartedAsync(1);
            media.Calls[0].Release();
            await FluentActions.Awaiting(() => cancelled.WaitAsync(TimeSpan.FromSeconds(10))).Should().ThrowAsync<OperationCanceledException>(
                "a result completed after cancellation is not accepted");
            media.Calls[0].Ended.Should().BeTrue();

            media.OnEnd = null;
            var next = animation.PreviewAsync(request, screens, default);
            await media.StartedAsync(2);
            media.Calls[1].Release();
            (await next.WaitAsync(TimeSpan.FromSeconds(10))).Frames.Select(f => Convert.FromBase64String(f.PackedBase64))
                .Should().BeEquivalentTo([Fill(0x11), Fill(0x22), Fill(0x33)], o => o.WithStrictOrdering());
            (media.Active, media.MaxActive).Should().Be((0, 1));
        }
        finally { media.ReleaseAll(); }
    }

    [Fact]
    public async Task Rapid_rebinding_on_the_stock_owner_prepares_one_binding_at_a_time_and_uploads_only_status_then_the_latest_frames()
    {
        var rig = new AnimationRig(this);
        var held = new List<TaskCompletionSource>();
        rig.Media.Hold = _ => // ignores its token like a slow decode; the media task ends only when the work does
        {
            var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (held) held.Add(hold);
            return hold.Task;
        };
        using var service = rig.Make(Admitted());
        await service.StartAsync(default);
        try
        {
            await WaitAsync(service, "Running");
            for (var i = 0; i < 8; i++) await service.SetKeyboardDisplayAnimationAsync(Enable(i % 2 == 0 ? AssetA : AssetB));
            await UntilAsync(() => rig.Media.Prepares >= 1, "first preparation");
            await Task.Delay(300);
            rig.Media.Prepares.Should().Be(1, "the cancelled, unfinished preparation still owns the one worker");
            var waiting = Animation(service);
            (waiting.State, waiting.Settings.AssetId).Should().Be((KeyboardDisplayAnimationStates.Preparing, AssetB));
            waiting.Detail.Should().Contain("等待其底层解码结束");
            (await service.GetKeyboardDisplayStatusAsync()).State.Should().Be("Running", "the owner keeps running while the worker is busy");

            lock (held) held[0].SetResult();
            await UntilAsync(() => rig.Media.Prepares == 2, "the latest binding");
            rig.Media.LastRequest!.Value.Asset.Should().Be(AssetB);
            await Task.Delay(200);
            Enumerable.Range(0, rig.Factory.Count).Select(rig.Factory.Image).Where(i => Fills.Any(f => f.AsSpan().SequenceEqual(i))).Should()
                .BeEmpty("the retired binding's late result never plays");
            lock (held) held[1].SetResult();
            await UntilAsync(() => rig.Factory.Count >= 2 && rig.Factory.Image(rig.Factory.Count - 1).AsSpan().SequenceEqual(Fill(0x44)), "binding B plays");
            (rig.Media.Prepares, rig.Media.Completed, rig.Factory.Opens).Should().Be((2, 2, 1), "no extra preparation or device open");
            rig.Screens.Mutations.Should().Be(0);
        }
        finally
        {
            lock (held) foreach (var hold in held) hold.TrySetResult();
            await service.StopAsync(default);
        }
    }

    /// <summary>
    /// Keyboard media whose every request runs on its own STA thread and blocks, without looking at its token, until the test
    /// releases it. <see cref="Active"/> counts requests whose work has not ended; a request's task completes only after its work
    /// has ended, like <c>StaThread.InvokeAsync</c> under the real keyboard media. Scene and assets come from <see cref="Source"/>.
    /// </summary>
    private sealed class HeldMedia : IKeyboardOledMedia
    {
        private readonly List<HeldCall> _calls = [];
        private int _active, _maxActive;

        public FakeKeyboardOledMedia Source { get; } = new() { Scene = AnimationRig.BothScene };
        public Action<HeldCall>? OnEnd { get; set; }
        public int Started { get { lock (_calls) return _calls.Count; } }
        public int Active => Volatile.Read(ref _active);
        public int MaxActive => Volatile.Read(ref _maxActive);
        public IReadOnlyList<HeldCall> Calls { get { lock (_calls) return [.. _calls]; } }

        public ScreenSceneDto? CurrentReferences() => Source.Scene;

        public Task<KeyboardOledFrames> PrepareKeyboardOledAsync(string assetId, string fit, string encoding, KeyboardOledLimits limits, CancellationToken ct) =>
            Run("prepare", assetId, () => new KeyboardOledFrames(assetId, fit, encoding, 3000, 3, [0, 1000, 2000], FakeKeyboardOledMedia.Bytes(assetId)), ct);

        public Task<IReadOnlyList<KeyboardOledSample>> SampleKeyboardOledAsync(string assetId, string fit, string encoding, int gridFloorMs, int count,
            int timeoutMs, CancellationToken ct) =>
            Run<IReadOnlyList<KeyboardOledSample>>("sample", assetId, () =>
                [.. FakeKeyboardOledMedia.Bytes(assetId).Take(count).Select((packed, i) => new KeyboardOledSample(i * 1000, packed))], ct);

        public Task StartedAsync(int count) => UntilAsync(() => Started >= count, count + " started media requests");

        public void ReleaseAll() { foreach (var call in Calls) call.Release(); }

        private Task<T> Run<T>(string kind, string assetId, Func<T> result, CancellationToken ct)
        {
            var call = new HeldCall(kind, assetId, ct);
            var active = Interlocked.Increment(ref _active);
            for (var seen = MaxActive; active > seen; seen = MaxActive)
                if (Interlocked.CompareExchange(ref _maxActive, active, seen) == seen) break;
            lock (_calls) _calls.Add(call);
            var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                try
                {
                    call.WaitForRelease(); // uninterruptible: cancellation is not observed while the "decode" runs
                    OnEnd?.Invoke(call);
                    var value = result();
                    call.End();
                    Interlocked.Decrement(ref _active);
                    done.SetResult(value);
                }
                catch (Exception ex)
                {
                    call.End();
                    Interlocked.Decrement(ref _active);
                    done.SetException(ex);
                }
            }) { IsBackground = true, Name = "held keyboard media" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            return done.Task;
        }
    }

    private sealed class HeldCall(string kind, string assetId, CancellationToken token)
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _ended;
        public string Kind { get; } = kind;
        public string AssetId { get; } = assetId;
        public CancellationToken Token { get; } = token;
        public bool Ended => Volatile.Read(ref _ended) == 1;
        public void Release() => _release.TrySetResult();
        internal void WaitForRelease() => _release.Task.Wait();
        internal void End() => Volatile.Write(ref _ended, 1);
    }
}
