using System.Buffers.Binary;
using System.Reflection;
using System.Text;
using System.Text.Json;
using UnifiedRgb.Contracts;
using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Devices.AsusRog;
using UnifiedRgb.Host.Services;
using UnifiedRgb.Screens.Keyboard;
using UnifiedRgb.Screens.Runtime;
using UnifiedRgb.Transports.Hid;

namespace UnifiedRgb.Host.Tests;

// The local keyboard OLED animation on the actual stock owner (KeyboardDisplayService + RogWiredOledSession) over the existing
// fake wired channel, a recording tap, fake prepared media and a fake shared timeline. The deterministic animation clock only
// decides the cadence gate; frame selection comes from the shared timeline position alone.
public sealed partial class KeyboardDisplayServiceTests
{
    private const string Interval2s = "最多每 2 秒更新一次（不是帧率）";

    [Fact]
    public async Task Both_gates_off_keep_the_status_upload_sequence_identical_and_never_read_the_timeline_or_prepare()
    {
        var baseline = await RunStatusOnlyAsync(null, admitted: false);
        baseline.Images.Should().ContainSingle();
        File.WriteAllBytes(AnimationPath(), AnimationFile(enabled: true));
        var savedOn = File.ReadAllBytes(AnimationPath());
        var notAdmitted = await RunStatusOnlyAsync(new(), admitted: false);
        File.ReadAllBytes(AnimationPath()).Should().Equal(savedOn, "an unadmitted Host never rewrites the saved preference");
        File.Delete(AnimationPath());
        var preferenceOff = await RunStatusOnlyAsync(new(), admitted: true);
        File.Exists(AnimationPath()).Should().BeFalse("reading status never creates the sidecar");

        foreach (var run in new[] { notAdmitted, preferenceOff })
        {
            run.Writes.Should().Equal(baseline.Writes, "the owner's HID requests are unchanged with either gate off");
            run.Images.Should().Equal(baseline.Images, (a, b) => a.AsSpan().SequenceEqual(b));
            run.Detail.Should().Be(baseline.Detail);
            run.Timeline!.Reads.Should().Be(0);
            (run.Media!.Prepares, run.Media.Samples, run.Media.References).Should().Be((0, 0, 0));
        }
        notAdmitted.Status!.State.Should().Be(KeyboardDisplayAnimationStates.NotAdmitted);
        notAdmitted.Status.Settings.Enabled.Should().BeTrue("the saved preference is preserved");
        notAdmitted.Status.Choices.Should().BeEmpty();
        preferenceOff.Status!.State.Should().Be(KeyboardDisplayAnimationStates.Off);
        preferenceOff.Status.Choices.Should().ContainSingle();
    }

    [Fact]
    public async Task Frames_follow_only_the_shared_timeline_and_the_cadence_gate_holds_without_uploads_or_spinning()
    {
        var rig = new AnimationRig(this);
        using var service = rig.Make(Admitted());
        await service.StartAsync(default);
        try
        {
            await rig.ReadyAsync(service);
            var status = rig.Factory.Image(0);
            status.Should().Equal(service.ArtworkPixels.ToArray());
            rig.Timeline.At(1500);
            await service.SetKeyboardDisplayAnimationAsync(Enable(AssetA));
            await rig.ImagesAsync(2);
            rig.Factory.Image(1).Should().Equal(Fill(0x22), "position 1500 is inside the second segment");
            await UntilAsync(() => Animation(service).Acknowledged == 1, "first animation ACK");
            var playing = Animation(service);
            playing.State.Should().Be(KeyboardDisplayAnimationStates.Playing);
            playing.Detail.Should().Contain(Interval2s);
            (playing.Selected, playing.CachedFrames, playing.SourceFrames, playing.CacheBytes).Should().Be((1L, 3, 3, 3L * 6656));
            (await service.GetKeyboardDisplayStatusAsync()).Detail.Should().Contain("键盘动画画面已获有线通信确认");

            // The gate is closed: the timeline moves and wall time passes, but nothing is uploaded and the lease is not woken.
            var begins = rig.Factory.Io.BeginPackets;
            rig.Timeline.At(2500); await Task.Delay(300);
            rig.Timeline.At(100); await Task.Delay(300);
            rig.Factory.Count.Should().Be(2);
            rig.Factory.Io.BeginPackets.Should().Be(begins);
            Animation(service).Skipped.Should().Be(2, "two different frames were due while the gate was closed");

            rig.Time.Advance(TimeSpan.FromMilliseconds(2000));
            await rig.ImagesAsync(3);
            rig.Factory.Image(2).Should().Equal(Fill(0x11), "the frame for the CURRENT shared position, not the skipped ones");
            rig.Timeline.At((3000L * 1_000_000) + 2100); // a seek far into the loop wraps by the asset duration
            rig.Time.Advance(TimeSpan.FromMilliseconds(1999));
            await Task.Delay(300);
            rig.Factory.Count.Should().Be(3);
            rig.Time.Advance(TimeSpan.FromMilliseconds(1));
            await rig.ImagesAsync(4);
            rig.Factory.Image(3).Should().Equal(Fill(0x33));
            await UntilAsync(() => service.GetKeyboardDisplayStatusAsync().GetAwaiter().GetResult().FramesAcknowledged == 4 + rig.Factory.EntryLogos, "fourth content ACK plus entry Logo");
            (Animation(service).Selected, Animation(service).Acknowledged).Should().Be((3L, 3L));
            rig.Factory.Opens.Should().Be(1);
            rig.Media.Prepares.Should().Be(1);
        }
        finally { await service.StopAsync(default); }
        rig.Factory.Io.Brightness.Should().Be(25, "stop restores the original");
        rig.Screens.Mutations.Should().Be(0);
    }

    [Fact]
    public async Task A_longer_saved_interval_keeps_the_gate_closed_for_that_interval()
    {
        var rig = new AnimationRig(this);
        using var service = rig.Make(Admitted());
        await service.StartAsync(default);
        try
        {
            await rig.ReadyAsync(service);
            await service.SetKeyboardDisplayAnimationAsync(Enable(AssetA) with { IntervalMs = 5000 });
            await rig.ImagesAsync(2);
            rig.Timeline.At(1000);
            rig.Time.Advance(TimeSpan.FromMilliseconds(4999)); await Task.Delay(300);
            rig.Factory.Count.Should().Be(2);
            rig.Time.Advance(TimeSpan.FromMilliseconds(1));
            await rig.ImagesAsync(3);
            rig.Factory.Image(2).Should().Equal(Fill(0x22));
            Animation(service).Detail.Should().Contain("最多每 5 秒更新一次");
        }
        finally { await service.StopAsync(default); }
    }

    [Fact]
    public async Task A_blocked_shared_timeline_shows_the_status_page_and_resumes_only_at_the_gate_without_its_own_clock()
    {
        var rig = new AnimationRig(this);
        using var service = rig.Make(Admitted());
        await service.StartAsync(default);
        try
        {
            await rig.ReadyAsync(service);
            await service.SetKeyboardDisplayAnimationAsync(Enable(AssetA));
            await rig.ImagesAsync(2);
            rig.Factory.Image(1).Should().Equal(Fill(0x11));

            rig.Timeline.Block("场景包已暂停");
            await rig.ImagesAsync(3);
            rig.Factory.Image(2).Should().Equal(service.ArtworkPixels.ToArray(), "no frame is invented while the shared clock is blocked");
            var blocked = Animation(service);
            blocked.State.Should().Be(KeyboardDisplayAnimationStates.Fallback);
            blocked.Detail.Should().Contain("共享时间轴不可用（场景包已暂停）");

            // Unblocked inside the interval: re-entry still waits for the gate, so the status page stays and nothing is sent.
            rig.Timeline.At(2100);
            rig.Time.Advance(TimeSpan.FromMilliseconds(1999)); await Task.Delay(300);
            rig.Factory.Count.Should().Be(3);
            rig.Time.Advance(TimeSpan.FromMilliseconds(1));
            await rig.ImagesAsync(4);
            rig.Factory.Image(3).Should().Equal(Fill(0x33), "the frame for the shared position when the gate opened");
            await UntilAsync(() => Animation(service).State == KeyboardDisplayAnimationStates.Playing, "playing again");
            rig.Factory.Opens.Should().Be(1);
        }
        finally { await service.StopAsync(default); }
    }

    [Fact]
    public async Task A_slow_completed_upload_trips_the_breaker_once_without_any_resend_and_only_a_save_rearms_it()
    {
        var rig = new AnimationRig(this);
        using var service = rig.Make(Admitted());
        await service.StartAsync(default);
        try
        {
            await rig.ReadyAsync(service);
            var slow = 1;
            rig.Factory.FirstChunk = () => { if (Interlocked.Exchange(ref slow, 0) == 1) rig.Time.Advance(TimeSpan.FromMilliseconds(1500)); };
            await service.SetKeyboardDisplayAnimationAsync(Enable(AssetA));
            await rig.ImagesAsync(3);
            rig.Factory.Image(1).Should().Equal(Fill(0x11));
            rig.Factory.Image(2).Should().Equal(service.ArtworkPixels.ToArray(), "the next turn shows the status page");
            var tripped = Animation(service);
            tripped.State.Should().Be(KeyboardDisplayAnimationStates.BreakerTripped);
            tripped.Detail.Should().Contain("1500 ms").And.Contain("1000 ms").And.Contain("未重发或重试");
            (tripped.Selected, tripped.Acknowledged).Should().Be((1L, 1L));

            rig.Timeline.At(2500); rig.Time.Advance(TimeSpan.FromSeconds(30)); await Task.Delay(400);
            rig.Factory.Count.Should().Be(3, "a tripped breaker selects nothing and resends nothing");
            Animation(service).Selected.Should().Be(1);

            await service.SetKeyboardDisplayAnimationAsync(Enable(AssetA));
            await rig.ImagesAsync(4);
            rig.Factory.Image(3).Should().Equal(Fill(0x33), "an explicit save re-arms it at the current shared position");
            await UntilAsync(() => Animation(service).State == KeyboardDisplayAnimationStates.Playing, "re-armed animation");
            rig.Media.Prepares.Should().Be(1, "the prepared loop is reused");
        }
        finally { await service.StopAsync(default); }
    }

    [Fact]
    public async Task Disabling_the_animation_shows_status_and_disabling_the_page_restores_the_original_without_replaying_old_frames()
    {
        var rig = new AnimationRig(this);
        using var service = rig.Make(Admitted());
        await service.StartAsync(default);
        try
        {
            await rig.ReadyAsync(service);
            var on = Enable(AssetA);
            rig.Timeline.At(1200);
            await service.SetKeyboardDisplayAnimationAsync(on);
            await rig.ImagesAsync(2);
            rig.Factory.Image(1).Should().Equal(Fill(0x22));

            var off = await service.SetKeyboardDisplayAnimationAsync(on with { Enabled = false });
            off.State.Should().Be(KeyboardDisplayAnimationStates.Off);
            await rig.ImagesAsync(3);
            rig.Factory.Image(2).Should().Equal(service.ArtworkPixels.ToArray(), "disabling the animation returns to the status page");
            using (var saved = JsonDocument.Parse(File.ReadAllBytes(AnimationPath())))
            {
                saved.RootElement.GetProperty("Enabled").GetBoolean().Should().BeFalse();
                saved.RootElement.GetProperty("AssetId").GetString().Should().Be(AssetA, "the binding is kept");
            }

            rig.Time.Advance(TimeSpan.FromSeconds(5));
            await service.SetKeyboardDisplayAnimationAsync(on);
            await rig.ImagesAsync(4);
            rig.Factory.Image(3).Should().Equal(Fill(0x22));

            await service.SetKeyboardDisplayEnabledAsync(false);
            await WaitAsync(service, "Stopped");
            rig.Factory.Io.Brightness.Should().Be(25, "disabling the page restores the original ROG animation settings");
            var pageOff = Animation(service);
            pageOff.State.Should().Be(KeyboardDisplayAnimationStates.PageOff);
            pageOff.Settings.Should().Be(on, "the animation preference is untouched by the page toggle");
            var count = rig.Factory.Count;
            rig.Timeline.At(2400); rig.Time.Advance(TimeSpan.FromSeconds(5)); await Task.Delay(300);
            rig.Factory.Count.Should().Be(count, "nothing is uploaded while the page is off");

            await service.SetKeyboardDisplayEnabledAsync(true);
            await rig.ReadyAsync(service);
            await rig.ImagesAsync(count + 1);
            rig.Factory.Image(count).Should().Equal(Fill(0x33), "the new session shows the current shared position, never the frame held before");
            rig.Factory.Opens.Should().Be(2);
        }
        finally { await service.StopAsync(default); }
    }

    [Fact]
    public async Task A_new_binding_cancels_the_old_preparation_and_its_late_result_or_a_removed_assignment_never_plays()
    {
        var rig = new AnimationRig(this);
        var releaseA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Media.Hold = asset => asset == AssetA ? releaseA.Task : Task.CompletedTask; // ignores its token like a slow decode
        using var service = rig.Make(Admitted());
        await service.StartAsync(default);
        try
        {
            await rig.ReadyAsync(service);
            rig.Timeline.At(1500);
            (await service.SetKeyboardDisplayAnimationAsync(Enable(AssetA))).State.Should().Be(KeyboardDisplayAnimationStates.Preparing);
            await UntilAsync(() => rig.Media.Tokens.Count == 1, "preparation A");
            await Task.Delay(200);
            rig.Factory.Count.Should().Be(1, "nothing plays while preparing");

            await service.SetKeyboardDisplayAnimationAsync(Enable(AssetB));
            rig.Media.Tokens[0].IsCancellationRequested.Should().BeTrue("the superseded generation is cancelled");
            await Task.Delay(200);
            rig.Media.Tokens.Should().HaveCount(1, "B waits until the cancelled preparation of A has actually ended");
            Animation(service).State.Should().Be(KeyboardDisplayAnimationStates.Preparing);
            rig.Factory.Count.Should().Be(1);
            releaseA.SetResult();
            await rig.ImagesAsync(2);
            rig.Factory.Image(1).Should().Equal(Fill(0x55));
            rig.Media.Completed.Should().Be(2, "late result A was returned and dropped");
            await Task.Delay(100);
            var status = Animation(service);
            status.Settings.AssetId.Should().Be(AssetB);
            status.LastError.Should().BeNull();
            rig.Timeline.At(2500); rig.Time.Advance(TimeSpan.FromSeconds(2));
            await rig.ImagesAsync(3);
            rig.Factory.Image(2).Should().Equal(Fill(0x66), "the late stale result was dropped");

            // The bound assignment leaves the scene: the very next turn shows status, without a held frame.
            rig.Media.Scene = new([new("screen:h2:test", AssetA, TimelineId: SharedTimelineId)]);
            await rig.ImagesAsync(4);
            var shown = rig.Factory.Image(3);
            Fills.Any(f => f.AsSpan().SequenceEqual(shown)).Should().BeFalse("a status page, not an animation frame");
            Animation(service).State.Should().Be(KeyboardDisplayAnimationStates.Unavailable);
            rig.Media.Scene = AnimationRig.BothScene;
            rig.Time.Advance(TimeSpan.FromSeconds(2));
            await UntilAsync(() => rig.Factory.Count >= 5 && rig.Factory.Image(rig.Factory.Count - 1).AsSpan().SequenceEqual(Fill(0x66)), "resumed B");
            rig.Media.Prepares.Should().Be(2);
        }
        finally { await service.StopAsync(default); }
    }

    [Fact]
    public async Task Saved_settings_survive_restart_and_admission_does_not_rewrite_them()
    {
        var rig = new AnimationRig(this);
        var on = Enable(AssetB) with { Fit = "cover", Encoding = KeyboardDisplayAnimationEncodings.LumaBayer4, IntervalMs = 3000 };
        using (var first = rig.Make(Admitted()))
        {
            var saved = await first.SetKeyboardDisplayAnimationAsync(on);
            saved.Settings.Should().Be(on);
        }
        var bytes = File.ReadAllBytes(AnimationPath());
        bytes.Length.Should().BeLessThan(2048);
        using (var restarted = rig.Make(Admitted()))
        {
            await UntilAsync(() => Animation(restarted).State is KeyboardDisplayAnimationStates.Ready, "restarted preparation");
            var status = Animation(restarted);
            status.Settings.Should().Be(on);
            status.CachedFrames.Should().Be(3);
            rig.Media.LastRequest.Should().Be((AssetB, "cover", KeyboardDisplayAnimationEncodings.LumaBayer4));
        }
        using (var unadmitted = rig.Make(Pinned()))
        {
            var status = Animation(unadmitted);
            status.State.Should().Be(KeyboardDisplayAnimationStates.NotAdmitted);
            status.Settings.Should().Be(on);
            await ((Func<Task>)(() => unadmitted.SetKeyboardDisplayAnimationAsync(on))).Should().ThrowAsync<InvalidOperationException>();
        }
        File.ReadAllBytes(AnimationPath()).Should().Equal(bytes);
        rig.Screens.Mutations.Should().Be(0);
    }

    [Theory]
    [InlineData("{\"Schema\":1,\"Enabled\":tru")]
    [InlineData("{\"Schema\":2,\"Enabled\":false,\"AssetId\":null,\"TimelineId\":null,\"Fit\":\"contain\",\"Encoding\":\"luma-round-v1\",\"IntervalMs\":2000}")]
    [InlineData("{\"Schema\":1,\"Enabled\":true,\"AssetId\":\"00000000000000a1\",\"TimelineId\":null,\"Fit\":\"contain\",\"Encoding\":\"luma-round-v1\",\"IntervalMs\":2000}")]
    [InlineData("{\"Schema\":1,\"Enabled\":false,\"AssetId\":null,\"TimelineId\":null,\"Fit\":\"stretch\",\"Encoding\":\"luma-round-v1\",\"IntervalMs\":2000}")]
    [InlineData("{\"Schema\":1,\"Enabled\":false,\"AssetId\":null,\"TimelineId\":null,\"Fit\":\"contain\",\"Encoding\":\"luma-round-v1\",\"IntervalMs\":500}")]
    [InlineData("oversized")]
    public async Task Unreadable_settings_keep_the_animation_off_and_the_file_untouched_until_an_explicit_save(string content)
    {
        var rig = new AnimationRig(this);
        Directory.CreateDirectory(Path.GetDirectoryName(AnimationPath())!);
        var bytes = content == "oversized" ? Encoding.UTF8.GetBytes("{\"Schema\":1,\"Pad\":\"" + new string('x', 3000) + "\"}") : Encoding.UTF8.GetBytes(content);
        File.WriteAllBytes(AnimationPath(), bytes);
        using var service = rig.Make(Admitted());
        var status = await service.GetKeyboardDisplayAnimationAsync();
        status.State.Should().Be(KeyboardDisplayAnimationStates.SettingsError);
        status.Settings.Enabled.Should().BeFalse();
        status.LastError.Should().Contain("原文件未被覆盖");
        await Task.Delay(100);
        rig.Media.Prepares.Should().Be(0);
        File.ReadAllBytes(AnimationPath()).Should().Equal(bytes);

        var saved = await service.SetKeyboardDisplayAnimationAsync(Enable(AssetA));
        saved.State.Should().NotBe(KeyboardDisplayAnimationStates.SettingsError);
        saved.LastError.Should().BeNull();
        using var replaced = JsonDocument.Parse(File.ReadAllBytes(AnimationPath()));
        replaced.RootElement.GetProperty("Schema").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task Invalid_or_unbound_requests_change_nothing()
    {
        var rig = new AnimationRig(this);
        using var service = rig.Make(Admitted());
        var before = await service.GetKeyboardDisplayAnimationAsync();
        foreach (var bad in new[]
        {
            Enable(AssetA) with { IntervalMs = 1999 }, Enable(AssetA) with { IntervalMs = 10_001 }, Enable(AssetA) with { Fit = "stretch" },
            Enable(AssetA) with { Encoding = "luma-legacy" }, Enable(AssetA) with { AssetId = "../../x" }, Enable(AssetA) with { TimelineId = "package:x" },
            Enable(AssetA) with { AssetId = null },
        })
            await ((Func<Task>)(() => service.SetKeyboardDisplayAnimationAsync(bad))).Should().ThrowAsync<ArgumentException>();
        await ((Func<Task>)(() => service.SetKeyboardDisplayAnimationAsync(Enable("00000000000000c3"))))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*不在当前场景的共享时间轴上*");
        await ((Func<Task>)(() => service.PreviewKeyboardDisplayAnimationAsync(new("00000000000000c3", SharedTimelineId, "contain", KeyboardDisplayAnimationEncodings.LumaRound))))
            .Should().ThrowAsync<InvalidOperationException>();
        File.Exists(AnimationPath()).Should().BeFalse();
        (await service.GetKeyboardDisplayAnimationAsync()).Should().BeEquivalentTo(before, o => o.Excluding(s => s.UpdatedAt));
        rig.Media.Prepares.Should().Be(0);
    }

    [Fact]
    public async Task A_refused_preparation_is_reported_and_not_retried_until_an_explicit_save()
    {
        var rig = new AnimationRig(this);
        rig.Media.Failure = new KeyboardOledPrepareException("需要 900 帧 × 6656 B = 5990400 B，超过缓存上限 1048576 B；未缓存任何帧");
        using var service = rig.Make(Admitted());
        await service.SetKeyboardDisplayAnimationAsync(Enable(AssetA));
        await UntilAsync(() => Animation(service).State == KeyboardDisplayAnimationStates.Error, "refusal");
        var failed = Animation(service);
        failed.LastError.Should().Contain("超过缓存上限").And.Contain("未缓存任何帧");
        (failed.CachedFrames, failed.CacheBytes).Should().Be((0, 0L));
        for (var i = 0; i < 3; i++) await service.GetKeyboardDisplayAnimationAsync();
        rig.Media.Prepares.Should().Be(1, "a refusal is not retried by status reads or the monitor");
        rig.Media.Failure = null;
        await service.SetKeyboardDisplayAnimationAsync(Enable(AssetA));
        await UntilAsync(() => Animation(service).State == KeyboardDisplayAnimationStates.Ready, "explicit retry");
        rig.Media.Prepares.Should().Be(2);
    }

    [Fact]
    public async Task Preview_returns_the_packed_bytes_of_the_shared_preparation_path()
    {
        var rig = new AnimationRig(this);
        using var service = rig.Make(Admitted());
        var preview = await service.PreviewKeyboardDisplayAnimationAsync(new(AssetB, SharedTimelineId, "cover", KeyboardDisplayAnimationEncodings.LumaBayer4));
        (preview.Width, preview.Height, preview.Encoding).Should().Be((208, 64, KeyboardDisplayAnimationEncodings.LumaBayer4));
        preview.Frames.Select(f => f.PositionMs).Should().Equal(0, 1000, 2000);
        preview.Frames.Select(f => Convert.FromBase64String(f.PackedBase64)).Should().BeEquivalentTo([Fill(0x44), Fill(0x55), Fill(0x66)], o => o.WithStrictOrdering());
        preview.Note.Should().Contain("低半字节为偶数像素").And.Contain("不代表键盘刷新率");
        rig.Media.Samples.Should().Be(1);
        rig.Media.Prepares.Should().Be(0, "a preview neither prepares nor enables playback");
        File.Exists(AnimationPath()).Should().BeFalse();
        using var unadmitted = rig.Make(Pinned());
        await ((Func<Task>)(() => unadmitted.PreviewKeyboardDisplayAnimationAsync(new(AssetB, SharedTimelineId, "cover", KeyboardDisplayAnimationEncodings.LumaRound))))
            .Should().ThrowAsync<InvalidOperationException>("an unadmitted Host previews nothing");
        rig.Media.Samples.Should().Be(1);
    }

    [Fact]
    public async Task Case_colour_following_and_the_animation_share_the_one_owner()
    {
        var rig = new AnimationRig(this);
        var bridge = new RogWiredLightingBridge();
        using var service = rig.Make(Admitted() with { LightingVerified = true }, bridge);
        await service.StartAsync(default);
        try
        {
            await rig.ReadyAsync(service);
            await service.SetKeyboardDisplayAnimationAsync(Enable(AssetA));
            await rig.ImagesAsync(2);
            var writes = rig.Factory.Io.NativeWrites;
            await bridge.WriteAsync(new(255, 0, 0), default).WaitAsync(TimeSpan.FromSeconds(10));
            rig.Factory.Io.NativeWrites.Should().Be(writes + 1);
            rig.Timeline.At(1000); rig.Time.Advance(TimeSpan.FromSeconds(2));
            await rig.ImagesAsync(3);
            await bridge.WriteAsync(new(0, 0, 255), default).WaitAsync(TimeSpan.FromSeconds(10));
            rig.Factory.Io.NativeWrites.Should().Be(writes + 2);
            rig.Factory.Image(2).Should().Equal(Fill(0x22));
            rig.Factory.Opens.Should().Be(1);
            Animation(service).State.Should().Be(KeyboardDisplayAnimationStates.Playing);
        }
        finally { await service.StopAsync(default); }
        _rgbProxy!.CaseMutations.Should().Be(0);
        rig.Screens.Mutations.Should().Be(0);
    }

    // ---- helpers ----

    internal const string AssetA = "00000000000000a1";
    internal const string AssetB = "00000000000000b2";
    internal static readonly string SharedTimelineId = "package:" + new string('a', 64) + ":" + new string('b', 64);
    private static readonly byte[][] Fills = [.. new byte[] { 0x11, 0x22, 0x33, 0x44, 0x55, 0x66 }.Select(Fill)];

    internal static byte[] Fill(byte value) => Enumerable.Repeat(value, KeyboardOledFormat.PackedBytes).ToArray();

    internal static KeyboardDisplayAnimationSettingsDto Enable(string asset) =>
        new(true, asset, SharedTimelineId, "contain", KeyboardDisplayAnimationEncodings.LumaRound, 2000);

    internal static byte[] AnimationFile(bool enabled) => JsonSerializer.SerializeToUtf8Bytes(new
    {
        Schema = 1, Enabled = enabled, AssetId = AssetA, TimelineId = SharedTimelineId, Fit = "contain",
        Encoding = KeyboardDisplayAnimationEncodings.LumaRound, IntervalMs = 2000,
    });

    private static KeyboardDisplayOptions Admitted() => Pinned() with { AnimationAdmitted = true };

    private string AnimationPath() => Path.Combine(_directory, "peripherals", KeyboardDisplayAnimation.FileName);

    private static KeyboardDisplayAnimationStatusDto Animation(KeyboardDisplayService service) =>
        service.GetKeyboardDisplayAnimationAsync().GetAwaiter().GetResult();

    private sealed record StatusRun(List<string> Writes, List<byte[]> Images, string Detail, FakeSharedTimeline? Timeline, FakeKeyboardOledMedia? Media,
        KeyboardDisplayAnimationStatusDto? Status);

    /// <summary>Start, first ACK, an idle lease, stop; null media = the original construction without animation services.</summary>
    private async Task<StatusRun> RunStatusOnlyAsync(FakeKeyboardOledMedia? media, bool admitted)
    {
        var timeline = media is null ? null : new FakeSharedTimeline();
        var source = media ?? new FakeKeyboardOledMedia();
        source.Scene = new([new("screen:h2:test", AssetA, TimelineId: SharedTimelineId)]); // the same status artwork in every run
        var screens = KeyboardAnimationScreens.Create(source);
        var entryTime = new ManualTimeProvider();
        var factory = new TapFactory { EntryAcknowledged = () => entryTime.Advance(TimeSpan.FromMilliseconds(KeyboardNativePresentation.EntryLogoMs)) };
        var options = new UnifiedRgbOptions { DataDirectory = _directory, KeyboardDisplay = Pinned() with { AnimationAdmitted = admitted } };
        var rgb = DispatchProxy.Create<IRgbService, ReadProxy>();
        using var service = media is null
            ? new KeyboardDisplayService(options, rgb, screens, factory, new ClearGuard(), time: entryTime)
            : new KeyboardDisplayService(options, rgb, screens, factory, new ClearGuard(), keyboardMedia: media, sharedTimeline: timeline, time: entryTime);
        await service.StartAsync(default);
        string detail;
        try
        {
            await WaitAsync(service, "Running");
            await UntilAsync(() => factory.Count >= 1, "post-logo status content");
            factory.EntryLogos.Should().Be(1);
            await Task.Delay(400);
            detail = (await service.GetKeyboardDisplayStatusAsync()).Detail;
        }
        finally { await service.StopAsync(default); }
        var status = media is null ? null : await service.GetKeyboardDisplayAnimationAsync();
        return new(factory.Writes, [.. Enumerable.Range(0, factory.Count).Select(factory.Image)], detail, timeline, media, status);
    }

    private sealed class AnimationRig(KeyboardDisplayServiceTests owner)
    {
        public static readonly ScreenSceneDto BothScene = new([
            new("screen:h2:test", AssetA, TimelineId: SharedTimelineId),
            new("screen:h2:zwei", AssetB, TimelineId: SharedTimelineId),
        ]);

        public FakeKeyboardOledMedia Media { get; } = new() { Scene = BothScene };
        public FakeSharedTimeline Timeline { get; } = new();
        public ManualTimeProvider Time { get; } = new();
        public TapFactory Factory { get; } = new();
        public KeyboardAnimationScreens Screens { get; private set; } = null!;

        public KeyboardDisplayService Make(KeyboardDisplayOptions options, RogWiredLightingBridge? bridge = null)
        {
            var rgb = DispatchProxy.Create<IRgbService, ReadProxy>();
            owner._rgbProxy = (ReadProxy)rgb;
            var screens = KeyboardAnimationScreens.Create(Media);
            Screens = (KeyboardAnimationScreens)(object)screens;
            Factory.EntryAcknowledged = () => Time.Advance(TimeSpan.FromMilliseconds(KeyboardNativePresentation.EntryLogoMs));
            return new(new UnifiedRgbOptions { DataDirectory = owner._directory, KeyboardDisplay = options }, rgb, screens, Factory, new ClearGuard(), bridge,
                keyboardMedia: Media, sharedTimeline: Timeline, time: Time);
        }

        public async Task ReadyAsync(KeyboardDisplayService service)
        {
            await WaitAsync(service, "Running");
            await ImagesAsync(1);
            Factory.EntryLogos.Should().Be(Factory.Opens, "each new owner ACKed its one entry Logo before content");
        }

        public Task ImagesAsync(int count) => UntilAsync(() => Factory.Count >= count, count + " uploaded images");
    }

    /// <summary>Opens the existing fake wired channel through a tap that records every request and each complete uploaded image.</summary>
    private sealed class TapFactory : IRogWiredOledFactory
    {
        private readonly List<byte[]> _images = [];
        public Io Io { get; private set; } = new();
        public int Opens { get; private set; }
        public List<string> Writes { get; } = [];
        public Action? FirstChunk { get; set; }
        public Action? EntryAcknowledged { get; set; }
        public int EntryLogos { get; private set; }
        private static readonly byte[] EntryLogo = KeyboardStatusArtwork.RenderLogo();
        public int Count { get { lock (_images) return _images.Count; } }
        public byte[] Image(int index) { lock (_images) return _images[index]; }
        public RogWiredOledConnection? TryOpen(string key)
        {
            if (++Opens > 1) Io = Io.Reopen();
            return new(new Tap(Io, this), () => true, () => key, () => "001200000024000100");
        }

        private sealed class Tap(Io io, TapFactory owner) : IHidReportChannel
        {
            private readonly List<byte> _image = [];
            private bool _entryRenewal;
            public async ValueTask WriteAsync(ReadOnlyMemory<byte> request, CancellationToken ct)
            {
                var p = request.ToArray();
                if (p[1] == 0x67 && p[2] == 2 && _entryRenewal)
                {
                    _entryRenewal = false;
                    owner.EntryAcknowledged?.Invoke(); // the preceding upload callback has ACKed the Logo and started its bound
                }
                if (p[1] == 0x67 && p[2] == 0) _image.Clear();
                if (p[1] == 0x67 && p[2] == 1)
                {
                    if (_image.Count == 0) owner.FirstChunk?.Invoke();
                    _image.AddRange(p.AsSpan(5, Math.Min(60, KeyboardOledFormat.PackedBytes - _image.Count)).ToArray());
                    if (BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(3)) == 0)
                    {
                        var completed = _image.ToArray();
                        if (completed.AsSpan().SequenceEqual(EntryLogo))
                        {
                            owner.EntryLogos++;
                            _entryRenewal = true;
                        }
                        else lock (owner._images) owner._images.Add(completed);
                    }
                }
                if (!(p[1] == 0x67 && p[2] == 2)) lock (owner.Writes) owner.Writes.Add(Convert.ToHexString(p)); // lease renewals are timing, not content
                await io.WriteAsync(request, ct);
            }
            public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct) => io.ReadAsync(buffer, ct);
            public void Dispose() => io.Dispose();
        }
    }
}

/// <summary>Prepared keyboard frames by asset: three 1000 ms segments of a 3000 ms loop, each a distinct uniform byte.</summary>
internal sealed class FakeKeyboardOledMedia : IKeyboardOledMedia
{
    private ScreenSceneDto? _scene;
    private int _prepares, _samples, _references, _completed;
    public ScreenSceneDto? Scene { get => Volatile.Read(ref _scene); set => Volatile.Write(ref _scene, value); }
    public IReadOnlyList<ScreenAssetDto> Assets { get; } =
    [
        new(KeyboardDisplayServiceTests.AssetA, "动画 A", 64, 64, 3, 3000, "animation"),
        new(KeyboardDisplayServiceTests.AssetB, "动画 B", 64, 64, 3, 3000, "animation"),
    ];
    public Func<string, Task>? Hold { get; set; }
    public Exception? Failure { get; set; }
    public List<CancellationToken> Tokens { get; } = [];
    public (string Asset, string Fit, string Encoding)? LastRequest { get; private set; }
    public int Prepares => Volatile.Read(ref _prepares);
    public int Samples => Volatile.Read(ref _samples);
    public int References => Volatile.Read(ref _references);
    public int Completed => Volatile.Read(ref _completed);

    public ScreenSceneDto? CurrentReferences() { Interlocked.Increment(ref _references); return Scene; }

    public async Task<KeyboardOledFrames> PrepareKeyboardOledAsync(string assetId, string fit, string encoding, KeyboardOledLimits limits, CancellationToken ct)
    {
        Interlocked.Increment(ref _prepares);
        lock (Tokens) Tokens.Add(ct);
        LastRequest = (assetId, fit, encoding);
        try
        {
            if (Hold is not null) await Hold(assetId);
            if (Failure is not null) throw Failure;
            return new(assetId, fit, encoding, 3000, 3, [0, 1000, 2000], Bytes(assetId));
        }
        finally { Interlocked.Increment(ref _completed); }
    }

    public Task<IReadOnlyList<KeyboardOledSample>> SampleKeyboardOledAsync(string assetId, string fit, string encoding, int gridFloorMs, int count,
        int timeoutMs, CancellationToken ct)
    {
        Interlocked.Increment(ref _samples);
        var frames = Bytes(assetId);
        return Task.FromResult<IReadOnlyList<KeyboardOledSample>>([.. new[] { 0, 1000, 2000 }.Take(count).Select((at, i) => new KeyboardOledSample(at, frames[i]))]);
    }

    public static byte[][] Bytes(string assetId) => assetId == KeyboardDisplayServiceTests.AssetB
        ? [KeyboardDisplayServiceTests.Fill(0x44), KeyboardDisplayServiceTests.Fill(0x55), KeyboardDisplayServiceTests.Fill(0x66)]
        : [KeyboardDisplayServiceTests.Fill(0x11), KeyboardDisplayServiceTests.Fill(0x22), KeyboardDisplayServiceTests.Fill(0x33)];
}

/// <summary>The shared scene timeline as the screens see it: one known package timeline at a settable position.</summary>
internal sealed class FakeSharedTimeline : IScreenTimeline
{
    private ScreenTimelineSample? _sample = new(0, "test-shared-clock");
    private int _reads;
    public int Reads => Volatile.Read(ref _reads);
    public void At(double positionMs) => Volatile.Write(ref _sample, new(positionMs, "test-shared-clock"));
    public void Block(string why) => Volatile.Write(ref _sample, new(0, "test-shared-clock", why));
    public ScreenTimelineSample? Position(string timelineId)
    {
        Interlocked.Increment(ref _reads);
        return timelineId == KeyboardDisplayServiceTests.SharedTimelineId ? Volatile.Read(ref _sample) : null;
    }
}

/// <summary>Read-only screen service over <see cref="FakeKeyboardOledMedia"/>; every other call is counted and refused.</summary>
public class KeyboardAnimationScreens : DispatchProxy
{
    private FakeKeyboardOledMedia _media = null!;
    private int _mutations;
    public int Mutations => Volatile.Read(ref _mutations);

    internal static IScreenService Create(FakeKeyboardOledMedia media)
    {
        var proxy = DispatchProxy.Create<IScreenService, KeyboardAnimationScreens>();
        ((KeyboardAnimationScreens)(object)proxy)._media = media;
        return proxy;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod?.Name switch
    {
        "GetScreenSceneAsync" => Task.FromResult(_media.Scene ?? new ScreenSceneDto([])),
        "ListScreenAssetsAsync" => Task.FromResult(_media.Assets),
        "ListScreensAsync" => Task.FromResult<IReadOnlyList<ScreenDeviceDto>>([new("screen:h2:test", "H2", "H2", 400, 400, "Sleeping", null, true, 0, 0, null, 0)]),
        _ => Refuse(targetMethod),
    };

    private object? Refuse(MethodInfo? method)
    {
        Interlocked.Increment(ref _mutations);
        throw new InvalidOperationException("No screen writes allowed: " + method?.Name);
    }
}
