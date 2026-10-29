using System.Globalization;
using Microsoft.Extensions.Configuration;
using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Host;
using UnifiedRgb.Screens.Composition;
using UnifiedRgb.Screens.Encoding;
using UnifiedRgb.Screens.Media;
using UnifiedRgb.Screens.Protocol;
using UnifiedRgb.Screens.Runtime;
using static UnifiedRgb.Screens.Tests.BakeTelemetryTests;
using static UnifiedRgb.Screens.Tests.PreparedOverlayKit;

namespace UnifiedRgb.Screens.Tests;

/// <summary>
/// ScreensOptions.TurzxCompositeTelemetry: false by default (a verified TURZX keeps its PNG layer byte for byte); opted in, normal native
/// output of a verified TURZX composites the exact live overlay into every access unit without a trial, never cached, with ONE display
/// reading taken from the unchanged collector at most every 2 s of monotonic time and rendered at the actual wall time (a stale or missing
/// reading stays an em dash). A new assignment, mode, trial or session takes a fresh reading; an explicit trial's own BakeTelemetry flag
/// overrides the option both ways; leaving the PNG layer clears it once BEFORE the first composited access unit. Frozen-clock fake channels;
/// the picture checks decode the actual access units with the Windows inbox decoder. No throughput or optical claim.
/// </summary>
[Collection(nameof(CompositeTelemetryTests))]
public sealed class CompositeTelemetryTests
{
    private const string ConfiguredNote = "telemetry composited before encoding (2 s telemetry sample; no PNG overlay)";
    private static readonly int SampleMs = (int)ScreenWorker.TelemetrySamplePeriod.TotalMilliseconds;

    [Fact]
    public void The_option_is_false_by_default_absent_from_the_shipped_settings_and_binds_only_when_set()
    {
        new ScreensOptions().TurzxCompositeTelemetry.Should().BeFalse();
        var shipped = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        File.ReadAllText(shipped).Should().NotContain(nameof(ScreensOptions.TurzxCompositeTelemetry), "no source appsettings change");
        Bind(new ConfigurationBuilder().AddJsonFile(shipped)).TurzxCompositeTelemetry.Should().BeFalse();
        Bind(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["UnifiedRgb:Screens:Enabled"] = "true" }))
            .TurzxCompositeTelemetry.Should().BeFalse("absent means false");
        Bind(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["UnifiedRgb:Screens:TurzxCompositeTelemetry"] = "true" }))
            .TurzxCompositeTelemetry.Should().BeTrue();

        static ScreensOptions Bind(IConfigurationBuilder builder) =>
            builder.Build().GetSection(UnifiedRgbOptions.Section).Get<UnifiedRgbOptions>()!.Screens;
    }

    [Theory]
    [InlineData(ScreenFamily.Turzx, "dashboard", null, true, true, "BakedConfigured")]
    [InlineData(ScreenFamily.Turzx, "compact", null, true, true, "BakedConfigured")]
    [InlineData(ScreenFamily.Turzx, "dashboard", null, true, false, "PngLayer")] // the default: unchanged
    [InlineData(ScreenFamily.Turzx, "dashboard", null, false, true, "PngLayer")] // not individually verified
    [InlineData(ScreenFamily.Turzx, "none", null, true, true, "PngLayer")] // nothing to composite
    [InlineData(ScreenFamily.Turzx, "dashboard", false, true, true, "PngLayer")] // a legacy trial A/B-overrides the option
    [InlineData(ScreenFamily.Turzx, "dashboard", true, true, true, "BakedTrial")]
    [InlineData(ScreenFamily.Turzx, "dashboard", true, false, false, "BakedTrial")] // the trial alone
    [InlineData(ScreenFamily.Turzx, "none", true, false, false, "PngLayer")]
    [InlineData(ScreenFamily.H2, "dashboard", null, true, true, "PngLayer")] // H2 composites its own picture anyway
    [InlineData(ScreenFamily.TlLcd, "dashboard", null, true, true, "PngLayer")]
    public void The_mode_is_the_trials_own_flag_else_the_option_of_a_verified_TURZX_with_an_overlay(ScreenFamily family, string overlay,
        bool? trialBake, bool verified, bool option, string expected)
    {
        var trial = trialBake is { } bake ? new NativeVideoTrialDto("t", Devices.Turzx.Id, 1000, 30, null) { BakeTelemetry = bake } : null;
        ScreenWorker.TelemetryModeOf(new ScreensOptions { TurzxCompositeTelemetry = option }, family,
                new ScreenAssignmentDto(Devices.Turzx.Id, Overlay: overlay), trial, verified)
            .Should().Be(Enum.Parse<VideoTelemetry>(expected));
    }

    [MediaFoundationFact]
    public async Task By_default_a_verified_TURZX_keeps_the_live_png_layer_and_its_cache()
    {
        var collector = new Collector();
        using var h = await VerifiedAsync(false, collector);
        await NativeWorkerHarness.SettleAsync(() => h.DataWrites >= 1, "first access unit");
        Commands(h.Writes).Should().Equal(111, 112, 41, ScreenProtocol.PushPngCommand, 15, TurzxVideoProtocol.DataCommand, ScreenProtocol.PushPngCommand);
        await TraceAsync(h);
        var reads = collector.Reads;
        for (var i = 0; i < 6; i++) await PictureAsync(h);
        collector.Reads.Should().BeGreaterThanOrEqualTo(reads + 6, "the PNG layer still reads the collector for every picture");

        var mark = h.Writes.Length;
        collector.Set(h, 33);
        await PictureAsync(h);
        await NativeWorkerHarness.SettleAsync(() => true, "overlay");
        var written = h.Writes.Skip(mark).ToArray();
        Commands(written).Should().Equal(TurzxVideoProtocol.DataCommand, ScreenProtocol.PushPngCommand);
        written[1][512..].Should().Equal(await ExactPngAsync(h, collector.Value), "the PNG layer follows the collector at once, as before");

        var facts = Facts(h);
        facts.EncodingNote.Should().EndWith("live PNG overlay");
        facts.OverlayEncodingNote.Should().Be("RGBA8 WIC PNG");
        h.Worker.Performance.SourceFrames!.Should().Contain(r => r.CacheHit, "the PNG-layer path still reuses encoded source frames");
    }

    [MediaFoundationFact]
    public async Task Opted_in_a_verified_TURZX_composites_every_picture_without_a_trial_png_or_cache()
    {
        var collector = new Collector();
        using var h = await VerifiedAsync(true, collector);
        await NativeWorkerHarness.SettleAsync(() => h.DataWrites >= 1, "first access unit");
        h.Trial.Should().BeNull("normal output, no trial");
        Commands(h.Writes).Should().Equal(111, 112, 41, ScreenProtocol.PushPngCommand, 15, TurzxVideoProtocol.DataCommand);
        var first = await MatchAsync(h, AccessUnit(h, 0), h.Assignment, collector.Value);
        first.AssertBaked();

        await TraceAsync(h);
        for (var i = 0; i < 8; i++) await PictureAsync(h);
        var last = await MatchAsync(h, AccessUnit(h, h.DataWrites - 1), h.Assignment, collector.Value);
        last.AssertBaked();
        Capture("composite-configured-metrics.txt", $"first {first}\nlast {last}");

        WorkerPngs(h.Writes).Should().BeEmpty("no per-update PNG while composited");
        var facts = Facts(h);
        (facts.AccessUnits, facts.OverlayWrites).Should().Be((10L, 1L), "only Begin's own transparent PNG");
        facts.EncodingNote.Should().EndWith(ConfiguredNote).And.NotContainEquivalentOf("trial");
        facts.OverlayEncodingNote.Should().Be(ScreenWorker.CompositedOverlayNote).And.NotContainEquivalentOf("trial");
        var rows = h.Worker.Performance.SourceFrames!;
        rows.Should().OnlyContain(r => !r.CacheHit, "a composited picture is never served from the encoded-picture cache");
        rows.Select(r => r.SourceIndex).Distinct().Count().Should().BeLessThan(rows.Count, "the 400 ms loop repeats source frames");
        rows.Zip(rows.Skip(1)).Should().OnlyContain(p => p.First.SourceIndex != p.Second.SourceIndex, "every picture is the source's next frame");
    }

    [MediaFoundationFact]
    public async Task The_display_reading_is_taken_at_most_once_per_two_seconds_and_reused_unchanged()
    {
        var collector = new Collector();
        using var h = await VerifiedAsync(true, collector);
        await NativeWorkerHarness.SettleAsync(() => h.DataWrites >= 1, "first access unit");
        collector.Reads.Should().Be(1, "the first picture takes the first reading");
        var sampled = collector.Value!;
        var newer = collector.Set(h, 77); // the collector moves on right after the sample

        for (var ms = NativeWorkerHarness.PeriodMs; ms < SampleMs; ms += NativeWorkerHarness.PeriodMs) await PictureAsync(h);
        collector.Reads.Should().Be(1, "the 19 pictures inside 2 s reuse the one reading");
        var held = await MatchAsync(h, AccessUnit(h, h.DataWrites - 1), h.Assignment, sampled, newer);
        held.AssertBaked();
        held.Other!.Value.Should().BeGreaterThan(held.Bad + 40, "the 1.9 s picture shows the sampled reading, not the newer one");

        var data = h.DataWrites;
        await PictureAsync(h); // 2.0 s: due
        collector.Reads.Should().Be(2);
        var fresh = await MatchAsync(h, AccessUnit(h, data), h.Assignment, newer, sampled);
        fresh.AssertBaked();
        fresh.Other!.Value.Should().BeGreaterThan(fresh.Bad + 40, "the due sample shows the collector's current reading");
        Capture("composite-sample-metrics.txt", $"held {held}\nfresh {fresh}");

        collector.Set(h, 33);
        for (var ms = NativeWorkerHarness.PeriodMs; ms < SampleMs; ms += NativeWorkerHarness.PeriodMs) await PictureAsync(h);
        collector.Reads.Should().Be(2);
        await PictureAsync(h);
        collector.Reads.Should().Be(3, "one reading per 2 s at 10 pictures/s");
    }

    [MediaFoundationFact]
    public async Task A_reading_that_goes_stale_inside_a_sample_or_is_missing_shows_an_em_dash_at_the_actual_time()
    {
        var collector = new Collector(); // frozen: the collector never has anything newer than its first reading
        using var h = await VerifiedAsync(true, collector);
        await NativeWorkerHarness.SettleAsync(() => h.DataWrites >= 1, "first access unit");
        var frozen = collector.Value!;
        await PicturesForAsync(h, SampleMs);
        await PicturesForAsync(h, SampleMs);
        var sampledAt = h.Time.GetUtcNow(); // 4.0 s
        collector.Reads.Should().Be(3, "every due sample reads the collector; it simply has nothing newer");
        var dashes = frozen with { Timestamp = frozen.Timestamp - TimeSpan.FromHours(1) };

        await PicturesForAsync(h, 900); // 4.9 s: the reused reading is 4.9 s old
        collector.Reads.Should().Be(3);
        var recent = await MatchAsync(h, AccessUnit(h, h.DataWrites - 1), h.Assignment, frozen, dashes);
        recent.AssertBaked();
        recent.Other!.Value.Should().BeGreaterThan(recent.Bad + 40, "still within 5 s: the numbers");

        await PicturesForAsync(h, 200); // 5.1 s: the SAME reused reading is now stale
        collector.Reads.Should().Be(3, "staleness needs no new reading: the renderer gets the actual wall time");
        var stale = await MatchAsync(h, AccessUnit(h, h.DataWrites - 1), h.Assignment, frozen, frozen with { Timestamp = sampledAt });
        stale.AssertBaked();
        stale.Other!.Value.Should().BeGreaterThan(stale.Bad + 40, "the sample time is never stamped onto the reading: em dashes, not numbers");
        (await MatchAsync(h, AccessUnit(h, h.DataWrites - 1), h.Assignment, dashes)).Bad.Should().BeLessThan(1920 * 480 / 1000,
            "the stale picture is the em-dash picture");

        collector.Value = null;
        await PicturesForAsync(h, SampleMs);
        collector.Reads.Should().Be(4);
        var missing = await MatchAsync(h, AccessUnit(h, h.DataWrites - 1), h.Assignment, null, BakeTelemetryTests.Telemetry(h.Time.GetUtcNow(), 10));
        missing.AssertBaked();
        missing.Other!.Value.Should().BeGreaterThan(missing.Bad + 40, "a missing reading is an em dash, never invented numbers");
        Capture("composite-stale-metrics.txt", $"4.9s {recent}\n5.1s {stale}\nmissing {missing}");
    }

    [MediaFoundationFact]
    public async Task A_new_assignment_mode_trial_or_session_takes_a_fresh_reading()
    {
        var dark = 0;
        var directory = Path.Combine(Path.GetTempPath(), "urgb-screens-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var collector = new Collector();
            using var h = await VerifiedAsync(true, collector,
                c => c with { WholeCaseBlackout = () => Volatile.Read(ref dark) != 0, BacklightStateDirectory = directory });
            await NativeWorkerHarness.SettleAsync(() => h.DataWrites >= 1, "first access unit");
            collector.Reads.Should().Be(1);

            var reading = collector.Set(h, 41); // no clock advance anywhere below: always well inside a 2 s sample
            await h.PostAsync(h.Assignment with { Rotation = 180 });
            collector.Reads.Should().Be(2, "a new assignment");
            (await MatchAsync(h, AccessUnit(h, h.DataWrites - 1), h.Assignment, reading)).AssertBaked();

            collector.Set(h, 52);
            await SwitchTrialAsync(h, Baked("baked"));
            collector.Reads.Should().Be(3, "a new mode (the trial's BakeTelemetry)");
            Facts(h).OverlayEncodingNote.Should().Be(ScreenWorker.BakedOverlayNote);

            collector.Set(h, 63);
            await SwitchTrialAsync(h, Baked("baked again"));
            collector.Reads.Should().Be(4, "a new trial");

            reading = collector.Set(h, 74);
            var data = h.DataWrites;
            await ExpireTrialAsync(h);
            collector.Reads.Should().Be(5, "back to the configured mode");
            Facts(h).OverlayEncodingNote.Should().Be(ScreenWorker.CompositedOverlayNote);
            (await MatchAsync(h, AccessUnit(h, data), h.Assignment, reading)).AssertBaked();

            Volatile.Write(ref dark, 1);
            await Wait.UntilAsync(() => h.Worker.Snapshot().State == ScreenStates.Sleeping, "sleeping");
            reading = collector.Set(h, 85);
            data = h.DataWrites;
            Volatile.Write(ref dark, 0);
            h.Time.Advance(TimeSpan.FromMilliseconds(NativeWorkerHarness.PeriodMs));
            await NativeWorkerHarness.SettleAsync(() => h.DataWrites > data && h.Worker.Snapshot().State == ScreenStates.Running, "awake again");
            collector.Reads.Should().Be(6, "a new session, 100 ms after the previous reading");
            (await MatchAsync(h, AccessUnit(h, data), h.Assignment, reading)).AssertBaked();
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [MediaFoundationFact]
    public async Task A_plain_trial_overrides_the_option_and_its_expiry_clears_the_png_once_before_the_first_composited_picture()
    {
        var collector = new Collector();
        using var h = await VerifiedAsync(true, collector);
        await NativeWorkerHarness.SettleAsync(() => h.DataWrites >= 1, "first access unit");
        var transparent = BeginPng(h.Writes);

        var mark = h.Writes.Length;
        await SwitchTrialAsync(h, NativeWorkerHarness.NewTrial("plain"));
        await NativeWorkerHarness.SettleAsync(() => true, "overlay");
        Commands(h.Writes.Skip(mark)).Should().Equal(TurzxVideoProtocol.DataCommand, ScreenProtocol.PushPngCommand);
        Facts(h).EncodingNote.Should().EndWith("live PNG overlay");
        Facts(h).OverlayEncodingNote.Should().Be("RGBA8 WIC PNG");

        mark = h.Writes.Length;
        collector.Set(h, 20);
        await PictureAsync(h);
        await NativeWorkerHarness.SettleAsync(() => true, "overlay");
        var written = h.Writes.Skip(mark).ToArray();
        Commands(written).Should().Equal(TurzxVideoProtocol.DataCommand, ScreenProtocol.PushPngCommand);
        written[1][512..].Should().Equal(await ExactPngAsync(h, collector.Value), "the legacy trial's PNG follows the collector at once");

        mark = h.Writes.Length;
        var data = h.DataWrites;
        await ExpireTrialAsync(h);
        var back = h.Writes.Skip(mark).ToArray();
        Commands(back).Should().Equal(ScreenProtocol.PushPngCommand, TurzxVideoProtocol.DataCommand);
        back[0][512..].Should().Equal(transparent, "one transparent clear, BEFORE the first composited access unit");
        (await MatchAsync(h, AccessUnit(h, data), h.Assignment, collector.Value)).AssertBaked();
        Facts(h).EncodingNote.Should().EndWith(ConfiguredNote);
        Facts(h).OverlayEncodingNote.Should().Be(ScreenWorker.CompositedOverlayNote);

        mark = h.Writes.Length;
        for (var i = 0; i < 3; i++)
        {
            collector.Set(h, 30 + i);
            await PictureAsync(h);
        }

        await PicturesForAsync(h, SampleMs);
        await NativeWorkerHarness.SettleAsync(() => true, "pictures");
        Commands(h.Writes.Skip(mark)).Should().OnlyContain(c => c == TurzxVideoProtocol.DataCommand, "the clear happened exactly once");
        Commands(h.Writes).Count(c => c == 41).Should().Be(1, "one native session throughout");
        Commands(h.Writes).Should().NotContain(123, "the verified screen never stopped");
    }

    [MediaFoundationFact]
    public async Task A_baked_trial_on_an_opted_out_screen_keeps_its_trial_labels_and_its_expiry_restores_the_exact_png()
    {
        var collector = new Collector();
        using var h = await VerifiedAsync(false, collector);
        await NativeWorkerHarness.SettleAsync(() => h.DataWrites >= 1, "first access unit");
        WorkerPngs(h.Writes).Should().HaveCount(1, "the verified PNG layer");

        var mark = h.Writes.Length;
        var data = h.DataWrites;
        await SwitchTrialAsync(h, Baked("bake"));
        var entered = h.Writes.Skip(mark).ToArray();
        Commands(entered).Should().Equal(ScreenProtocol.PushPngCommand, TurzxVideoProtocol.DataCommand);
        entered[0][512..].Should().Equal(BeginPng(h.Writes), "one clear BEFORE the first baked access unit");
        (await MatchAsync(h, AccessUnit(h, data), h.Assignment, collector.Value)).AssertBaked();
        Facts(h).EncodingNote.Should().EndWith(BakedNote);
        Facts(h).OverlayEncodingNote.Should().Be(ScreenWorker.BakedOverlayNote);
        var reads = collector.Reads;
        collector.Set(h, 44);
        await PictureAsync(h);
        collector.Reads.Should().Be(reads, "the trial samples too");

        mark = h.Writes.Length;
        await ExpireTrialAsync(h);
        await NativeWorkerHarness.SettleAsync(() => true, "overlay");
        var left = h.Writes.Skip(mark).ToArray();
        Commands(left).Should().Equal(TurzxVideoProtocol.DataCommand, ScreenProtocol.PushPngCommand);
        left[1][512..].Should().Equal(await ExactPngAsync(h, collector.Value), "the exact PNG of the current overlay and live telemetry");
        Facts(h).EncodingNote.Should().EndWith("live PNG overlay");
        Facts(h).OverlayEncodingNote.Should().Be("RGBA8 WIC PNG");
        Commands(h.Writes).Count(c => c == 41).Should().Be(1, "one native session throughout");
    }

    [MediaFoundationFact]
    public async Task Overlay_none_reassignment_blackout_and_wake_keep_the_composite_behaviour_and_labels()
    {
        var dark = 0;
        var directory = Path.Combine(Path.GetTempPath(), "urgb-screens-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var collector = new Collector();
            using var h = await VerifiedAsync(true, collector,
                c => c with { WholeCaseBlackout = () => Volatile.Read(ref dark) != 0, BacklightStateDirectory = directory });
            await NativeWorkerHarness.SettleAsync(() => h.DataWrites >= 1, "first access unit");

            await h.PostAsync(h.Assignment with { Overlay = "none" });
            await NativeWorkerHarness.SettleAsync(() => true, "overlay");
            Facts(h).OverlayEncodingNote.Should().BeNull("nothing to composite: the plain no-overlay picture, as without the option");
            Facts(h).EncodingNote.Should().EndWith("live PNG overlay", "the existing no-overlay wording is unchanged");

            var data = h.DataWrites;
            await h.PostAsync(h.Assignment with { Overlay = "dashboard" });
            await NativeWorkerHarness.SettleAsync(() => true, "overlay");
            (await MatchAsync(h, AccessUnit(h, data), h.Assignment, collector.Value)).AssertBaked();
            Facts(h).OverlayEncodingNote.Should().Be(ScreenWorker.CompositedOverlayNote);
            WorkerPngs(h.Writes).Should().BeEmpty("the plane stayed transparent from Begin");

            await PictureAsync(h);
            var before = h.Writes.Length;
            Volatile.Write(ref dark, 1);
            await Wait.UntilAsync(() => h.Worker.Snapshot().State == ScreenStates.Sleeping, "sleeping");
            Commands(h.Writes.Skip(before)).Should().Equal(14, 123, 111, 114);
            h.Worker.Performance.NativeVideo.Should().BeNull();

            before = h.Writes.Length;
            data = h.DataWrites;
            Volatile.Write(ref dark, 0);
            h.Time.Advance(TimeSpan.FromMilliseconds(NativeWorkerHarness.PeriodMs));
            await NativeWorkerHarness.SettleAsync(() => h.DataWrites > data && h.Worker.Snapshot().State == ScreenStates.Running, "awake again");
            var woke = Commands(h.Writes.Skip(before));
            woke.Take(6).Should().Equal(111, 112, 41, ScreenProtocol.PushPngCommand, 15, TurzxVideoProtocol.DataCommand);
            woke.Count(c => c == ScreenProtocol.PushPngCommand).Should().Be(1, "only the new session's transparent Begin PNG");
            WorkerPngs(h.Writes).Should().BeEmpty();
            (await MatchAsync(h, AccessUnit(h, data), h.Assignment, collector.Value)).AssertBaked();
            Facts(h).EncodingNote.Should().EndWith(ConfiguredNote);
            Facts(h).OverlayEncodingNote.Should().Be(ScreenWorker.CompositedOverlayNote);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [MediaFoundationFact]
    public async Task Losing_verification_ends_the_composite_with_the_normal_stop_and_the_image_path()
    {
        var verified = 1;
        var collector = new Collector();
        using var h = await VerifiedAsync(true, collector, c => c with
        {
            NativeVideoAllowed = _ => Volatile.Read(ref verified) != 0,
            EncodingOf = _ => Volatile.Read(ref verified) != 0
                ? new ScreenEncodingSelection(ScreenEncodingPolicies.NativeH264IntraV1, false) : ScreenEncodingSelection.Legacy,
        });
        await NativeWorkerHarness.SettleAsync(() => h.DataWrites >= 1, "first access unit");
        await PictureAsync(h);

        var before = h.Writes.Length;
        Volatile.Write(ref verified, 0);
        h.Time.Advance(TimeSpan.FromMilliseconds(NativeWorkerHarness.PeriodMs));
        await NativeWorkerHarness.SettleAsync(() => h.Writes is { } all && all.Length > before + 3
            && NativeWorkerHarness.Command(all[before + 3]) == ScreenProtocol.PushPngCommand, "stop controls, then the normal image path");
        Commands(h.Writes.Skip(before).Take(4)).Should().Equal(123, 111, 114, ScreenProtocol.PushPngCommand);
        h.Worker.Performance.NativeVideo.Should().BeNull();
        WorkerPngs(h.Writes).Should().BeEmpty();
    }

    /// <summary>The composited preview is the decoded access unit of the canvas that already holds the overlay: no PNG layer on top.</summary>
    [MediaFoundationFact]
    public async Task The_composited_preview_is_the_decoded_access_unit_of_the_overlay_canvas_alone()
    {
        var result = await Sta.RunAsync(() =>
        {
            var family = Devices.Turzx.Family;
            var rotation = new ScreensOptions().TurzxLandscapeRotation;
            var assignment = new ScreenAssignmentDto(Devices.Turzx.Id, Overlay: "dashboard");
            var canvas = Nv12Rasters.SceneWithTelemetry(family.Width, family.Height);
            var telemetry = Nv12Rasters.Telemetry;
            var now = telemetry.Timestamp;
            var configured = ScreenWorker.PreviewVideo(family, assignment, canvas, telemetry, now, rotation, videoTelemetry: VideoTelemetry.BakedConfigured);
            var trial = ScreenWorker.PreviewVideo(family, assignment, canvas, telemetry, now, rotation, videoTelemetry: VideoTelemetry.BakedTrial);
            var layered = ScreenWorker.PreviewVideo(family, assignment, canvas, telemetry, now, rotation);
            using var encoder = new MfH264IntraEncoder(family.Height, family.Width, fullRange: true);
            var fresh = PixelOps.Rotate(MfH264IntraEncoder.Decode(encoder.Encode(PixelOps.Rotate(canvas, rotation)), family.Height, family.Width, fullRange: true),
                360 - rotation);
            var bad = Count(configured.Shown, fresh, out _);
            var codec = Count(configured.Shown, canvas, out var mae);
            var layer = Count(layered.Shown, configured.Shown, out _);
            return (configured.Note, TrialNote: trial.Note, LayeredNote: layered.Note, Bad: bad, Codec: codec, Mae: mae, Layer: layer);
        });
        Capture("composite-preview-metrics.txt", string.Create(CultureInfo.InvariantCulture,
            $"bad>40 vs fresh decode {result.Bad}; codec>40 vs canvas {result.Codec}; mae vs canvas {result.Mae:F3}; png-layer path differs>40 {result.Layer}"));
        result.Note.Should().Contain("decoded actual access unit with the live overlay composited before encoding (TurzxCompositeTelemetry; no PNG overlay)")
            .And.NotContainEquivalentOf("trial");
        result.TrialNote.Should().Contain("(BakeTelemetry trial; no PNG overlay)");
        result.LayeredNote.Should().Contain("decoded actual access unit + lossless live PNG overlay (RGBA8 WIC PNG)");
        result.Bad.Should().BeLessThan(1920 * 480 / 1000, "the shown picture is the decoded access unit of exactly that canvas");
        result.Mae.Should().BeGreaterThan(0, "an actual H.264 round trip, not the original canvas");
        result.Layer.Should().BeGreaterThan(1000, "the PNG-layer preview would draw the overlay a second time; the composited one does not");
    }

    [MediaFoundationTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_manager_runs_and_previews_a_verified_TURZX_in_its_configured_mode(bool composite)
    {
        await using var rig = await RunningAsync(composite);
        var assignment = (await rig.Manager.GetScreenSceneAsync()).Assignments.Single();
        await Wait.UntilAsync(async () => (await Native(rig)) is { AccessUnits: >= 3 }, "verified native pictures");
        var facts = (await Native(rig))!;
        var opens = rig.Opener.Opens;
        var preview = await rig.Manager.PreviewScreenAsync(assignment);
        rig.Opener.Opens.Should().Be(opens, "preview never opens hardware");
        if (!composite)
        {
            facts.EncodingNote.Should().EndWith("live PNG overlay");
            facts.OverlayEncodingNote.Should().Be("RGBA8 WIC PNG");
            WorkerPngs(rig.Opener.Channels.SelectMany(c => c.Writes)).Should().NotBeEmpty();
            preview.Note.Should().Contain("decoded actual access unit + lossless live PNG overlay (RGBA8 WIC PNG)");
            return;
        }

        facts.EncodingNote.Should().EndWith(ConfiguredNote);
        facts.OverlayEncodingNote.Should().Be(ScreenWorker.CompositedOverlayNote);
        facts.OverlayWrites.Should().Be(1, "only Begin's own transparent PNG");
        WorkerPngs(rig.Opener.Channels.SelectMany(c => c.Writes)).Should().BeEmpty();
        preview.Note.Should().Contain("composited before encoding (TurzxCompositeTelemetry; no PNG overlay)").And.NotContain("+ lossless");
        var plain = await rig.Manager.PreviewScreenAsync(assignment with { Overlay = "none" });
        var differs = await Sta.RunAsync(() => Count(WpfCodec.DecodePng(Convert.FromBase64String(preview.PngBase64), 1920L * 1920),
            WpfCodec.DecodePng(Convert.FromBase64String(plain.PngBase64), 1920L * 1920), out _));
        differs.Should().BeGreaterThan(1000, "the overlay is inside the decoded access unit");
    }

    [MediaFoundationFact]
    public async Task The_manager_lets_a_plain_trial_override_the_option_and_its_end_clears_once_before_the_composite()
    {
        await using var rig = await RunningAsync(true);
        var assignment = (await rig.Manager.GetScreenSceneAsync()).Assignments.Single();
        await Wait.UntilAsync(async () => (await Native(rig)) is { AccessUnits: >= 3 }, "verified native pictures");
        var path = Path.Combine(rig.DataDirectory, "screens", "scene.json");
        var original = File.ReadAllBytes(path);

        var trial = await rig.Manager.BeginNativeVideoTrialAsync(new(Devices.Turzx.Id, 60000, 15));
        trial.BakeTelemetry.Should().BeFalse();
        await Wait.UntilAsync(async () => (await Native(rig))?.EncodingNote?.EndsWith("live PNG overlay", StringComparison.Ordinal) == true, "the trial's PNG layer");
        (await rig.Manager.PreviewScreenAsync(assignment)).Note.Should().Contain("+ lossless live PNG overlay");
        await Wait.UntilAsync(() => WorkerPngs(rig.Opener.Channels.SelectMany(c => c.Writes)).Count >= 1, "the trial drew its PNG layer");

        var mark = rig.Opener.Channels.SelectMany(c => c.Writes).Count();
        await rig.Manager.EndNativeVideoTrialAsync(trial.TrialId);
        await Wait.UntilAsync(async () => (await Native(rig))?.EncodingNote?.EndsWith(ConfiguredNote, StringComparison.Ordinal) == true, "back to the composite");
        var units = (await Native(rig))!.AccessUnits;
        await Wait.UntilAsync(async () => (await Native(rig))!.AccessUnits >= units + 3, "more composited pictures");

        var writes = rig.Opener.Channels.SelectMany(c => c.Writes).ToArray();
        var transparent = BeginPng(writes);
        var after = writes.Skip(mark).ToArray();
        var clears = Enumerable.Range(0, after.Length)
            .Where(i => NativeWorkerHarness.Command(after[i]) == ScreenProtocol.PushPngCommand && after[i][512..].SequenceEqual(transparent)).ToArray();
        clears.Should().ContainSingle("exactly one clear of the drawn plane");
        var composited = Commands(after.Skip(clears[0] + 1));
        composited.Should().NotBeEmpty().And.OnlyContain(c => c == TurzxVideoProtocol.DataCommand, "the clear precedes every composited access unit");
        Commands(writes).Count(c => c == 41).Should().Be(1, "one native session throughout");
        (await rig.Manager.PreviewScreenAsync(assignment)).Note.Should().Contain("(TurzxCompositeTelemetry; no PNG overlay)");
        File.ReadAllBytes(path).Should().Equal(original, "nothing persists");
    }

    private static NativeVideoTrial Baked(string id) => NativeWorkerHarness.NewTrial(id, t => t with { BakeTelemetry = true });

    /// <summary>The unchanged collector as the worker sees it: its latest reading (or none), counting every read.</summary>
    private sealed class Collector
    {
        private PcTelemetryDto? _value = BakeTelemetryTests.Telemetry(new DateTimeOffset(2026, 10, 22, 12, 0, 0, TimeSpan.Zero), 10); // the harness start
        private int _reads;

        public PcTelemetryDto? Value { get => Volatile.Read(ref _value); set => Volatile.Write(ref _value, value); }

        public int Reads => Volatile.Read(ref _reads);

        public PcTelemetryDto? Read()
        {
            Interlocked.Increment(ref _reads);
            return Value;
        }

        public PcTelemetryDto Set(NativeWorkerHarness h, double cpu)
        {
            var reading = BakeTelemetryTests.Telemetry(h.Time.GetUtcNow(), cpu);
            Value = reading;
            return reading;
        }
    }

    /// <summary>A TURZX in NativeH264VerifiedScreenIds with its native policy (the manager's verified || trial gate), the option as given.</summary>
    private static Task<NativeWorkerHarness> VerifiedAsync(bool composite, Collector collector, Func<ScreenWorkerContext, ScreenWorkerContext>? more = null) =>
        NativeWorkerHarness.StartAsync(null, customize: c =>
        {
            c.Options.TurzxCompositeTelemetry = composite;
            c = c with
            {
                Telemetry = collector.Read,
                EncodingOf = _ => new ScreenEncodingSelection(ScreenEncodingPolicies.NativeH264IntraV1, false),
                NativeVideoAllowed = _ => true,
            };
            return more is null ? c : more(c);
        });

    /// <summary>The trial disappears without a command (expiry); the verified screen stays native and admits one picture at once.</summary>
    private static async Task ExpireTrialAsync(NativeWorkerHarness h)
    {
        var data = h.DataWrites;
        h.Trial = null;
        await NativeWorkerHarness.SettleAsync(() => h.DataWrites > data, "the first picture after the trial");
        h.DataWrites.Should().Be(data + 1);
    }

    private static async Task<byte[]> ExactPngAsync(NativeWorkerHarness h, PcTelemetryDto? telemetry)
    {
        var now = h.Time.GetUtcNow();
        var assignment = h.Assignment;
        return await Sta.RunAsync(() => ScreenWorker.PrepareOverlayPng(ScreenWorker.RenderVideoOverlay(new OverlayRenderer(), Devices.Turzx.Family,
            assignment, telemetry, now)!, assignment, new ScreensOptions().TurzxLandscapeRotation, false).Bytes);
    }

    private static async Task<NativeVideoPerformanceDto?> Native(Rig rig) => (await rig.Manager.GetScreenPerformanceAsync()).SingleOrDefault()?.NativeVideo;

    /// <summary>
    /// Records the source trace (cache hits) with a 10 fps rate trial: a lab measurement, NOT a native trial, so the configured mode still
    /// applies. The trial change resets pacing: one picture at once.
    /// </summary>
    private static async Task TraceAsync(NativeWorkerHarness h)
    {
        var data = h.DataWrites;
        h.RateTrial = new ScreenRateTrial(new("trace", [Devices.Turzx.Id], NativeWorkerHarness.Fps, 60000, false, 50), 0, long.MaxValue);
        await NativeWorkerHarness.SettleAsync(() => h.DataWrites > data, "the trace's first picture");
        h.DataWrites.Should().Be(data + 1);
    }

    /// <summary>A running manager with a verified native TURZX (compact overlay, 15 fps) and the option as given; fake ACKs only.</summary>
    private static async Task<Rig> RunningAsync(bool composite)
    {
        var rig = new Rig(o =>
        {
            Rig.AllOutput(o);
            o.NativeH264VerifiedScreenIds = [Devices.Turzx.Id];
            o.TurzxCompositeTelemetry = composite;
        }, Devices.Turzx);
        try
        {
            rig.Opener.Configure = c => c.Reply = packet =>
            {
                var header = ScreenProtocol.DecryptHeader(ScreenFamily.Turzx, packet.AsSpan(0, 512));
                var reply = new byte[512]; reply[0] = header[0]; reply[1] = 200; header.AsSpan(4, 4).CopyTo(reply.AsSpan(2)); return reply;
            };
            await rig.Manager.SetScreenDeviceSettingsAsync(new(Devices.Turzx.Id, 0, ScreenEncodingPolicies.NativeH264IntraV1));
            var asset = await rig.Manager.UploadScreenAssetAsync(new("composite", Convert.ToBase64String(Images.Gif(2, 2,
                GifFrame.Fill(0, 0, 2, 2, 1, delayCs: 10), GifFrame.Fill(0, 0, 2, 2, 2, delayCs: 10)))));
            await rig.Manager.SetScreenSceneAsync(Images.Scene(new ScreenAssignmentDto(Devices.Turzx.Id, asset.Id, Overlay: "compact", Fps: 15)));
            rig.Manager.Start();
            return rig;
        }
        catch
        {
            await rig.DisposeAsync();
            throw;
        }
    }
}

/// <summary>The composite checks encode and decode many full-size access units: run alone, never starving other tests' frozen-clock settle windows.</summary>
[CollectionDefinition(nameof(CompositeTelemetryTests), DisableParallelization = true)]
public sealed class CompositeTelemetrySerial
{
}
