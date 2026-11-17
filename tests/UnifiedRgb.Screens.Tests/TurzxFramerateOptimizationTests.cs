using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Screens.Composition;
using UnifiedRgb.Screens.Encoding;
using UnifiedRgb.Screens.Media;
using UnifiedRgb.Screens.Protocol;
using UnifiedRgb.Screens.Runtime;
using UnifiedRgb.Screens.Usb;
using Vortice.MediaFoundation;
using Xunit.Abstractions;
using static Vortice.MediaFoundation.MediaFactory;
using Color = System.Windows.Media.Color;

namespace UnifiedRgb.Screens.Tests;

[Collection(nameof(CompositeTelemetryTests))]
public sealed class TurzxFramerateOptimizationTests(ITestOutputHelper output)
{
    private static readonly ScreenOverlayLayoutDto TypicalLayout = new(0.30444444444444446, 0.1688888888888889, 0.29333333333333333, 0.18666666666666668, 4, 0.0);
    private static readonly Color TypicalAccent = Color.FromRgb(0xE3, 0xBC, 0x72);

    [Fact]
    public async Task OverlayRenderer_reuses_cached_raster_within_2s_sample_with_zero_allocations_and_invalidates_cleanly()
    {
        var result = await Sta.RunAsync(() =>
        {
            var renderer = new OverlayRenderer();
            var now = HeadlessProbe.Now;
            var reading1 = new PcTelemetryDto(now, 42.1, 12L << 30, 32L << 30, 55.1, 61.2, 8L << 30, 24L << 30, "bench", null);

            var first = renderer.Render(1920, 480, OverlayShape.Rectangle, "dashboard", TypicalAccent, reading1, now, TypicalLayout);
            first.Should().NotBeNull();
            renderer.RenderCount.Should().Be(1);
            renderer.CacheHits.Should().Be(0);

            // Warm cache-hit path (same reference inside 2s window) and verify 0 managed bytes allocated per hit.
            _ = renderer.Render(1920, 480, OverlayShape.Rectangle, "dashboard", TypicalAccent, reading1, now.AddMilliseconds(16), TypicalLayout);
            var allSame = true;
            var beforeBytes = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 1; i <= 118; i++)
            {
                var hit = renderer.Render(1920, 480, OverlayShape.Rectangle, "dashboard", TypicalAccent, reading1, now.AddMilliseconds(i * 16), TypicalLayout);
                allSame &= ReferenceEquals(hit, first);
            }

            var hitAlloc = GC.GetAllocatedBytesForCurrentThread() - beforeBytes;
            allSame.Should().BeTrue();
            hitAlloc.Should().Be(0, "steady-state overlay cache hits allocate zero bytes");
            renderer.RenderCount.Should().Be(1);
            renderer.CacheHits.Should().Be(119);

            // New telemetry DTO instance whose formatted rounded text matches (42.1 -> 42.4 = "42%", 55.1 -> 55.4 = "55%"): zero re-rasterize AND zero allocations.
            var readingUnchangedRounded = reading1 with { Timestamp = now.AddSeconds(2), CpuUsagePercent = 42.4, GpuUsagePercent = 55.4 };
            var beforeRoundedBytes = GC.GetAllocatedBytesForCurrentThread();
            var hit2 = renderer.Render(1920, 480, OverlayShape.Rectangle, "dashboard", TypicalAccent, readingUnchangedRounded, now.AddSeconds(2), TypicalLayout);
            var roundedAlloc = GC.GetAllocatedBytesForCurrentThread() - beforeRoundedBytes;
            roundedAlloc.Should().Be(0, "Path 2 signature comparison uses stackalloc without heap allocations");
            ReferenceEquals(hit2, first).Should().BeTrue();
            renderer.RenderCount.Should().Be(1);
            renderer.CacheHits.Should().Be(120);

            // Changed value -> invalidates cleanly.
            var readingChanged = readingUnchangedRounded with { CpuUsagePercent = 43.0 };
            var updated = renderer.Render(1920, 480, OverlayShape.Rectangle, "dashboard", TypicalAccent, readingChanged, now.AddSeconds(2), TypicalLayout);
            ReferenceEquals(updated, first).Should().BeFalse();
            renderer.RenderCount.Should().Be(2);

            // Staleness boundary (>5s) -> invalidates to em dashes even with the same reading instance.
            var staleFrame = renderer.Render(1920, 480, OverlayShape.Rectangle, "dashboard", TypicalAccent, readingChanged, readingChanged.Timestamp.AddSeconds(5.1), TypicalLayout);
            ReferenceEquals(staleFrame, updated).Should().BeFalse();
            renderer.RenderCount.Should().Be(3);

            // Layout / dimensions / mode / accent changes all invalidate cleanly.
            var layoutChanged = renderer.Render(1920, 480, OverlayShape.Rectangle, "dashboard", TypicalAccent, readingChanged, readingChanged.Timestamp.AddSeconds(5.1), TypicalLayout with { Columns = 2 });
            var resized = renderer.Render(1280, 320, OverlayShape.Rectangle, "dashboard", TypicalAccent, readingChanged, readingChanged.Timestamp.AddSeconds(5.1), null);
            var modeChanged = renderer.Render(1920, 480, OverlayShape.Rectangle, "compact", TypicalAccent, readingChanged, readingChanged.Timestamp.AddSeconds(5.1), null);
            var accentChanged = renderer.Render(1920, 480, OverlayShape.Rectangle, "compact", Color.FromRgb(0x5D, 0xDA, 0xC8), readingChanged, readingChanged.Timestamp.AddSeconds(5.1), null);
            renderer.RenderCount.Should().Be(7);

            // PixelOps.BlendOverRegion must equal full-frame PixelOps.BlendOver byte-for-byte for an OverlayLayout layer.
            var bgFull = Nv12Rasters.Gradient(1920, 480);
            var bgRegion = bgFull.Clone();
            var region = OverlayLayouts.Pixels(TypicalLayout, 1920, 480);
            PixelOps.BlendOver(bgFull, first!, 0, 0);
            PixelOps.BlendOverRegion(bgRegion, first!, region.X, region.Y, region.Width, region.Height);
            bgRegion.Pixels.Should().Equal(bgFull.Pixels);

            return (hitAlloc, roundedAlloc, renderer.RenderCount, renderer.CacheHits);
        });

        output.WriteLine($"OverlayRenderer cache hit alloc={result.hitAlloc} B, rounded-match alloc={result.roundedAlloc} B, renders={result.RenderCount}, hits={result.CacheHits}");
    }

    [Fact]
    public void TurzxVideoProtocol_and_ScreenProtocol_match_independent_DES_and_allocate_zero_bytes_per_packet()
    {
        var chunk = new byte[4096];
        new Random(42).NextBytes(chunk);
        const uint stamp = 0x12345678u;

        // Byte-exact parity against independent DES.Create() oracle.
        byte[] LegacyTurzxHeader(byte cmd, uint s, byte val = 0, int? chunkLen = null)
        {
#pragma warning disable CA5351
            using var des = DES.Create();
            var key = "slv3tuzx"u8.ToArray();
            des.Key = key;
            var plain = new byte[500];
            plain[0] = cmd; plain[2] = 0x1A; plain[3] = 0x6D;
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(plain.AsSpan(4, 4), s);
            if (chunkLen is { } len) System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(plain.AsSpan(8, 4), len);
            else plain[8] = val;
            var block = new byte[512];
            des.EncryptCbc(plain, key, PaddingMode.Zeros).CopyTo(block, 0);
            block[510] = 0xA1; block[511] = 0x1A;
            return block;
#pragma warning restore CA5351
        }

        TurzxVideoProtocol.StopPlayback(stamp).Should().Equal(LegacyTurzxHeader(111, stamp));
        TurzxVideoProtocol.Prepare(stamp).Should().Equal(LegacyTurzxHeader(112, stamp));
        TurzxVideoProtocol.Start(stamp).Should().Equal(LegacyTurzxHeader(41, stamp));
        TurzxVideoProtocol.Cadence(stamp).Should().Equal(LegacyTurzxHeader(15, stamp, 60));
        TurzxVideoProtocol.QueueStatus(stamp).Should().Equal(LegacyTurzxHeader(122, stamp));
        TurzxVideoProtocol.EndStream(stamp).Should().Equal(LegacyTurzxHeader(123, stamp));
        TurzxVideoProtocol.StopImagePlayback(stamp).Should().Equal(LegacyTurzxHeader(114, stamp));

        var dataPacket = TurzxVideoProtocol.Data(chunk, stamp);
        dataPacket[..512].Should().Equal(LegacyTurzxHeader(121, stamp, chunkLen: chunk.Length));
        dataPacket[512..].Should().Equal(chunk);

        var reusable = new byte[512 + chunk.Length];
        TurzxVideoProtocol.WriteDataPacket(chunk, stamp, reusable).Should().Be(reusable.Length);
        reusable.Should().Equal(dataPacket);

        // Zero allocation check for WriteDataPacket + WriteControlHeader.
        var ctrlBuf = new byte[512];
        _ = TurzxVideoProtocol.WriteDataPacket(chunk, stamp, reusable);
        _ = TurzxVideoProtocol.WriteControlHeader(122, stamp, ctrlBuf);
        var beforeAlloc = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++)
        {
            _ = TurzxVideoProtocol.WriteDataPacket(chunk, stamp + (uint)i, reusable);
            _ = TurzxVideoProtocol.WriteControlHeader(122, stamp + (uint)i, ctrlBuf);
        }
        var alloc = GC.GetAllocatedBytesForCurrentThread() - beforeAlloc;
        alloc.Should().Be(0, "protocol packet encryption reuses thread-static DES and stackalloc scratch");

        // Concurrent thread-safety check for [ThreadStatic] DES across TurzxVideoProtocol and ScreenProtocol.
        Parallel.For(0, 16, tid =>
        {
            var buf = new byte[512 + chunk.Length];
            var ts = stamp + (uint)(tid * 100);
            TurzxVideoProtocol.WriteDataPacket(chunk, ts, buf);
            buf[..512].Should().Equal(LegacyTurzxHeader(121, ts, chunkLen: chunk.Length));

            foreach (var fam in new[] { ScreenFamily.TlLcd, ScreenFamily.H2, ScreenFamily.Turzx })
            {
                var plain = ScreenProtocol.BuildPlainHeader(fam, ScreenProtocol.AllowedCommands(fam)[0], ts, 1234);
                var hdr = ScreenProtocol.EncryptHeader(fam, plain);
                ScreenProtocol.DecryptHeader(fam, hdr).Should().Equal(plain);
            }
        });
    }

    [MediaFoundationFact]
    public async Task Zero_LOH_buffers_compose_rotate_and_persistent_MFT_encoder_eliminate_per_frame_LOH_and_match_output()
    {
        var result = await Sta.RunAsync(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), "urgb-turzx-loh-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var assets = new AssetStore(Path.Combine(root, "assets"), 1L << 30, 64, NullLogger.Instance);
                var framePngs = CreateTestFrames1920x480(4);
                var asset = assets.Commit("0011223344556677", "bench", new ImportResult("png", 1920, 480, framePngs, [17, 16, 17, 17]));
                var assignment = new ScreenAssignmentDto(Devices.Turzx.Id, asset.Id, Overlay: "dashboard", Fps: 60,
                    AccentColor: "#E3BC72", Fit: "contain", OverlayLayout: TypicalLayout);
                var now = HeadlessProbe.Now;
                var telemetry = new PcTelemetryDto(now, 38.4, 10L << 30, 32L << 30, 62.1, 64.0, 6L << 30, 24L << 30, "bench", null);
                var buffers = new ScreenComposeBuffers();
                var rotateBuf = new PixelFrame(480, 1920);
                var txBuf = new byte[512 + TurzxVideoProtocol.MaxChunkBytes];

                using var compositorBefore = new ScreenCompositor(assets, 64L * 1024 * 1024);
                using var compositorAfter = new ScreenCompositor(assets, 64L * 1024 * 1024);
                using var encoderSingle = new MfH264IntraEncoder(480, 1920, fullRange: true, singleSlice: true, quality: 95);
                using var encoderMulti = new MfH264IntraEncoder(480, 1920, fullRange: true, singleSlice: false, quality: 95);

                // Warm up caches & encoder so steady-state allocation compares per-frame work.
                for (var f = 0; f < 4; f++)
                {
                    var el = f * 17L;
                    var cb = compositorBefore.Compose(Devices.Turzx.Family, assignment, el, telemetry, now);
                    _ = PixelOps.Rotate(cb.Canvas, 90);
                    var ca = compositorAfter.Compose(Devices.Turzx.Family, assignment, el, telemetry, now, buffers);
                    PixelOps.RotateInto(ca.Canvas, rotateBuf, 90);
                    _ = encoderSingle.Encode(rotateBuf);
                    _ = encoderMulti.Encode(rotateBuf);
                    _ = TurzxVideoProtocol.WriteDataPacket(txBuf.AsSpan(512, 1024), (uint)(1000 + f), txBuf);
                }

                // Verify bit-for-bit identical pixel canvas & rotated frame across Before and After.
                var beforeComp = compositorBefore.Compose(Devices.Turzx.Family, assignment, 34, telemetry, now);
                var beforeRot = PixelOps.Rotate(beforeComp.Canvas, 90);
                var afterComp = compositorAfter.Compose(Devices.Turzx.Family, assignment, 34, telemetry, now, buffers);
                PixelOps.RotateInto(afterComp.Canvas, rotateBuf, 90);
                afterComp.Canvas.Pixels.Should().Equal(beforeComp.Canvas.Pixels);
                rotateBuf.Pixels.Should().Equal(beforeRot.Pixels);

                // Measure per-frame Before allocations (Compose + Rotate + legacy-like sample encode + Protocol.Data).
                long beforeTotalBytes = 0;
                const int samples = 8;
                var a0 = GC.GetAllocatedBytesForCurrentThread();
                for (var i = 0; i < samples; i++)
                {
                    var c = compositorBefore.Compose(Devices.Turzx.Family, assignment, (i % 4) * 17L, telemetry, now);
                    var r = PixelOps.Rotate(c.Canvas, 90);
                    var au = encoderSingle.Encode(r);
                    _ = TurzxVideoProtocol.Data(au, (uint)(2000 + i));
                    beforeTotalBytes += au.Length;
                }
                var beforeAllocPerFrame = (GC.GetAllocatedBytesForCurrentThread() - a0) / (double)samples;

                // Measure steady-state After non-AU allocations (Compose w/ buffers + RotateInto + persistent MFT + WriteDataPacket).
                long afterAuBytes = 0;
                var a1 = GC.GetAllocatedBytesForCurrentThread();
                for (var i = 0; i < samples; i++)
                {
                    var c = compositorAfter.Compose(Devices.Turzx.Family, assignment, (i % 4) * 17L, telemetry, now, buffers);
                    PixelOps.RotateInto(c.Canvas, rotateBuf, 90);
                    var au = encoderSingle.Encode(rotateBuf);
                    _ = TurzxVideoProtocol.WriteDataPacket(au, (uint)(3000 + i), txBuf);
                    afterAuBytes += au.Length;
                    H264AnnexB.ValidateIdrAccessUnit(au, out var sliceCount).Should().BeTrue();
                    sliceCount.Should().Be(1);
                }
                var afterTotalAlloc = GC.GetAllocatedBytesForCurrentThread() - a1;
                var afterOverheadPerFrame = (afterTotalAlloc - afterAuBytes) / (double)samples;

                // Validate multi-slice Q95 parity as well.
                var multiAu = encoderMulti.Encode(rotateBuf);
                H264AnnexB.ValidateIdrAccessUnit(multiAu, out var multiSlices).Should().BeTrue();
                multiSlices.Should().BeGreaterThan(1);

                beforeAllocPerFrame.Should().BeGreaterThan(7_000_000, "legacy path allocates Compose 3.68 MB + Rotate 3.68 MB LOH per frame");
                afterOverheadPerFrame.Should().BeLessThan(85_000, "reused buffers eliminate LOH churn outside the returned AU bytes");
                return (beforeAllocPerFrame, afterTotalAlloc / (double)samples, afterOverheadPerFrame, afterAuBytes / (double)samples, multiSlices);
            }
            finally { Directory.Delete(root, recursive: true); }
        });

        output.WriteLine($"Before alloc/frame: {result.beforeAllocPerFrame:N0} B | After total alloc/frame: {result.Item2:N0} B (AU avg: {result.Item4:N0} B, non-AU overhead: {result.afterOverheadPerFrame:N0} B) | multi-slice count={result.multiSlices}");
    }

    [MediaFoundationFact]
    public async Task Lookahead_overlaps_CPU_encode_with_USB_write_and_drops_stale_prefetched_frames()
    {
        var dir = Path.Combine(Path.GetTempPath(), "urgb-turzx-lookahead-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var frames = Enumerable.Range(0, 6).Select(k => GifFrame.Fill(0, 0, 2, 2, (byte)(k % 4), delayCs: 10)).ToArray();
            var gif = Images.Gif(2, 2, frames);
            var import = await Sta.RunAsync(() => ImageImporter.Import(gif, ImportLimits.Default));
            var assets = new AssetStore(Path.Combine(dir, "assets"), 1L << 30, 64, NullLogger.Instance);
            var asset = assets.Commit(AssetStore.IdOf(gif), "overlap-anim", import);

            var time = new ManualTime(HeadlessProbe.Now);
            var collector = new PcTelemetryDto(HeadlessProbe.Now, 25, 8L << 30, 32L << 30, 48, 59, 4L << 30, 24L << 30, "test", null);
            var opener = new FakeOpener();
            using var usbEntered = new ManualResetEventSlim(false);
            using var releaseUsb = new ManualResetEventSlim(false);
            var firstDataSeen = 0;

            opener.Configure = c =>
            {
                c.FailWrite = _ =>
                {
                    return null;
                };
                c.Reply = packet =>
                {
                    var header = ScreenProtocol.DecryptHeader(ScreenFamily.Turzx, packet.AsSpan(0, 512));
                    if (header[0] == TurzxVideoProtocol.DataCommand && Interlocked.Increment(ref firstDataSeen) == 1)
                    {
                        usbEntered.Set();
                        releaseUsb.Wait(5000); // hold ACK return for Frame 0 while Lookahead runs Frame 1 in parallel
                    }

                    var stamp = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
                    var ack = new byte[512]; ack[0] = header[0]; ack[1] = 200;
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(ack.AsSpan(2), stamp);
                    return ack;
                };
            };

            var options = new ScreensOptions { Enabled = true, NvmlTelemetry = false, TurzxCompositeTelemetry = true, TurzxSingleSlice = true };
            Rig.AllOutput(options);
            var assignment = new ScreenAssignmentDto(Devices.Turzx.Id, asset.Id, Overlay: "dashboard", Fps: 10);
            var ctx = new ScreenWorkerContext(options, opener, new FakeContention(), assets, () => collector, time, NullLogger.Instance)
            {
                EncodingOf = _ => new ScreenEncodingSelection(ScreenEncodingPolicies.NativeH264IntraV1, false),
                NativeVideoAllowed = _ => true,
            };

            using var worker = new ScreenWorker(Devices.Turzx, ctx, new WorkerCommand(assignment, false));
            usbEntered.Wait(5000).Should().BeTrue("worker reached Frame 0 Data transfer");

            // While Frame 0 USB ACK is STILL blocked, Lookahead MUST finish encoding Frame 1 in parallel!
            await Wait.UntilAsync(() => worker.LookaheadStats.Completed >= 1, "lookahead completes Frame 1 while Frame 0 USB ACK is blocked");
            var statsDuringUsb = worker.LookaheadStats;
            statsDuringUsb.Submitted.Should().BeGreaterThanOrEqualTo(1);
            statsDuringUsb.Completed.Should().BeGreaterThanOrEqualTo(1);
            statsDuringUsb.Consumed.Should().Be(0);

            releaseUsb.Set();
            await Wait.UntilAsync(() => opener.Channels.SelectMany(c => c.Writes).Any(p => ScreenProtocol.DecryptHeader(ScreenFamily.Turzx, p.AsSpan(0, 512))[0] == 121), "Frame 0 finishes");

            // Step 100ms -> Frame 1 consumes prefetched result!
            time.Advance(TimeSpan.FromMilliseconds(100));
            await Wait.UntilAsync(() => worker.LookaheadStats.Consumed >= 1, "Frame 1 consumed prefetched lookahead");

            // Trigger 1: Clock jump (+300ms skipping frames 2, 3 -> lands on frame 4) MUST drop stale Frame 2!
            var discardedBefore = worker.LookaheadStats.Discarded;
            time.Advance(TimeSpan.FromMilliseconds(300));
            await Wait.UntilAsync(() => worker.LookaheadStats.Discarded > discardedBefore
                && opener.Channels.SelectMany(c => c.Writes).Count(p => ScreenProtocol.DecryptHeader(ScreenFamily.Turzx, p.AsSpan(0, 512))[0] == 121) >= 3,
                "clock jump discarded stale Frame 2 prefetch and sent Frame 4");

            // Ensure Frame 4's lookahead (Frame 5) completed cleanly into the held slot before triggering assignment change.
            await Wait.UntilAsync(() => worker.LookaheadStats.Submitted >= 3
                && (worker.LookaheadStats.Completed + worker.LookaheadStats.Discarded) >= worker.LookaheadStats.Submitted,
                "Frame 5 lookahead settled before assignment change");

            // Trigger 2: Assignment / OverlayLayout change drops stale immediately!
            discardedBefore = worker.LookaheadStats.Discarded;
            var changedAssignment = assignment with { OverlayLayout = TypicalLayout };
            worker.Post(changedAssignment, false);
            await Wait.UntilAsync(() => worker.LookaheadStats.Discarded > discardedBefore, "assignment change discarded stale prefetch");

            // Unit verification of LookaheadMatchCriteria stale guards (clock backward jump, variant change, session change, telemetry generation).
            using var lookahead = new TurzxFrameLookahead(assets, 64L * 1024 * 1024);
            var dummySession = new FakeChannel();
            var stampSrc = new ScreenTimestamp(ScreenFamily.Turzx, () => HeadlessProbe.Now.UtcDateTime, () => HeadlessProbe.Now.UtcDateTime);
            var videoSession1 = new TurzxVideoSession(dummySession, stampSrc, () => false);
            var videoSession2 = new TurzxVideoSession(dummySession, stampSrc, () => false);
            var variant = new LookaheadEncoderVariant(true, 95, 90);
            var job = new LookaheadJob(1, videoSession1, Devices.Turzx.Family, changedAssignment with { TimelineId = "pkg:t" }, asset.Id,
                ExpectedFrameIndex: 2, ExpectedElapsedMs: 200, PeriodMs: 100, AssetDurationMs: 600, ExpectedTimelineSource: "device-clock",
                TelemetryGeneration: 7, TelemetryReading: collector, TelemetryEvalUtc: HeadlessProbe.Now, ExpectedTelemetryStale: false,
                TelemetryMode: VideoTelemetry.BakedConfigured, Variant: variant);

            var validCriteria = new LookaheadMatchCriteria(videoSession1, changedAssignment with { TimelineId = "pkg:t" }, asset.Id,
                ActualFrameIndex: 2, ActualElapsedMs: 200, PrevElapsedMs: 100, PeriodMs: 100, AssetDurationMs: 600,
                ActualTimelineSource: "device-clock", TelemetryGeneration: 7, ActualTelemetryStale: false,
                TelemetryMode: VideoTelemetry.BakedConfigured, Variant: variant);
            TurzxFrameLookahead.Matches(job, validCriteria).Should().BeTrue();
            TurzxFrameLookahead.Matches(job, validCriteria with { ActualElapsedMs = 180, PrevElapsedMs = 200 }).Should().BeFalse("backward timeline jump");
            TurzxFrameLookahead.Matches(job, validCriteria with { ActualFrameIndex = 3 }).Should().BeFalse("skipped frame index");
            TurzxFrameLookahead.Matches(job, validCriteria with { ActualTimelineSource = "apply-epoch" }).Should().BeFalse("timeline source change");
            TurzxFrameLookahead.Matches(job, validCriteria with { TelemetryGeneration = 8 }).Should().BeFalse("telemetry generation change");
            TurzxFrameLookahead.Matches(job, validCriteria with { ActualTelemetryStale = true }).Should().BeFalse("staleness boundary crossed");
            TurzxFrameLookahead.Matches(job, validCriteria with { Variant = variant with { SingleSlice = false } }).Should().BeFalse("slice variant changed");
            TurzxFrameLookahead.Matches(job, validCriteria with { Session = videoSession2 }).Should().BeFalse("session reopened");
            TurzxFrameLookahead.Matches(job, validCriteria with { TelemetryMode = VideoTelemetry.BakedTrial }).Should().BeFalse("telemetry mode changed");

            // Cancellation / shouldAbort drops immediately without returning stale frame.
            lookahead.Submit(job with { ExpectedFrameIndex = 0, ExpectedElapsedMs = 0 });
            lookahead.TryTake(validCriteria with { ActualFrameIndex = 0, ActualElapsedMs = 0, PrevElapsedMs = 0 }, () => true).Should().BeNull();
            lookahead.Stats.Discarded.Should().BeGreaterThanOrEqualTo(1);

            output.WriteLine($"Overlap stats during live worker test: {worker.LookaheadStats}");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [MediaFoundationFact]
    public async Task Turzx_host_pipeline_benchmark_before_vs_after_1920x480_dashboard_Q95()
    {
        const int steadyFrames = 30;
        var root = Path.Combine(Path.GetTempPath(), "urgb-turzx-bench-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var assets = new AssetStore(Path.Combine(root, "assets"), 1L << 30, 64, NullLogger.Instance);
            var framePngs = await Sta.RunAsync(() => CreateTestFrames1920x480(12));
            var delays = Enumerable.Range(0, 12).Select(i => i % 3 == 1 ? 16 : 17).ToArray(); // 60 Hz 200ms cycle
            var asset = assets.Commit("8899aabbccddeeff", "bench1920", new ImportResult("png", 1920, 480, framePngs, delays));
            var assignment = new ScreenAssignmentDto(Devices.Turzx.Id, asset.Id, Overlay: "dashboard", Fps: 60,
                AccentColor: "#E3BC72", Fit: "contain", OverlayLayout: TypicalLayout);
            var now = HeadlessProbe.Now;
            var telemetry = new PcTelemetryDto(now, 37.0, 12L << 30, 32L << 30, 62.0, 64.0, 8L << 30, 24L << 30, "bench", null);

            // 1) Measure isolated host CPU stage costs (Before vs After) on STA for both SingleSlice=true and SingleSlice=false.
            var stageReport = await Sta.RunAsync(() =>
            {
                var lines = new List<string>();
                foreach (var singleSlice in new[] { true, false })
                {
                    using var compBefore = new ScreenCompositor(assets, 64L * 1024 * 1024);
                    using var compAfter = new ScreenCompositor(assets, 64L * 1024 * 1024);
                    using var enc = new MfH264IntraEncoder(480, 1920, fullRange: true, singleSlice: singleSlice, quality: 95);
                    var buffers = new ScreenComposeBuffers();
                    var rotateBuf = new PixelFrame(480, 1920);
                    var txBuf = new byte[512 + TurzxVideoProtocol.MaxChunkBytes];

                    // Warm asset cache + encoder
                    for (var i = 0; i < 12; i++)
                    {
                        _ = compBefore.Compose(Devices.Turzx.Family, assignment, i * 17L, telemetry, now);
                        var ca = compAfter.Compose(Devices.Turzx.Family, assignment, i * 17L, telemetry, now, buffers);
                        PixelOps.RotateInto(ca.Canvas, rotateBuf, 90);
                        _ = enc.Encode(rotateBuf);
                    }

                    // Single cold RenderTargetBitmap rasterization cost + cache-hit cost
                    var ovRenderer = new OverlayRenderer();
                    var ovSw = Stopwatch.StartNew();
                    _ = ovRenderer.Render(1920, 480, OverlayShape.Rectangle, "dashboard", TypicalAccent, telemetry, now, TypicalLayout);
                    ovSw.Stop();
                    var coldOverlayMs = ovSw.Elapsed.TotalMilliseconds;

                    // Before per-stage measurement
                    double bCompose = 0, bRotate = 0, bEncode = 0, bProto = 0;
                    long bAllocStart = GC.GetAllocatedBytesForCurrentThread();
                    long totalAuBytes = 0;
                    for (var i = 0; i < steadyFrames; i++)
                    {
                        var el = (i % 12) * 17L;
                        var t0 = Stopwatch.GetTimestamp();
                        var res = compBefore.Compose(Devices.Turzx.Family, assignment, el, telemetry, now);
                        var t1 = Stopwatch.GetTimestamp();
                        var rot = PixelOps.Rotate(res.Canvas, 90);
                        var t2 = Stopwatch.GetTimestamp();
                        var au = enc.Encode(rot);
                        var t3 = Stopwatch.GetTimestamp();
                        _ = TurzxVideoProtocol.Data(au, (uint)(1000 + i));
                        var t4 = Stopwatch.GetTimestamp();
                        bCompose += Stopwatch.GetElapsedTime(t0, t1).TotalMilliseconds;
                        bRotate += Stopwatch.GetElapsedTime(t1, t2).TotalMilliseconds;
                        bEncode += Stopwatch.GetElapsedTime(t2, t3).TotalMilliseconds;
                        bProto += Stopwatch.GetElapsedTime(t3, t4).TotalMilliseconds;
                        totalAuBytes += au.Length;
                    }
                    var bAllocPerFrame = (GC.GetAllocatedBytesForCurrentThread() - bAllocStart) / (double)steadyFrames;

                    // After per-stage measurement
                    double aCompose = 0, aRotate = 0, aEncode = 0, aProto = 0;
                    long aAllocStart = GC.GetAllocatedBytesForCurrentThread();
                    long aTotalAuBytes = 0;
                    for (var i = 0; i < steadyFrames; i++)
                    {
                        var el = (i % 12) * 17L;
                        var t0 = Stopwatch.GetTimestamp();
                        var res = compAfter.Compose(Devices.Turzx.Family, assignment, el, telemetry, now, buffers);
                        var t1 = Stopwatch.GetTimestamp();
                        PixelOps.RotateInto(res.Canvas, rotateBuf, 90);
                        var t2 = Stopwatch.GetTimestamp();
                        var au = enc.Encode(rotateBuf);
                        var t3 = Stopwatch.GetTimestamp();
                        _ = TurzxVideoProtocol.WriteDataPacket(au, (uint)(2000 + i), txBuf);
                        var t4 = Stopwatch.GetTimestamp();
                        aCompose += Stopwatch.GetElapsedTime(t0, t1).TotalMilliseconds;
                        aRotate += Stopwatch.GetElapsedTime(t1, t2).TotalMilliseconds;
                        aEncode += Stopwatch.GetElapsedTime(t2, t3).TotalMilliseconds;
                        aProto += Stopwatch.GetElapsedTime(t3, t4).TotalMilliseconds;
                        aTotalAuBytes += au.Length;
                    }
                    var aAllocTotal = (GC.GetAllocatedBytesForCurrentThread() - aAllocStart) / (double)steadyFrames;

                    var bCpu = (bCompose + bRotate + bEncode + bProto) / steadyFrames;
                    var aCpu = (aCompose + aRotate + aEncode + aProto) / steadyFrames;
                    var avgAu = totalAuBytes / (double)steadyFrames;
                    lines.Add($"[SingleSlice={singleSlice}] ColdOverlay={coldOverlayMs:F2}ms | AvgAU={avgAu:N0}B | "
                        + $"BEFORE: compose={bCompose / steadyFrames:F2}ms rot={bRotate / steadyFrames:F2}ms enc={bEncode / steadyFrames:F2}ms proto={bProto / steadyFrames:F2}ms totalCPU={bCpu:F2}ms alloc={bAllocPerFrame:N0}B/f | "
                        + $"AFTER: compose={aCompose / steadyFrames:F2}ms rot={aRotate / steadyFrames:F2}ms enc={aEncode / steadyFrames:F2}ms proto={aProto / steadyFrames:F2}ms totalCPU={aCpu:F2}ms alloc={aAllocTotal:N0}B/f (non-AU={aAllocTotal - avgAu:N0}B/f)");
                }
                return lines;
            });

            foreach (var line in stageReport) output.WriteLine(line);

            // 2) Compare serial vs 1-slot vs Stage-5 2-slot pipelined overlap under simulated WinUSB transfer delay (8.0 ms/frame).
            var serialReport = await MeasureSerialLoopAsync(assets, assignment, telemetry, simulatedUsbMs: 8.0, steadyFrames);
            var overlapReport = await MeasurePipelinedLoopAsync(assets, asset, assignment, telemetry, simulatedUsbMs: 8.0, steadyFrames);
            var depth2Report = await MeasureDepth2LoopAsync(assets, asset, assignment, telemetry, simulatedUsbMs: 8.0, steadyFrames);
            output.WriteLine(serialReport);
            output.WriteLine(overlapReport);
            output.WriteLine(depth2Report);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [MediaFoundationFact]
    public async Task Stage5_1to1_slot_reservation_36k_wrap_and_uncached_mp4_compose_preserve_frame_sequence()
    {
        var root = Path.Combine(Path.GetTempPath(), "urgb-turzx-stage5-slots-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            // 1. O(log N) StoredAsset.FrameAt binary search matches linear oracle across 36,000 frames (600,000 ms) incl. 0-delay & loop wrap.
            var longDelays = new int[SegmentedVideo.MaxFrames];
            for (var i = 0; i < longDelays.Length; i++)
            {
                longDelays[i] = i == 500 ? 0 : (i % 3 == 2 ? 16 : 17);
            }
            var longAsset = new StoredAsset("0123456789abcdef", "long60-36k", SegmentedVideo.Format, 1920, 480, longDelays, 1);
            int LinearFrameAt(long elapsed)
            {
                var rem = (int)(Math.Max(0, elapsed) % longAsset.DurationMs);
                for (var k = 0; k < longDelays.Length; k++) { rem -= longDelays[k]; if (rem < 0) return k; }
                return longDelays.Length - 1;
            }
            foreach (var sampleMs in new long[] { 0, 16, 17, 33, 34, 49, 50, 182, 183, 184, 199, 200, 8333, 299_999, longAsset.DurationMs - 18, longAsset.DurationMs - 17, longAsset.DurationMs - 1, longAsset.DurationMs, longAsset.DurationMs + 17 })
            {
                longAsset.FrameAt(sampleMs).Should().Be(LinearFrameAt(sampleMs), "elapsed={0}", sampleMs);
            }

            // 2. Uncached 1920x480 H.264 MP4 sequence Compose allocates < 64 KB/frame and equals non-aliased reference pixel-for-pixel.
            var mp4Bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "h264-wide-initial-format.mp4"));
            var probe = await Sta.RunAsync(() => SegmentedVideo.Probe(mp4Bytes, Path.Combine(root, "probe"), CancellationToken.None));
            var assets = new AssetStore(Path.Combine(root, "assets"), 1L << 30, 64, NullLogger.Instance);
            var seqAsset = assets.CommitSequence("wide-seq", [new(mp4Bytes, probe.DurationMs), new(mp4Bytes, probe.DurationMs)], [probe, probe]);
            var seqAssignment = new ScreenAssignmentDto(Devices.Turzx.Id, seqAsset.Id, Overlay: "dashboard", Fps: 60,
                AccentColor: "#E3BC72", Fit: "contain", OverlayLayout: TypicalLayout);
            var now = HeadlessProbe.Now;
            var collector = new PcTelemetryDto(now, 37.0, 12L << 30, 32L << 30, 62.0, 64.0, 8L << 30, 24L << 30, "bench", null);

            var seqAlloc = await Sta.RunAsync(() =>
            {
                using var uncached = new ScreenCompositor(assets, cacheBytes: 0);
                using var reference = new ScreenCompositor(assets, cacheBytes: 0);
                var buffers = new ScreenComposeBuffers();
                for (var f = 0; f < 3; f++)
                {
                    var el = f * 42L;
                    var a = uncached.Compose(Devices.Turzx.Family, seqAssignment, el, collector, now, buffers);
                    var b = reference.Compose(Devices.Turzx.Family, seqAssignment, el, collector, now);
                    a.Canvas.Pixels.Should().Equal(b.Canvas.Pixels);
                }

                var beforeBytes = GC.GetAllocatedBytesForCurrentThread();
                for (var f = 3; f < 15; f++)
                {
                    _ = uncached.Compose(Devices.Turzx.Family, seqAssignment, f * 42L, collector, now, buffers);
                }
                return (GC.GetAllocatedBytesForCurrentThread() - beforeBytes) / 12.0;
            });
            seqAlloc.Should().BeLessThan(64_000, "uncached 1920x480 MP4 sequence decode + compose reuses decoder raster and ScreenComposeBuffers with zero LOH");
            output.WriteLine($"Uncached 1920x480 MP4 sequence Compose+Overlay alloc={seqAlloc:N0} B/frame");

            // 3. Direct Codex boundary proof: Frame 11 (184ms) NEVER claims candidate Frame 10 (182ms), ONLY claims Frame 11 (199ms),
            //    and taking Frame 10 later NEVER evicts earlier-Seq future Frame 11 (or wrapped long-MP4 future Frame 0).
            var delaysCfr60 = Enumerable.Range(0, 60).Select(i => i % 3 == 2 ? 16 : 17).ToArray(); // [17,17,16]: Frame 10 = 167..182, Frame 11 = 183..199
            var cfrPngs = await Sta.RunAsync(() => CreateTestFrames1920x480(2));
            var smallFrames = Enumerable.Range(0, 60).Select(i => cfrPngs[i & 1]).ToArray();
            var anim60 = assets.Commit("1122334455667788", "exact60", new ImportResult("png", 1920, 480, smallFrames, delaysCfr60));
            anim60.FrameAt(182).Should().Be(10);
            anim60.FrameAt(184).Should().Be(11);
            anim60.FrameAt(199).Should().Be(11);

            using var lookahead = new TurzxFrameLookahead(assets, 64L * 1024 * 1024);
            var dummyCh = new FakeChannel();
            var stampSrc = new ScreenTimestamp(ScreenFamily.Turzx, () => HeadlessProbe.Now.UtcDateTime, () => HeadlessProbe.Now.UtcDateTime);
            var vs = new TurzxVideoSession(dummyCh, stampSrc, () => false);
            var variant = new LookaheadEncoderVariant(true, 95, 90);
            var assign60 = new ScreenAssignmentDto(Devices.Turzx.Id, anim60.Id, Overlay: "dashboard", Fps: 60,
                AccentColor: "#E3BC72", Fit: "contain", OverlayLayout: TypicalLayout, TimelineId: "pkg:cfr60");

            LookaheadJob Job(int idx, long el, int durationMs) => new(
                lookahead.Epoch, vs, Devices.Turzx.Family, assign60, anim60.Id, idx, el, 16.667, durationMs, "device-clock",
                1, collector, now, false, VideoTelemetry.BakedConfigured, variant);
            LookaheadMatchCriteria Criteria(int idx, long el, long prevEl, int durationMs) => new(
                vs, assign60, anim60.Id, idx, el, prevEl, 16.667, durationMs, "device-clock",
                1, false, VideoTelemetry.BakedConfigured, variant);

            // 3a. Codex exact reproduction: Frame 11 @ 184 ms already completed; Schedule([Frame 10 @ 182 ms, Frame 11 @ 199 ms]).
            TurzxFrameLookahead.SamePredictedTarget(Job(11, 184, anim60.DurationMs), Job(10, 182, anim60.DurationMs)).Should().BeFalse();
            TurzxFrameLookahead.SamePredictedTarget(Job(11, 184, anim60.DurationMs), Job(11, 199, anim60.DurationMs)).Should().BeTrue();
            lookahead.Schedule([Job(11, 184, anim60.DurationMs)]);
            await Wait.UntilAsync(() => lookahead.Stats.Completed == 1, "initial Frame 11 finishes");

            lookahead.Schedule([Job(10, 182, anim60.DurationMs), Job(11, 199, anim60.DurationMs)]);
            lookahead.Stats.Discarded.Should().Be(0, "Frame 11 @ 184 ms claims Slot 1 (Frame 11 @ 199 ms) without discarding");
            lookahead.Stats.Submitted.Should().Be(2, "Slot 0 (Frame 10 @ 182 ms) is enqueued rather than aliased by Frame 11");
            await Wait.UntilAsync(() => lookahead.Stats.Completed == 2, "Frame 10 completes behind Frame 11");

            var take10 = lookahead.TryTake(Criteria(10, 182, 166, anim60.DurationMs), () => false);
            take10.Should().NotBeNull();
            take10!.Job.ExpectedFrameIndex.Should().Be(10);
            lookahead.Stats.Discarded.Should().Be(0, "consuming later-Seq Frame 10 preserves earlier-Seq future Frame 11");

            var take11 = lookahead.TryTake(Criteria(11, 199, 182, anim60.DurationMs), () => false);
            take11.Should().NotBeNull();
            take11!.Job.ExpectedFrameIndex.Should().Be(11);
            lookahead.Stats.Consumed.Should().Be(2);

            // 3b. 1-to-1 duplicate claim guard: ONE Frame 11 in pipeline can claim AT MOST one slot of [Frame 11 @ 183, Frame 11 @ 199].
            lookahead.Schedule([Job(11, 183, anim60.DurationMs)]);
            await Wait.UntilAsync(() => lookahead.Stats.Completed == 3, "single Frame 11 completes");
            var subBefore = lookahead.Stats.Submitted;
            lookahead.Schedule([Job(11, 183, anim60.DurationMs), Job(11, 199, anim60.DurationMs)]);
            lookahead.Stats.Submitted.Should().Be(subBefore + 1, "second slot requires a fresh instance once Slot 0 is claimed 1-to-1");
            await Wait.UntilAsync(() => lookahead.Stats.Completed == 4, "second Frame 11 completes");
            lookahead.TryTake(Criteria(11, 183, 166, anim60.DurationMs), () => false)!.Job.ExpectedElapsedMs.Should().Be(183);
            lookahead.TryTake(Criteria(11, 199, 183, anim60.DurationMs), () => false)!.Job.ExpectedElapsedMs.Should().Be(199);

            // 3c. Long 36,000-frame (600,000 ms) modular loop wrap: [Frame 35_999 @ 599_983, Frame 0 @ 0, Frame 1 @ 17].
            //     Reuse anim60 image store for payload, testing Matches/Schedule/IsFutureCandidate modular arithmetic with 600,000 ms duration.
            var longDur = longAsset.DurationMs;
            lookahead.Invalidate();
            var jLast = Job(59, longDur - 17, longDur);
            var jZero = Job(0, longDur + 1, longDur);
            var jOne = Job(1, 17, longDur);
            lookahead.Schedule([jLast, jZero, jOne]);
            await Wait.UntilAsync(() => lookahead.Stats.Completed >= 7, "all 3 wrap boundary jobs complete");
            lookahead.TryTake(Criteria(59, longDur - 17, longDur - 34, longDur), () => false)!.Job.ExpectedFrameIndex.Should().Be(59);
            lookahead.TryTake(Criteria(0, 0, longDur - 17, longDur), () => false)!.Job.ExpectedFrameIndex.Should().Be(0);
            lookahead.TryTake(Criteria(1, 17, 0, longDur), () => false)!.Job.ExpectedFrameIndex.Should().Be(1);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [MediaFoundationFact]
    public async Task Stage5_60fps_32clip_mp4_sequence_worker_delivers_exact_wire_frames_across_boundaries_seams_and_wrap()
    {
        var root = Path.Combine(Path.GetTempPath(), "urgb-turzx-mp4-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var clipBytes = await Sta.RunAsync(() => CreateMarked60FpsMp4Clip(root, width: 384, height: 96, frameCount: 60));
            var clipProbe = await Sta.RunAsync(() => SegmentedVideo.Probe(clipBytes, Path.Combine(root, "probe"), CancellationToken.None));
            clipProbe.DelaysMs.Should().HaveCount(60);
            clipProbe.DurationMs.Should().Be(1000);

            var assets = new AssetStore(Path.Combine(root, "assets"), 1L << 30, 64, NullLogger.Instance);
            var clips = Enumerable.Repeat(new ScreenVideoSegment(clipBytes, clipProbe.DurationMs), SegmentedVideo.MaxClips).ToArray();
            var probes = Enumerable.Repeat(clipProbe, SegmentedVideo.MaxClips).ToArray();
            var seqAsset = assets.CommitSequence("marked-60fps-32x", clips, probes);
            seqAsset.FrameCount.Should().Be(1920);
            seqAsset.DurationMs.Should().Be(32_000);

            var time = new ManualTime(HeadlessProbe.Now);
            var timeline = new SourceStepScenario.StepTimeline { Sample = new(0, "device-clock") };
            var collector = new PcTelemetryDto(time.GetUtcNow(), 37.0, 12L << 30, 32L << 30, 62.0, 64.0, 8L << 30, 24L << 30, "bench", null);
            var opener = new FakeOpener
            {
                Configure = c => c.Reply = packet =>
                {
                    var hdr = ScreenProtocol.DecryptHeader(ScreenFamily.Turzx, packet.AsSpan(0, 512));
                    var stamp = BinaryPrimitives.ReadUInt32LittleEndian(hdr.AsSpan(4));
                    var ack = new byte[512];
                    ack[0] = hdr[0];
                    ack[1] = 200;
                    BinaryPrimitives.WriteUInt32LittleEndian(ack.AsSpan(2), stamp);
                    return ack;
                }
            };

            var options = new ScreensOptions { Enabled = true, NvmlTelemetry = false, TurzxCompositeTelemetry = true, TurzxSingleSlice = true };
            Rig.AllOutput(options);
            var assignment = new ScreenAssignmentDto(Devices.Turzx.Id, seqAsset.Id, Overlay: "dashboard", Fps: 60,
                AccentColor: "#E3BC72", Fit: "contain", OverlayLayout: TypicalLayout, TimelineId: "pkg:mp4-60");
            ScreenRateTrial? rateTrial = new(new("mp4-e2e-rate", [Devices.Turzx.Id], 60, 60_000, false, 128), 0, long.MaxValue);
            var ctx = new ScreenWorkerContext(options, opener, new FakeContention(), assets,
                () => collector with { Timestamp = time.GetUtcNow() }, time, NullLogger.Instance)
            {
                EncodingOf = _ => new ScreenEncodingSelection(ScreenEncodingPolicies.NativeH264IntraV1, false),
                NativeVideoAllowed = _ => true,
                TrialOf = _ => rateTrial,
                Timeline = () => timeline,
            };

            using var worker = new ScreenWorker(Devices.Turzx, ctx, new WorkerCommand(assignment, false));

            static int DataPacketCount(FakeOpener o) =>
                o.Channels.SelectMany(ch => ch.Writes).Count(p => ScreenProtocol.DecryptHeader(ScreenFamily.Turzx, p.AsSpan(0, 512))[0] == TurzxVideoProtocol.DataCommand);

            async Task WaitSettledAsync(int expectedWrites)
            {
                await Wait.UntilAsync(() => DataPacketCount(opener) >= expectedWrites, $"write #{expectedWrites}");
                await Wait.UntilAsync(() => (worker.LookaheadStats.Completed + worker.LookaheadStats.Discarded) >= worker.LookaheadStats.Submitted,
                    $"lookahead settles after write #{expectedWrites}");
            }

            // Initial frozen frame at t = 0 ms -> Frame 0.
            await WaitSettledAsync(1);
            var sentPositions = new List<long> { 0 };
            var writeCount = 1;
            var beatIndex = 0;
            var clockMs = 0L;

            async Task StepBeatAsync(long timelinePosMs)
            {
                beatIndex++;
                var targetClockMs = (long)Math.Floor(beatIndex * 1000.0 / 60.0) + 1;
                var deltaMs = Math.Max(1, checked((int)(targetClockMs - clockMs)));
                clockMs += deltaMs;
                timeline.Sample = new(timelinePosMs, "device-clock");
                sentPositions.Add(timelinePosMs);
                time.Advance(TimeSpan.FromMilliseconds(deltaMs));
                writeCount++;
                await WaitSettledAsync(writeCount);
            }

            // Phase 1: Contiguous 60-Hz beats across start and Codex's exact boundary (150, 166, 182, 199, 216, 230 ms).
            long[] phase1Positions = [17, 34, 50, 67, 84, 100, 117, 134, 150, 166, 182, 199, 216, 230];
            foreach (var pos in phase1Positions)
            {
                await StepBeatAsync(pos);
            }

            var consumedAfterPhase1 = worker.LookaheadStats.Consumed;
            consumedAfterPhase1.Should().BeGreaterThanOrEqualTo(phase1Positions.Length - 2,
                "contiguous 60-Hz MP4 beats incl. 166->182->199->216 ms boundary hit lookahead without discarding Frame 10/11");

            // Phase 2: Seek to Clip 0 -> Clip 1 boundary (966..1033 ms) — 1 seek miss, then hits across clip seam!
            await StepBeatAsync(966);
            var consumedBeforeClipSeam = worker.LookaheadStats.Consumed;
            long[] seamPositions = [983, 1000, 1017, 1033];
            foreach (var pos in seamPositions)
            {
                await StepBeatAsync(pos);
            }
            (worker.LookaheadStats.Consumed - consumedBeforeClipSeam).Should().BeGreaterThanOrEqualTo(seamPositions.Length - 1);

            // Phase 3: Seek to Clip 31 -> Clip 0 32-clip sequence loop wrap (31966..31999 -> 16..50 ms)!
            await StepBeatAsync(31_966);
            var consumedBeforeWrap = worker.LookaheadStats.Consumed;
            long[] wrapPositions = [31_983, 31_999, 16, 33, 50];
            foreach (var pos in wrapPositions)
            {
                await StepBeatAsync(pos);
            }
            (worker.LookaheadStats.Consumed - consumedBeforeWrap).Should().BeGreaterThanOrEqualTo(wrapPositions.Length - 1);

            // Extract every command-121 access unit written to FakeOpener, H.264 decode it on STA, and verify every picture's 6-bit stripe index!
            DataPacketCount(opener).Should().Be(sentPositions.Count);
            opener.Channels.Should().HaveCount(1);
            var decodedWireFrames = await Sta.RunAsync(() => ExtractAndDecodeWireIndices([.. opener.Last.Writes]));
            var expectedGlobalFrames = sentPositions.Select(p => seqAsset.FrameAt(p)).ToArray();
            var expectedClipRelativeFrames = expectedGlobalFrames.Select(idx => idx % 60).ToArray();

            decodedWireFrames.Should().Equal(expectedClipRelativeFrames,
                "every single transmitted H.264 access unit decodes to the exact expected MP4 sequence frame index");
            decodedWireFrames.Skip(9).Take(6).Should().Equal(9, 9, 10, 11, 12, 13);

            var perfRows = worker.Performance.SourceFrames!;
            perfRows.Select(r => r.SourceIndex).Should().Equal(expectedGlobalFrames);
            worker.LookaheadStats.Errors.Should().Be(0);
            worker.Snapshot().Dropped.Should().Be(0);
            output.WriteLine($"[MP4 E2E 32-clip 60-FPS] steps={sentPositions.Count}, stats={worker.LookaheadStats}, phase1={string.Join(',', decodedWireFrames.Take(15))}");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static byte[] CreateMarked60FpsMp4Clip(string scratchDir, int width, int height, int frameCount)
    {
        var path = Path.Combine(scratchDir, "marked60-" + Guid.NewGuid().ToString("N") + ".mp4");
        MFStartup().CheckError();
        try
        {
            using var outType = MFCreateMediaType();
            outType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video).CheckError();
            outType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264).CheckError();
            MFSetAttributeSize(outType, MediaTypeAttributeKeys.FrameSize, (uint)width, (uint)height);
            MFSetAttributeRatio(outType, MediaTypeAttributeKeys.FrameRate, 60, 1);
            MFSetAttributeRatio(outType, MediaTypeAttributeKeys.PixelAspectRatio, 1, 1);
            outType.Set(MediaTypeAttributeKeys.InterlaceMode, (uint)2).CheckError();
            outType.Set(MediaTypeAttributeKeys.AvgBitrate, (uint)8_000_000).CheckError();
            outType.Set(MediaTypeAttributeKeys.Mpeg2Profile, (uint)66).CheckError();

            using var inType = MFCreateMediaType();
            inType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video).CheckError();
            inType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.Rgb32).CheckError();
            MFSetAttributeSize(inType, MediaTypeAttributeKeys.FrameSize, (uint)width, (uint)height);
            MFSetAttributeRatio(inType, MediaTypeAttributeKeys.FrameRate, 60, 1);
            MFSetAttributeRatio(inType, MediaTypeAttributeKeys.PixelAspectRatio, 1, 1);
            inType.Set(MediaTypeAttributeKeys.InterlaceMode, (uint)2).CheckError();
            inType.Set(MediaTypeAttributeKeys.DefaultStride, (uint)(width * 4)).CheckError();

            using var writer = MFCreateSinkWriterFromURL(path, null, null);
            var stream = writer.AddStream(outType);
            writer.SetInputMediaType(stream, inType, null);
            writer.BeginWriting();

            var pixels = new byte[width * height * 4];
            var stripeWidth = width / 6;
            for (var f = 0; f < frameCount; f++)
            {
                for (var y = 0; y < height; y++)
                {
                    var row = y * width * 4;
                    for (var x = 0; x < width; x++)
                    {
                        var bit = Math.Min(5, x / stripeWidth);
                        var v = ((f >> bit) & 1) != 0 ? (byte)240 : (byte)16;
                        var i = row + (x * 4);
                        pixels[i] = v;
                        pixels[i + 1] = v;
                        pixels[i + 2] = v;
                        pixels[i + 3] = 255;
                    }
                }

                using var sample = MFCreateSample();
                using var buffer = MFCreateMemoryBuffer(pixels.Length);
                buffer.Lock(out var ptr, out _, out _);
                try { Marshal.Copy(pixels, 0, ptr, pixels.Length); }
                finally { buffer.Unlock(); }
                buffer.CurrentLength = pixels.Length;
                sample.AddBuffer(buffer);
                var pts = (long)Math.Round(f * 10_000_000.0 / 60.0);
                var nextPts = (long)Math.Round((f + 1) * 10_000_000.0 / 60.0);
                sample.SampleTime = pts;
                sample.SampleDuration = nextPts - pts;
                writer.WriteSample(stream, sample);
            }

            writer.Finalize();
        }
        finally
        {
            MFShutdown().CheckError();
        }

        return File.ReadAllBytes(path);
    }

    private static List<int> ExtractAndDecodeWireIndices(IReadOnlyList<byte[]> writes)
    {
        var indices = new List<int>();
        using var au = new MemoryStream();
        foreach (var pkt in writes)
        {
            var hdr = ScreenProtocol.DecryptHeader(ScreenFamily.Turzx, pkt.AsSpan(0, 512));
            if (hdr[0] != TurzxVideoProtocol.DataCommand)
            {
                continue;
            }

            var len = BinaryPrimitives.ReadInt32BigEndian(hdr.AsSpan(8));
            au.Write(pkt, 512, len);
            if (len < TurzxVideoProtocol.MaxChunkBytes)
            {
                var decodedNative = MfH264IntraEncoder.Decode(au.ToArray(), 480, 1920, fullRange: true);
                au.SetLength(0);
                var shown = PixelOps.Rotate(decodedNative, 270);
                var idx = 0;
                for (var bit = 0; bit < 6; bit++)
                {
                    var cx = (bit * 320) + 160;
                    var (b, g, r, _) = shown.At(cx, 440);
                    if (((b + g + r) / 3) > 128)
                    {
                        idx |= 1 << bit;
                    }
                }

                indices.Add(idx);
            }
        }

        au.Length.Should().Be(0, "all access units completed without dangling tail chunks");
        return indices;
    }

    private static async Task<string> MeasureSerialLoopAsync(AssetStore assets, ScreenAssignmentDto assignment,
        PcTelemetryDto telemetry, double simulatedUsbMs, int frameCount)
    {
        var stampSrc = new ScreenTimestamp(ScreenFamily.Turzx, () => HeadlessProbe.Now.UtcDateTime, () => HeadlessProbe.Now.UtcDateTime);
        var channel = new SimulatedDelayChannel(simulatedUsbMs);
        var session = new TurzxVideoSession(channel, stampSrc, () => false);
        session.Begin(await Sta.RunAsync(() => WpfCodec.EncodePng(new PixelFrame(480, 1920))));
        var now = HeadlessProbe.Now;

        var totalMs = await Sta.RunAsync(() =>
        {
            using var comp = new ScreenCompositor(assets, 64L * 1024 * 1024);
            using var enc = new MfH264IntraEncoder(480, 1920, fullRange: true, singleSlice: true, quality: 95);
            for (var w = 0; w < 12; w++)
            {
                var c = comp.Compose(Devices.Turzx.Family, assignment, w * 17L, telemetry, now);
                _ = enc.Encode(PixelOps.Rotate(c.Canvas, 90));
            }

            var sw = Stopwatch.StartNew();
            for (var i = 0; i < frameCount; i++)
            {
                var el = (i + 2) * 17L;
                var cb = comp.Compose(Devices.Turzx.Family, assignment, el, telemetry, now);
                var rot = PixelOps.Rotate(cb.Canvas, 90);
                var au = enc.Encode(rot);
                session.SendFrame(au);
            }
            sw.Stop();
            session.Stop();
            return sw.Elapsed.TotalMilliseconds;
        });

        var avgMs = totalMs / frameCount;
        return $"[Serial Baseline @ simulated USB {simulatedUsbMs:F1}ms/frame] {frameCount} frames in {totalMs:F1}ms => avg {avgMs:F2}ms/frame ({1000.0 / avgMs:F1} host FPS)";
    }

    private static async Task<string> MeasurePipelinedLoopAsync(AssetStore assets, StoredAsset asset, ScreenAssignmentDto assignment,
        PcTelemetryDto telemetry, double simulatedUsbMs, int frameCount)
    {
        var stampSrc = new ScreenTimestamp(ScreenFamily.Turzx, () => HeadlessProbe.Now.UtcDateTime, () => HeadlessProbe.Now.UtcDateTime);
        var channel = new SimulatedDelayChannel(simulatedUsbMs);
        var session = new TurzxVideoSession(channel, stampSrc, () => false);
        session.Begin(await Sta.RunAsync(() => WpfCodec.EncodePng(new PixelFrame(480, 1920))));

        using var lookahead = new TurzxFrameLookahead(assets, 64L * 1024 * 1024);
        var buffers = new ScreenComposeBuffers();
        var rotateBuf = new PixelFrame(480, 1920);
        var now = HeadlessProbe.Now;
        var variant = new LookaheadEncoderVariant(true, 95, 90);

        // Warm Lookahead & encoder once, then drive steadyFrames pipelined iterations.
        var totalSw = await Sta.RunAsync(() =>
        {
            using var comp = new ScreenCompositor(assets, 64L * 1024 * 1024);
            using var enc = new MfH264IntraEncoder(480, 1920, fullRange: true, singleSlice: true, quality: 95);
            for (var w = 0; w < 12; w++)
            {
                var cw = comp.Compose(Devices.Turzx.Family, assignment, w * 17L, telemetry, now, buffers);
                PixelOps.RotateInto(cw.Canvas, rotateBuf, 90);
                _ = enc.Encode(rotateBuf);
            }

            lookahead.Submit(new LookaheadJob(lookahead.Epoch, session, Devices.Turzx.Family, assignment, asset.Id,
                asset.FrameAt(17), 17, 16.667, asset.DurationMs, null, 1, telemetry, now, false, VideoTelemetry.BakedConfigured, variant));
            _ = lookahead.TryTake(new LookaheadMatchCriteria(session, assignment, asset.Id, asset.FrameAt(17), 17, 0, 16.667, asset.DurationMs, null, 1, false, VideoTelemetry.BakedConfigured, variant), () => false);
            lookahead.Submit(new LookaheadJob(lookahead.Epoch, session, Devices.Turzx.Family, assignment, asset.Id,
                asset.FrameAt(34), 34, 16.667, asset.DurationMs, null, 1, telemetry, now, false, VideoTelemetry.BakedConfigured, variant));
            var consumedBefore = lookahead.Stats.Consumed;

            var sw = Stopwatch.StartNew();
            long prevEl = 17;
            for (var i = 0; i < frameCount; i++)
            {
                var el = (i + 2) * 17L;
                var idx = asset.FrameAt(el);
                var criteria = new LookaheadMatchCriteria(session, assignment, asset.Id, idx, el, prevEl, 16.667, asset.DurationMs,
                    null, 1, false, VideoTelemetry.BakedConfigured, variant);
                var hit = lookahead.TryTake(criteria, () => false);
                ScreenImage frameToSend;
                if (hit is not null)
                {
                    frameToSend = hit.Frame;
                }
                else
                {
                    var cb = comp.Compose(Devices.Turzx.Family, assignment, el, telemetry, now, buffers);
                    PixelOps.RotateInto(cb.Canvas, rotateBuf, 90);
                    frameToSend = new ScreenImage(TurzxVideoProtocol.DataCommand, enc.Encode(rotateBuf), "fallback");
                }

                var nextEl = (i + 3) * 17L;
                lookahead.Submit(new LookaheadJob(lookahead.Epoch, session, Devices.Turzx.Family, assignment, asset.Id,
                    asset.FrameAt(nextEl), nextEl, 16.667, asset.DurationMs, null, 1, telemetry, now, false, VideoTelemetry.BakedConfigured, variant));
                session.SendFrame(frameToSend.Image);
                prevEl = el;
            }
            sw.Stop();
            session.Stop();
            return (sw.Elapsed.TotalMilliseconds, consumedBefore);
        });

        var stats = lookahead.Stats;
        (stats.Consumed - totalSw.consumedBefore).Should().Be(frameCount, "all steady frames match lookahead prediction and reuse pre-encoded frames");
        var avgFrameMs = totalSw.Item1 / frameCount;
        var fps = 1000.0 / avgFrameMs;
        return $"[Pipelined Overlap @ simulated USB {simulatedUsbMs:F1}ms/frame] {frameCount} frames in {totalSw.Item1:F1}ms => avg {avgFrameMs:F2}ms/frame ({fps:F1} host FPS); stats={stats}";
    }

    private static async Task<string> MeasureDepth2LoopAsync(AssetStore assets, StoredAsset asset, ScreenAssignmentDto assignment,
        PcTelemetryDto telemetry, double simulatedUsbMs, int frameCount)
    {
        var stampSrc = new ScreenTimestamp(ScreenFamily.Turzx, () => HeadlessProbe.Now.UtcDateTime, () => HeadlessProbe.Now.UtcDateTime);
        var channel = new SimulatedDelayChannel(simulatedUsbMs);
        var session = new TurzxVideoSession(channel, stampSrc, () => false);
        session.Begin(await Sta.RunAsync(() => WpfCodec.EncodePng(new PixelFrame(480, 1920))));

        using var lookahead = new TurzxFrameLookahead(assets, 64L * 1024 * 1024);
        var buffers = new ScreenComposeBuffers();
        var rotateBuf = new PixelFrame(480, 1920);
        var now = HeadlessProbe.Now;
        var variant = new LookaheadEncoderVariant(true, 95, 90);

        var totalSw = await Sta.RunAsync(() =>
        {
            using var comp = new ScreenCompositor(assets, 64L * 1024 * 1024);
            using var enc = new MfH264IntraEncoder(480, 1920, fullRange: true, singleSlice: true, quality: 95);
            for (var w = 0; w < 12; w++)
            {
                var cw = comp.Compose(Devices.Turzx.Family, assignment, w * 17L, telemetry, now, buffers);
                PixelOps.RotateInto(cw.Canvas, rotateBuf, 90);
                _ = enc.Encode(rotateBuf);
            }

            LookaheadJob JobAt(long el) => new(lookahead.Epoch, session, Devices.Turzx.Family, assignment, asset.Id,
                asset.FrameAt(el), el, 16.667, asset.DurationMs, null, 1, telemetry, now, false, VideoTelemetry.BakedConfigured, variant);

            lookahead.Schedule([JobAt(17), JobAt(34)]);
            _ = lookahead.TryTake(new LookaheadMatchCriteria(session, assignment, asset.Id, asset.FrameAt(17), 17, 0, 16.667, asset.DurationMs, null, 1, false, VideoTelemetry.BakedConfigured, variant), () => false);
            lookahead.Schedule([JobAt(34), JobAt(51)]);
            var consumedBefore = lookahead.Stats.Consumed;

            var sw = Stopwatch.StartNew();
            long prevEl = 17;
            for (var i = 0; i < frameCount; i++)
            {
                var el = (i + 2) * 17L;
                var idx = asset.FrameAt(el);
                var criteria = new LookaheadMatchCriteria(session, assignment, asset.Id, idx, el, prevEl, 16.667, asset.DurationMs,
                    null, 1, false, VideoTelemetry.BakedConfigured, variant);
                var hit = lookahead.TryTake(criteria, () => false);
                ScreenImage frameToSend;
                if (hit is not null)
                {
                    frameToSend = hit.Frame;
                }
                else
                {
                    var cb = comp.Compose(Devices.Turzx.Family, assignment, el, telemetry, now, buffers);
                    PixelOps.RotateInto(cb.Canvas, rotateBuf, 90);
                    frameToSend = new ScreenImage(TurzxVideoProtocol.DataCommand, enc.Encode(rotateBuf), "fallback");
                }

                lookahead.Schedule([JobAt((i + 3) * 17L), JobAt((i + 4) * 17L)]);
                session.SendFrame(frameToSend.Image);
                prevEl = el;
            }
            sw.Stop();
            session.Stop();
            return (sw.Elapsed.TotalMilliseconds, consumedBefore);
        });

        var stats = lookahead.Stats;
        (stats.Consumed - totalSw.consumedBefore).Should().Be(frameCount, "all steady depth-2 frames hit pre-encoded lookahead");
        var avgFrameMs = totalSw.Item1 / frameCount;
        var fps = 1000.0 / avgFrameMs;
        return $"[Offline PNG+SimulatedUSB Depth-2 Benchmark (not MP4 60FPS acceptance) @ {simulatedUsbMs:F1}ms] {frameCount} frames in {totalSw.Item1:F1}ms => avg {avgFrameMs:F2}ms/frame ({fps:F1} host FPS); stats={stats}";
    }

    private static List<byte[]> CreateTestFrames1920x480(int count)
    {
        var list = new List<byte[]>(count);
        for (var k = 0; k < count; k++)
        {
            var pf = new PixelFrame(1920, 480);
            var px = pf.Pixels;
            var shift = (byte)(k * 19);
            for (var y = 0; y < 480; y++)
            {
                var rowBase = y * 1920 * 4;
                var yByte = (byte)((y + shift) & 0xFF);
                for (var x = 0; x < 1920; x++)
                {
                    var idx = rowBase + (x * 4);
                    px[idx] = (byte)((x + shift) & 0xFF);
                    px[idx + 1] = yByte;
                    px[idx + 2] = (byte)(((x ^ y) + shift) & 0xFF);
                    px[idx + 3] = 255;
                }
            }

            list.Add(WpfCodec.EncodePng(pf));
        }

        return list;
    }

    private sealed class SimulatedDelayChannel(double usbMs) : IScreenUsbChannel, IScreenUsbDiagnostics
    {
        private readonly Queue<byte[]> _packets = new();
        private byte _cmd;
        private uint _stamp;
        public byte OutEndpoint => 0x01;
        public byte InEndpoint => 0x81;
        public int? DeviceSpeedCode => 3;
        public ScreenReadCounts LastRead { get; private set; }
        public int ReplyQuietMs { private get; set; } = 2;
        public bool? CorrelatedReplies { private get; set; }
        public bool ExactReplyPair { private get; set; }
        public void ExpectResponse(byte command, uint timestamp) { _cmd = command; _stamp = timestamp; }

        public void Write(ReadOnlySpan<byte> data, int timeoutMs)
        {
            var hdr = ScreenProtocol.DecryptHeader(ScreenFamily.Turzx, data[..512]);
            var cmd = hdr[0];
            var stamp = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(hdr.AsSpan(4));
            if (cmd == TurzxVideoProtocol.DataCommand)
            {
                SpinWaitFor(usbMs);
            }
            var ack = new byte[512]; ack[0] = cmd; ack[1] = 200;
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(ack.AsSpan(2), stamp);
            _packets.Enqueue(ack);
            _packets.Enqueue([]);
        }

        public int Read(Span<byte> buffer, int timeoutMs)
        {
            LastRead = new(0, 0, 0, Correlated: CorrelatedReplies == true);
            return CorrelatedReplies == true && ExactReplyPair
                ? ScreenReplyFlow.ReadExactPair(ReadRaw, buffer, timeoutMs)
                : ScreenReplyFlow.ReadAndDrain(ReadRaw, buffer, timeoutMs, ReplyQuietMs);
        }

        private ScreenPipeRead ReadRaw(Span<byte> buffer, int timeoutMs)
        {
            LastRead = LastRead with { Reads = LastRead.Reads + 1 };
            if (!_packets.TryDequeue(out var pkt))
            {
                LastRead = LastRead with { Timeouts = LastRead.Timeouts + 1 };
                return new(0, true);
            }
            pkt.CopyTo(buffer);
            var match = ScreenReplyFlow.MatchesImageReply(pkt, _cmd, _stamp);
            LastRead = LastRead with { ZeroLength = LastRead.ZeroLength + (pkt.Length == 0 ? 1 : 0), Matched = LastRead.Matched + (match ? 1 : 0) };
            return new(pkt.Length, false, match);
        }

        private static void SpinWaitFor(double ms)
        {
            var start = Stopwatch.GetTimestamp();
            var targetTicks = (long)(ms * Stopwatch.Frequency / 1000.0);
            while (Stopwatch.GetTimestamp() - start < targetTicks)
            {
                Thread.SpinWait(64);
            }
        }

        public void Abort() { }
        public void Dispose() { }
    }
}
