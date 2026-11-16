using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Screens.Composition;
using UnifiedRgb.Screens.Encoding;
using UnifiedRgb.Screens.Media;
using UnifiedRgb.Screens.Protocol;
using UnifiedRgb.Screens.Runtime;
using UnifiedRgb.Screens.Usb;
using Xunit.Abstractions;
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

            // 2) Compare serial vs 1-frame pipelined overlap under simulated WinUSB transfer delay (8.0 ms/frame).
            var serialReport = await MeasureSerialLoopAsync(assets, assignment, telemetry, simulatedUsbMs: 8.0, steadyFrames);
            var overlapReport = await MeasurePipelinedLoopAsync(assets, asset, assignment, telemetry, simulatedUsbMs: 8.0, steadyFrames);
            output.WriteLine(serialReport);
            output.WriteLine(overlapReport);
        }
        finally { Directory.Delete(root, recursive: true); }
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
