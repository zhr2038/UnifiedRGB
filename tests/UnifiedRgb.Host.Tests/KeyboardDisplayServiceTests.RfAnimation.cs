using System.Buffers.Binary;
using System.Diagnostics;
using System.Reflection;
using UnifiedRgb.Contracts;
using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Core.Engine;
using UnifiedRgb.Devices;
using UnifiedRgb.Devices.AsusRog;
using UnifiedRgb.Host.Services;
using UnifiedRgb.Transports.Hid;

namespace UnifiedRgb.Host.Tests;

// The keyboard animation on the stock RF owner: the real RogWirelessOledConnection over the existing fake receiver channel
// (WirelessIo/Io) through a recording tap. Fake ACKs and echoes are not physical RF evidence; they prove the owner's packing,
// ordering and failure policy only. The colour cases drive the real FrameBus -> DeviceWriter -> WiredKeyboardAdapter ->
// RogWiredLightingBridge chain into the same owner, with its existing 2 s colour wait unchanged.
public sealed partial class KeyboardDisplayServiceTests
{
    [Fact]
    public async Task Rf_animation_uses_the_stock_wireless_owner_with_RF59_images_and_leaves_the_restoration_sequence_unchanged()
    {
        var baseline = await RunRfScriptAsync(animated: false);
        var animated = await RunRfScriptAsync(animated: true);

        baseline.Images.Should().ContainSingle();
        animated.Images.Should().HaveCount(5);
        foreach (var run in new[] { baseline, animated })
        {
            for (var i = 0; i < run.Images.Count; i++) ExpectRf59(run.Images[i], run.Expected[i]);
            (run.WiredOpens, run.WirelessOpens, run.NativeWrites).Should().Be((0, 1, 3), "one RF owner carried red, blue and the stop-time original-bank restore");
            run.BrightnessWrites.Distinct().Should().BeEquivalentTo(new byte[] { 100, 25 });
            run.Presets.Distinct().Should().Equal((byte)3);
            run.Brightness.Should().Be(25, "page off and stop restore the original ROG animation brightness");
            run.Native.Should().Be(RogProtocol.KeyboardColourFrame(new(10, 20, 30), 25, 80).Payloads[0], "stop restores the original lighting bank");
            run.AvailableAfterPageOff.Should().BeTrue("RF lighting stays owned while the display is off");
            (run.CaseMutations, run.ScreenMutations).Should().Be((0, 0));
            File.Exists(Path.Combine(run.Directory, "peripherals", "keyboard-display-session.json")).Should().BeFalse("recovery completed");
        }
        animated.Mutating.Should().Equal(baseline.Mutating, "animation frames add no preset, brightness or lighting commands on the RF owner");
        animated.PageOff.Should().Be(KeyboardDisplayAnimationStates.PageOff);
        animated.ImagesWhilePageOff.Should().Be(0, "nothing is uploaded while the page is off");
        animated.Acknowledged.Should().Be(3);
    }

    [Fact]
    public async Task A_completed_slow_RF_frame_outlasts_the_existing_colour_wait_then_trips_the_breaker_without_a_late_colour_or_resend()
    {
        var rig = new RfAnimationRig(this, _directory);
        var bridge = new RogWiredLightingBridge();
        var options = RfAdmitted();
        using var service = rig.Make(options, bridge);
        using var adapter = new WiredKeyboardAdapter(bridge, options);
        var descriptor = (await adapter.DiscoverAsync(default)).Single();
        using var bus = new FrameBus();
        var mailbox = bus.Register(descriptor);
        using var writer = new DeviceWriter(mailbox, adapter);
        await service.StartAsync(default);
        try
        {
            await WaitAsync(service, "Running");
            await rig.ImagesAsync(1);
            await adapter.OpenAsync(descriptor.Id, default);
            var holding = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var colourDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var held = 0;
            // Each held chunk stays below the 1000 ms exchange deadline and the whole frame below the 8000 ms upload deadline;
            // together they outlast the existing 2 s colour wait. The animation clock records a 1500 ms selection-to-ACK.
            rig.Factory.BeforeChunk = async (p, ct) =>
            {
                if (p[5] != 0x11 || colourDone.Task.IsCompleted || held >= 6) return;
                if (held++ == 0) { rig.Time.Advance(TimeSpan.FromMilliseconds(1500)); holding.TrySetResult(); }
                await Task.WhenAny(colourDone.Task, Task.Delay(600, ct));
            };
            await service.SetKeyboardDisplayAnimationAsync(Enable(AssetA));
            await holding.Task.WaitAsync(TimeSpan.FromSeconds(10));

            var clock = Stopwatch.StartNew();
            (await DrainAsync(bus, mailbox, writer, new(255, 0, 0)).WaitAsync(TimeSpan.FromSeconds(10))).Should().BeTrue();
            clock.Elapsed.Should().BeGreaterThan(TimeSpan.FromMilliseconds(1900), "the existing 2 s wait was the boundary");
            rig.Factory.Count.Should().Be(1, "the slow frame was still transferring when the colour wait expired");
            colourDone.TrySetResult();
            var stats = writer.GetStats();
            (stats.Written, stats.Errors, stats.LastError).Should().Be((0L, 1L, new TimeoutException().Message), "the colour is reported failed, not healthy");

            await rig.ImagesAsync(3);
            ExpectRf59(rig.Factory.Image(1), Fill(0x11));
            ExpectRf59(rig.Factory.Image(2), service.ArtworkPixels.ToArray());
            var tripped = Animation(service);
            tripped.State.Should().Be(KeyboardDisplayAnimationStates.BreakerTripped, "the completed slow frame trips the breaker and falls back to status");
            tripped.Detail.Should().Contain("1500 ms").And.Contain("超过上限 1000 ms").And.Contain("未重发或重试");
            (tripped.Selected, tripped.Acknowledged).Should().Be((1L, 1L));

            rig.Timeline.At(2500); rig.Time.Advance(TimeSpan.FromSeconds(30)); await Task.Delay(400);
            rig.Factory.Count.Should().Be(3, "no animation frame is selected or resent after the trip");
            (rig.Factory.Opens, rig.Factory.Io.BeginPackets, rig.Factory.Io.Data).Should().Be((1, 3, 3 * 113), "no reopen and no resent chunk");
            rig.Factory.Io.NativeWrites.Should().Be(0, "the expired colour is dropped by the owner, never written late");
            var display = await service.GetKeyboardDisplayStatusAsync();
            (display.State, display.LastError).Should().Be(("Running", (string?)null), "the frame itself was acknowledged in full");
            bridge.Available.Should().BeTrue();

            (await DrainAsync(bus, mailbox, writer, new(0, 0, 255)).WaitAsync(TimeSpan.FromSeconds(10))).Should().BeTrue();
            (writer.GetStats().Written, writer.GetStats().Errors).Should().Be((1L, 1L), "the next frame carries the current colour");
            rig.Factory.Io.NativeWrites.Should().Be(1);
        }
        finally { await service.StopAsync(default); }
        _rgbProxy!.CaseMutations.Should().Be(0);
        rig.Screens.Mutations.Should().Be(0);
    }

    [Fact]
    public async Task An_unacknowledged_RF_chunk_fails_the_waiting_colour_and_stays_a_sticky_error_without_fallback_resend_or_reopen()
    {
        var rig = new RfAnimationRig(this, _directory);
        var bridge = new RogWiredLightingBridge();
        var options = RfAdmitted();
        using var service = rig.Make(options, bridge);
        using var adapter = new WiredKeyboardAdapter(bridge, options);
        var descriptor = (await adapter.DiscoverAsync(default)).Single();
        using var bus = new FrameBus();
        var mailbox = bus.Register(descriptor);
        using var writer = new DeviceWriter(mailbox, adapter);
        var journal = Path.Combine(_directory, "peripherals", "keyboard-display-session.json");
        await service.StartAsync(default);
        try
        {
            await WaitAsync(service, "Running");
            await rig.ImagesAsync(1);
            await adapter.OpenAsync(descriptor.Id, default);
            // The fifth chunk of the animation frame reaches the fake keyboard, but its ACK never reaches the owner.
            rig.Factory.LoseAck = image => image.Lengths.Length == 5 && image.Pixels[0] == 0x11;
            await service.SetKeyboardDisplayAnimationAsync(Enable(AssetA));
            await rig.Factory.AckLost.Task.WaitAsync(TimeSpan.FromSeconds(10));

            var clock = Stopwatch.StartNew();
            (await DrainAsync(bus, mailbox, writer, new(255, 0, 0)).WaitAsync(TimeSpan.FromSeconds(10))).Should().BeTrue();
            clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2), "the ended generation fails the colour before its own wait expires");
            var stats = writer.GetStats();
            (stats.Written, stats.Errors, stats.LastError).Should().Be((0L, 1L, "Wired keyboard generation ended."));

            await WaitAsync(service, "Error");
            var failed = await service.GetKeyboardDisplayStatusAsync();
            failed.LastError.Should().Contain(nameof(RogOledTimeoutException)).And.Contain("stage=read").And.Contain("no retry");
            var writes = rig.Factory.WriteCount;
            var retained = File.ReadAllBytes(journal);
            await Task.Delay(2300);
            (rig.Factory.Opens, rig.Factory.Io.BeginPackets, rig.Factory.Io.Data).Should().Be((1, 2, 113 + 5), "no resend and no reopen");
            rig.Factory.WriteCount.Should().Be(writes, "the poisoned connection sends nothing more");
            rig.Factory.Count.Should().Be(1, "no status fallback image follows an uncertain transfer");
            rig.Factory.Io.NativeWrites.Should().Be(0);
            rig.Factory.Io.Brightness.Should().Be(100, "the unknown connection cannot send its restoration");
            File.ReadAllBytes(journal).Should().Equal(retained, "the recovery journal is retained");
            (await service.GetKeyboardDisplayStatusAsync()).State.Should().Be("Error", "the fault is sticky until an explicit toggle");

            var animation = Animation(service);
            animation.State.Should().Be(KeyboardDisplayAnimationStates.Ready, "an unknown outcome neither plays nor trips the breaker");
            animation.Detail.Should().Contain("等待键盘状态屏运行");
            (animation.Selected, animation.Acknowledged).Should().Be((1L, 0L));

            bridge.Available.Should().BeFalse();
            (await DrainAsync(bus, mailbox, writer, new(0, 0, 255)).WaitAsync(TimeSpan.FromSeconds(10))).Should().BeTrue();
            stats = writer.GetStats();
            (stats.Written, stats.Errors).Should().Be((0L, 2L), "the next frame is blocked, not reported healthy");
            stats.LastError.Should().Be(new DeviceBlockedException("Keyboard USB owner is not ready.").Message);
        }
        finally { await service.StopAsync(default); }
        _rgbProxy!.CaseMutations.Should().Be(0);
        rig.Screens.Mutations.Should().Be(0);
    }

    // ---- helpers ----

    private static KeyboardDisplayOptions RfAdmitted(bool admitted = true) =>
        WirelessPinned() with { WirelessLightingVerified = true, AnimationAdmitted = admitted };

    private static void ExpectRf59(RfImage image, byte[] expected)
    {
        (image.Declared, image.Width, image.Height).Should().Be((113, 208, 64));
        image.Lengths.Should().Equal([.. Enumerable.Repeat(59, 112), 48]);
        image.Pixels.Should().Equal(expected);
    }

    private static Task<bool> DrainAsync(FrameBus bus, Mailbox mailbox, DeviceWriter writer, Rgb colour)
    {
        mailbox.Back.Zones[0][0] = colour;
        bus.Publish(mailbox.Controller.Id, mailbox.Back);
        return Task.Factory.StartNew(writer.DrainOnce, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    private sealed record RfRun(string Directory, List<RfImage> Images, List<byte[]> Expected, List<string> Mutating, int WiredOpens,
        int WirelessOpens, int NativeWrites, List<byte> BrightnessWrites, List<byte> Presets, byte Brightness, string Native,
        bool AvailableAfterPageOff, string PageOff, int ImagesWhilePageOff, long Acknowledged, int CaseMutations, int ScreenMutations);

    /// <summary>Start, (animated: frame), red, (animated: frame, disable, re-enable), page off, blue, stop; each run in its own data directory.</summary>
    private async Task<RfRun> RunRfScriptAsync(bool animated)
    {
        var directory = Path.Combine(_directory, animated ? "animated" : "baseline");
        var rig = new RfAnimationRig(this, directory);
        var bridge = new RogWiredLightingBridge();
        var expected = new List<byte[]>();
        string pageOff;
        int whilePageOff;
        bool available;
        long acknowledged;
        using var service = rig.Make(RfAdmitted(animated), bridge);
        await service.StartAsync(default);
        try
        {
            await WaitAsync(service, "Running");
            await rig.ImagesAsync(1);
            expected.Add(service.ArtworkPixels.ToArray());
            (await service.GetKeyboardDisplayStatusAsync()).Detail.Should().Contain("无线通信确认");
            if (animated)
            {
                rig.Timeline.At(1500);
                await service.SetKeyboardDisplayAnimationAsync(Enable(AssetA));
                await rig.ImagesAsync(2);
                expected.Add(Fill(0x22));
                await UntilAsync(() => Animation(service).Acknowledged == 1, "first RF animation ACK");
                Animation(service).State.Should().Be(KeyboardDisplayAnimationStates.Playing);
                (await service.GetKeyboardDisplayStatusAsync()).Detail.Should().Contain("键盘动画画面已获无线通信确认");
            }
            await bridge.WriteAsync(new(255, 0, 0), default).WaitAsync(TimeSpan.FromSeconds(10));
            if (animated)
            {
                rig.Timeline.At(2500); rig.Time.Advance(TimeSpan.FromSeconds(2));
                await rig.ImagesAsync(3);
                expected.Add(Fill(0x33));
                await UntilAsync(() => Animation(service).Acknowledged == 2, "second RF animation ACK");
                await service.SetKeyboardDisplayAnimationAsync(Enable(AssetA) with { Enabled = false });
                await rig.ImagesAsync(4);
                expected.Add(service.ArtworkPixels.ToArray());
                await UntilAsync(() => service.GetKeyboardDisplayStatusAsync().GetAwaiter().GetResult().FramesAcknowledged == 4, "status ACK");
                // Re-enabled, so the page is turned off below while the animation is still wanted.
                rig.Time.Advance(TimeSpan.FromSeconds(2));
                await service.SetKeyboardDisplayAnimationAsync(Enable(AssetA));
                await rig.ImagesAsync(5);
                expected.Add(Fill(0x33));
                await UntilAsync(() => Animation(service).Acknowledged == 3, "third RF animation ACK");
            }
            await service.SetKeyboardDisplayEnabledAsync(false);
            await WaitAsync(service, "Stopped");
            rig.Factory.Io.Brightness.Should().Be(25, "page off restores the original");
            pageOff = Animation(service).State;
            var count = rig.Factory.Count;
            rig.Timeline.At(100); rig.Time.Advance(TimeSpan.FromSeconds(5)); await Task.Delay(300);
            whilePageOff = rig.Factory.Count - count;
            available = bridge.Available;
            await bridge.WriteAsync(new(0, 0, 255), default).WaitAsync(TimeSpan.FromSeconds(10));
            acknowledged = Animation(service).Acknowledged;
        }
        finally { await service.StopAsync(default); }
        var io = rig.Factory.Io;
        return new(directory, [.. Enumerable.Range(0, rig.Factory.Count).Select(rig.Factory.Image)], expected,
            [.. rig.Factory.WritesSnapshot().Where(w => w[2..4] is "61" or "68" or "51")], rig.Wired.Opens, rig.Factory.Opens, io.NativeWrites,
            [.. io.BrightnessWrites], [.. io.PresetSelections], io.Brightness, io.Native.Payloads[0], available, pageOff, whilePageOff,
            acknowledged, _rgbProxy!.CaseMutations, rig.Screens.Mutations);
    }

    private sealed record RfImage(int Declared, int Width, int Height, byte[] Pixels, int[] Lengths);

    private sealed class RfAnimationRig(KeyboardDisplayServiceTests owner, string directory)
    {
        public FakeKeyboardOledMedia Media { get; } = new() { Scene = AnimationRig.BothScene };
        public FakeSharedTimeline Timeline { get; } = new();
        public ManualTimeProvider Time { get; } = new();
        public RfTapFactory Factory { get; } = new();
        public Factory Wired { get; } = new();
        public KeyboardAnimationScreens Screens { get; private set; } = null!;

        public KeyboardDisplayService Make(KeyboardDisplayOptions options, RogWiredLightingBridge bridge)
        {
            var rgb = DispatchProxy.Create<IRgbService, ReadProxy>();
            owner._rgbProxy = (ReadProxy)rgb;
            var screens = KeyboardAnimationScreens.Create(Media);
            Screens = (KeyboardAnimationScreens)(object)screens;
            return new(new UnifiedRgbOptions { DataDirectory = directory, KeyboardDisplay = options }, rgb, screens, Wired, new ClearGuard(), bridge,
                Factory, keyboardMedia: Media, sharedTimeline: Timeline, time: Time);
        }

        public Task ImagesAsync(int count) => UntilAsync(() => Factory.Count >= count, count + " uploaded RF images");
    }

    /// <summary>Opens the real RF connection over the existing fake receiver channel through a tap that records each complete image.</summary>
    private sealed class RfTapFactory : IRogWirelessOledFactory
    {
        private readonly List<RfImage> _images = [];
        private readonly List<string> _writes = [];
        public int Opens { get; private set; }
        public List<RfTap> Channels { get; } = [];
        public Io Io => Channels.Single().Io.Inner;
        public Func<byte[], CancellationToken, Task>? BeforeChunk { get; set; }
        public Func<RfImage, bool>? LoseAck { get; set; }
        public TaskCompletionSource AckLost { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Count { get { lock (_images) return _images.Count; } }
        public RfImage Image(int index) { lock (_images) return _images[index]; }
        public int WriteCount { get { lock (_writes) return _writes.Count; } }
        public List<string> WritesSnapshot() { lock (_writes) return [.. _writes]; }

        public RogWirelessOledConnection? TryOpen(string receiverKey, string firmwareHeader)
        {
            Opens++;
            var tap = new RfTap(new WirelessIo(), this);
            Channels.Add(tap);
            return new(new RogReceiver(receiverKey, 0x1ACE, new Dictionary<byte, HidCollectionInfo>()), tap, firmwareHeader, () => tap.Io.Present);
        }

        internal sealed class RfTap(WirelessIo io, RfTapFactory owner) : IHidReportChannel
        {
            private (int Width, int Height) _size;
            private volatile bool _loseAck;
            public WirelessIo Io => io;

            public async ValueTask WriteAsync(ReadOnlyMemory<byte> report, CancellationToken ct)
            {
                var p = report.ToArray();
                p.Length.Should().Be(64, "RID2 RF report");
                var begin = p[1] == 0x67 && p[2] == 0;
                var data = p[1] == 0x67 && p[2] == 1;
                if (begin) _size = (BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(9)), BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(11)));
                if (data && owner.BeforeChunk is { } hold) await hold(p, ct);
                if (!(p[1] == 0x67 && p[2] == 2)) lock (owner._writes) owner._writes.Add(Convert.ToHexString(p)); // renewals are timing, not content
                await io.WriteAsync(report, ct);
                if (!data) return;
                var count = io.CopyLengths[^1];
                p.AsSpan(5 + count).ContainsAnyExcept((byte)0).Should().BeFalse("an RF59 chunk is zero-padded after its payload");
                var image = new RfImage(io.DeclaredChunks, _size.Width, _size.Height, [.. io.Pixels], [.. io.CopyLengths]);
                image.Lengths.Length.Should().Be(io.DeclaredChunks - BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(3)));
                if (owner.LoseAck?.Invoke(image) == true) { _loseAck = true; owner.AckLost.TrySetResult(); }
                if (BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(3)) == 0) lock (owner._images) owner._images.Add(image);
            }

            public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
            {
                var length = await io.ReadAsync(buffer, ct);
                if (!_loseAck) return length;
                await Task.Delay(Timeout.Infinite, ct); // the keyboard ACKed, but the reply is lost before the owner: an unknown outcome
                return 0;
            }

            public void Dispose() => io.Dispose();
        }
    }
}
