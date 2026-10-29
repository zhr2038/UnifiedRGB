using System.Globalization;
using Microsoft.Extensions.Configuration;
using UnifiedRgb.Contracts;
using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Host;
using UnifiedRgb.Screens.Composition;
using UnifiedRgb.Screens.Encoding;
using UnifiedRgb.Screens.Media;
using UnifiedRgb.Screens.Protocol;
using UnifiedRgb.Screens.Runtime;
using static UnifiedRgb.Screens.Tests.PreparedOverlayKit;

namespace UnifiedRgb.Screens.Tests;

/// <summary>
/// ScreensOptions.TurzxSingleSlice: false by default (a verified TURZX keeps the encoder's default slicing byte for byte); opted in, normal
/// native output of a verified TURZX encodes every Q95 picture as one IDR slice without a trial. An active trial's own SingleSlice flag
/// overrides the option both ways (the encoder is recreated, the session is not); H2, TL and the legacy image path never see it. The worker
/// and the preview share one rule, and the preview still decodes the actual access unit. Fake channels only; no throughput claim.
/// </summary>
[Collection(nameof(CompositeTelemetryTests))]
public sealed class TurzxSingleSliceTests
{
    private const string ConfiguredNote = "telemetry composited before encoding (2 s telemetry sample; no PNG overlay)";

    /// <summary>The harness's own start reading (NativeWorkerHarness.SetTelemetry(10) at its frozen start time).</summary>
    private static readonly PcTelemetryDto Reading = BakeTelemetryTests.Telemetry(new DateTimeOffset(2026, 10, 22, 12, 0, 0, TimeSpan.Zero), 10);

    [Fact]
    public void The_option_is_false_by_default_absent_from_the_shipped_settings_and_binds_only_when_set()
    {
        new ScreensOptions().TurzxSingleSlice.Should().BeFalse();
        var shipped = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        File.ReadAllText(shipped).Should().NotContain(nameof(ScreensOptions.TurzxSingleSlice), "no source appsettings change");
        Bind(new ConfigurationBuilder().AddJsonFile(shipped)).TurzxSingleSlice.Should().BeFalse();
        Bind(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["UnifiedRgb:Screens:Enabled"] = "true" }))
            .TurzxSingleSlice.Should().BeFalse("absent means false");
        var set = Bind(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["UnifiedRgb:Screens:TurzxSingleSlice"] = "true" }));
        set.TurzxSingleSlice.Should().BeTrue();
        set.TurzxCompositeTelemetry.Should().BeFalse("independent of the composite option");

        static ScreensOptions Bind(IConfigurationBuilder builder) =>
            builder.Build().GetSection(UnifiedRgbOptions.Section).Get<UnifiedRgbOptions>()!.Screens;
    }

    [Theory]
    [InlineData(ScreenFamily.Turzx, null, true, true, true)] // the configured normal mode
    [InlineData(ScreenFamily.Turzx, null, true, false, false)] // the default: unchanged
    [InlineData(ScreenFamily.Turzx, null, false, true, false)] // not individually verified
    [InlineData(ScreenFamily.Turzx, false, true, true, false)] // a trial's own false A/B-overrides the option
    [InlineData(ScreenFamily.Turzx, false, false, true, false)]
    [InlineData(ScreenFamily.Turzx, true, true, false, true)] // a single-slice trial on an opted-out screen
    [InlineData(ScreenFamily.Turzx, true, false, false, true)] // the trial alone
    [InlineData(ScreenFamily.H2, null, true, true, false)]
    [InlineData(ScreenFamily.H2, true, true, true, false)] // never H2, even if a trial carried the flag (the manager refuses one)
    [InlineData(ScreenFamily.TlLcd, null, true, true, false)]
    public void The_slicing_is_the_trials_own_flag_else_the_option_of_a_verified_TURZX(ScreenFamily family, bool? trialSingle, bool verified,
        bool option, bool expected)
    {
        var trial = trialSingle is { } single ? new NativeVideoTrialDto("t", Devices.Turzx.Id, 1000, 30, null) { SingleSlice = single } : null;
        ScreenWorker.SingleSliceOf(new ScreensOptions { TurzxSingleSlice = option }, family, trial, verified).Should().Be(expected);
    }

    [MediaFoundationTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task A_verified_TURZX_encodes_every_normal_picture_with_the_configured_slicing_at_Q95(bool oneSlice, bool composite)
    {
        using var h = await VerifiedAsync(oneSlice, composite);
        await NativeWorkerHarness.SettleAsync(() => h.DataWrites >= 1, "first access unit");
        h.Trial.Should().BeNull("normal output, no trial");
        var first = Units(h)[0];
        Sha(first).Should().Be(Sha(await FreshAsync(h, oneSlice, composite)), "byte for byte a fresh Q95 encoder with exactly this slicing");
        Sha(first).Should().NotBe(Sha(await FreshAsync(h, !oneSlice, composite)), "the other slicing is a different access unit");
        var slices = Slices(first);
        if (oneSlice) slices.Should().Be(1, "one complete IDR slice");
        else slices.Should().BeGreaterThan(1, "the inbox encoder's default slicing (Root measured 16 live)");

        for (var i = 0; i < 5; i++) await PictureAsync(h);
        var units = Units(h);
        units.Should().HaveCount(6).And.OnlyContain(u => IdrOnly(u) && Slices(u) == slices, "every picture, cached or not, keeps the slicing");
        Facts(h).EncodingNote.Should().Be($"H.264 all-IDR Baseline Q95, {slices} slices; host/shared-clock-selected picture; "
            + (composite ? ConfiguredNote : "live PNG overlay"));
        if (composite) WorkerPngs(h.Writes).Should().BeEmpty("the composite mode is unchanged");
        else WorkerPngs(h.Writes).Should().NotBeEmpty("the PNG layer is unchanged");
        BakeTelemetryTests.Capture($"single-slice-{oneSlice}-composite-{composite}.txt", string.Create(CultureInfo.InvariantCulture,
            $"first access unit {first.Length} B, {slices} IDR slices; access units {string.Join(' ', units.Select(u => u.Length))} B"));
    }

    [MediaFoundationTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_active_trial_uses_its_own_slice_flag_both_ways_and_its_end_restores_the_configured_slicing(bool option)
    {
        using var h = await VerifiedAsync(option, composite: false);
        await NativeWorkerHarness.SettleAsync(() => h.DataWrites >= 1, "first access unit");
        var configured = Slices(Units(h)[0]);
        if (option) configured.Should().Be(1);
        else configured.Should().BeGreaterThan(1);

        var data = h.DataWrites;
        await SwitchTrialAsync(h, NativeWorkerHarness.NewTrial("ab", t => t with { SingleSlice = !option }));
        var during = Units(h)[data];
        Sha(during).Should().Be(Sha(await FreshAsync(h, !option, false)), "the trial's own flag, from a recreated Q95 encoder");
        var trialSlices = Slices(during);
        if (option) trialSlices.Should().BeGreaterThan(1, "an explicit SingleSlice=false trial overrides the option");
        else trialSlices.Should().Be(1);
        for (var i = 0; i < 4; i++) await PictureAsync(h);
        Units(h).Skip(data).Should().HaveCount(5).And.OnlyContain(u => Slices(u) == trialSlices);
        Facts(h).EncodingNote.Should().StartWith($"H.264 all-IDR Baseline Q95, {trialSlices} slices;");

        data = h.DataWrites;
        h.Trial = null; // expiry: the verified screen stays native and admits one picture at once
        await NativeWorkerHarness.SettleAsync(() => h.DataWrites > data, "the first picture after the trial");
        h.DataWrites.Should().Be(data + 1);
        Sha(Units(h)[data]).Should().Be(Sha(await FreshAsync(h, option, false)), "the configured slicing again, from a recreated encoder");
        for (var i = 0; i < 5; i++) await PictureAsync(h); // past the 400 ms loop: encoded-picture cache hits too
        Units(h).Skip(data).Should().HaveCount(6).And.OnlyContain(u => Slices(u) == configured, "no cached picture of the trial's variant");
        Facts(h).EncodingNote.Should().StartWith($"H.264 all-IDR Baseline Q95, {configured} slices;").And.EndWith("live PNG overlay");
        Commands(h.Writes).Count(c => c == 41).Should().Be(1, "one native session throughout: only the encoder is recreated");
        Commands(h.Writes).Should().NotContain(123);
    }

    /// <summary>A lab rate trial (the normal-mode rate measurement) is not a native video trial: the configured slicing and labels stay.</summary>
    [MediaFoundationFact]
    public async Task A_rate_measurement_trial_is_not_a_native_trial_and_keeps_the_configured_single_slice()
    {
        using var h = await VerifiedAsync(oneSlice: true, composite: true);
        await NativeWorkerHarness.SettleAsync(() => h.DataWrites >= 1, "first access unit");
        var data = h.DataWrites;
        h.RateTrial = new ScreenRateTrial(new("rate", [Devices.Turzx.Id], NativeWorkerHarness.Fps, 60000, false, 50), 0, long.MaxValue);
        await NativeWorkerHarness.SettleAsync(() => h.DataWrites > data, "the rate trial's first picture");
        h.DataWrites.Should().Be(data + 1);
        for (var i = 0; i < 4; i++) await PictureAsync(h);
        h.Trial.Should().BeNull();
        Units(h).Skip(data).Should().HaveCount(5).And.OnlyContain(u => Slices(u) == 1);
        Facts(h).EncodingNote.Should().Be($"H.264 all-IDR Baseline Q95, 1 slices; host/shared-clock-selected picture; {ConfiguredNote}")
            .And.NotContainEquivalentOf("trial");
        h.Worker.Performance.SourceFrames!.Should().NotBeEmpty("the measurement records its source rows").And.OnlyContain(r => !r.CacheHit);
    }

    /// <summary>The selected single-slice preview is the decoded actual single-slice access unit: equal pixels, its own size and slicing.</summary>
    [MediaFoundationFact]
    public async Task The_single_slice_preview_is_the_decoded_actual_access_unit_of_that_variant()
    {
        var r = await Sta.RunAsync(() =>
        {
            var family = Devices.Turzx.Family;
            var rotation = new ScreensOptions().TurzxLandscapeRotation;
            var assignment = new ScreenAssignmentDto(Devices.Turzx.Id, Overlay: "dashboard");
            var canvas = Nv12Rasters.SceneWithTelemetry(family.Width, family.Height);
            var telemetry = Nv12Rasters.Telemetry;
            var single = ScreenWorker.PreviewVideo(family, assignment, canvas, telemetry, telemetry.Timestamp, rotation, singleSlice: true,
                videoTelemetry: VideoTelemetry.BakedConfigured);
            var standard = ScreenWorker.PreviewVideo(family, assignment, canvas, telemetry, telemetry.Timestamp, rotation,
                videoTelemetry: VideoTelemetry.BakedConfigured);
            using var encoder = new MfH264IntraEncoder(family.Height, family.Width, fullRange: true, singleSlice: true);
            var unit = encoder.Encode(PixelOps.Rotate(canvas, rotation));
            var fresh = PixelOps.Rotate(MfH264IntraEncoder.Decode(unit, family.Height, family.Width, fullRange: true), 360 - rotation);
            return (Single: single, Standard: standard, Unit: unit, Same: single.Shown.Pixels.AsSpan().SequenceEqual(fresh.Pixels),
                Codec: BakeTelemetryTests.Count(single.Shown, canvas, out var mae), Mae: mae);
        });
        r.Same.Should().BeTrue("the shown picture is exactly the decoded single-slice access unit");
        r.Mae.Should().BeGreaterThan(0, "an actual H.264 round trip, not the original canvas");
        Slices(r.Unit).Should().Be(1);
        r.Single.Note.Should().StartWith($"H.264 Baseline all-IDR Q95, 1 slices, {r.Unit.Length} B; decoded actual access unit");
        SlicesOf(r.Standard.Note).Should().BeGreaterThan(1, "the default preview keeps the default slicing");
        BakeTelemetryTests.Capture("single-slice-preview.txt", string.Create(CultureInfo.InvariantCulture,
            $"single {r.Single.Note}\ndefault {r.Standard.Note}\ncodec>40 vs canvas {r.Codec}; mae {r.Mae:F3}"));
    }

    [MediaFoundationTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task The_manager_runs_and_previews_a_verified_TURZX_with_the_configured_slicing(bool oneSlice, bool composite)
    {
        await using var rig = await RunningAsync(oneSlice, composite);
        var assignment = (await rig.Manager.GetScreenSceneAsync()).Assignments.Single();
        await Wait.UntilAsync(async () => (await Native(rig)) is { AccessUnits: >= 3 }, "verified native pictures");
        var facts = (await Native(rig))!;
        var slices = SlicesOf(facts.EncodingNote!);
        if (oneSlice) slices.Should().Be(1);
        else slices.Should().BeGreaterThan(1);
        facts.EncodingNote.Should().EndWith(composite ? ConfiguredNote : "live PNG overlay").And.NotContainEquivalentOf("trial");
        TurzxUnits(rig).Should().NotBeEmpty().And.OnlyContain(u => IdrOnly(u) && Slices(u) == slices);

        var opens = rig.Opener.Opens;
        var preview = await rig.Manager.PreviewScreenAsync(assignment);
        rig.Opener.Opens.Should().Be(opens, "preview never opens hardware");
        preview.Note.Should().StartWith($"H.264 Baseline all-IDR Q95, {slices} slices, ").And.Contain("decoded actual access unit").And.Contain("not sent");
        preview.Note.Should().Contain(composite ? "(TurzxCompositeTelemetry; no PNG overlay)" : "+ lossless live PNG overlay (RGBA8 WIC PNG)");
    }

    [MediaFoundationTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_manager_output_and_preview_follow_a_trials_own_slice_flag_then_the_option_again(bool option)
    {
        await using var rig = await RunningAsync(option);
        var assignment = (await rig.Manager.GetScreenSceneAsync()).Assignments.Single();
        await Wait.UntilAsync(async () => (await Native(rig)) is { AccessUnits: >= 3 }, "verified native pictures");
        var configured = SlicesOf((await Native(rig))!.EncodingNote!);
        (configured == 1).Should().Be(option);
        var path = Path.Combine(rig.DataDirectory, "screens", "scene.json");
        var original = File.ReadAllBytes(path);

        var trial = await rig.Manager.BeginNativeVideoTrialAsync(new(Devices.Turzx.Id, 60000, 15) { SingleSlice = !option });
        trial.SingleSlice.Should().Be(!option);
        await Wait.UntilAsync(async () => (await Native(rig))?.EncodingNote is { } n && (SlicesOf(n) == 1) == !option, "the trial's own slicing");
        var trialSlices = SlicesOf((await Native(rig))!.EncodingNote!);
        Slices(TurzxUnits(rig)[^1]).Should().Be(trialSlices);
        SlicesOf((await rig.Manager.PreviewScreenAsync(assignment)).Note).Should().Be(trialSlices, "the preview applies the trial's own flag too");

        await rig.Manager.EndNativeVideoTrialAsync(trial.TrialId);
        await Wait.UntilAsync(async () => (await Native(rig))?.EncodingNote is { } n && SlicesOf(n) == configured, "the configured slicing again");
        Slices(TurzxUnits(rig)[^1]).Should().Be(configured);
        SlicesOf((await rig.Manager.PreviewScreenAsync(assignment)).Note).Should().Be(configured);
        File.ReadAllBytes(path).Should().Equal(original, "nothing persists");
    }

    [MediaFoundationFact]
    public async Task H2_and_the_legacy_image_path_ignore_the_option()
    {
        var units = new List<byte[]>();
        var previews = new List<ScreenPreviewDto>();
        foreach (var option in new[] { false, true })
        {
            await using var rig = new Rig(o =>
            {
                Rig.AllOutput(o);
                o.NativeH264VerifiedScreenIds = [Devices.H2.Id];
                o.TurzxSingleSlice = option;
            }, Devices.H2);
            rig.Opener.Configure = c => c.Reply = packet => Ack(ScreenFamily.H2, packet);
            await rig.Manager.SetScreenDeviceSettingsAsync(new(Devices.H2.Id, 0, ScreenEncodingPolicies.NativeH264IntraV1));
            var asset = await rig.Manager.UploadScreenAssetAsync(new("h2 still", Convert.ToBase64String(Images.Gif(2, 2, GifFrame.Fill(0, 0, 2, 2, 1, delayCs: 10)))));
            var assignment = new ScreenAssignmentDto(Devices.H2.Id, asset.Id, Overlay: "none", Fps: 15);
            await rig.Manager.SetScreenSceneAsync(Images.Scene(assignment));
            rig.Manager.Start();
            await Wait.UntilAsync(() => H2Units(rig).Length > 0, "the first H2 native picture");
            (await rig.Manager.GetScreenPerformanceAsync()).Single().NativeVideo!.EncodingNote
                .Should().Be("H2 H.264 all-IDR Baseline; host/shared-clock-selected picture; telemetry composited before encoding");
            units.Add(H2Units(rig)[0]);
            previews.Add(await rig.Manager.PreviewScreenAsync(assignment));
        }

        Sha(units[1]).Should().Be(Sha(units[0]), "the option never reaches the H2 encoder (first access units of fresh encoders)");
        previews[1].Note.Should().Be(previews[0].Note);
        previews[1].PngBase64.Should().Be(previews[0].PngBase64, "the H2 preview is unchanged");

        await using var legacy = await RunningAsync(oneSlice: true, native: false);
        var turzx = (await legacy.Manager.GetScreenSceneAsync()).Assignments.Single();
        await Wait.UntilAsync(async () => (await legacy.ScreenAsync(Devices.Turzx.Id)).State == ScreenStates.Running, "legacy image path");
        (await Native(legacy)).Should().BeNull("a verified TURZX without the native policy keeps the image path");
        TurzxUnits(legacy).Should().BeEmpty();
        (await legacy.Manager.PreviewScreenAsync(turzx)).Note.Should().NotContain("H.264").And.NotContain("slices");
    }

    /// <summary>A TURZX in NativeH264VerifiedScreenIds with its native policy (the manager's verified || trial gate), the options as given.</summary>
    private static Task<NativeWorkerHarness> VerifiedAsync(bool oneSlice, bool composite) =>
        NativeWorkerHarness.StartAsync(null, customize: c =>
        {
            c.Options.TurzxSingleSlice = oneSlice;
            c.Options.TurzxCompositeTelemetry = composite;
            return c with
            {
                EncodingOf = _ => new ScreenEncodingSelection(ScreenEncodingPolicies.NativeH264IntraV1, false),
                NativeVideoAllowed = _ => true,
            };
        });

    /// <summary>
    /// The FIRST access unit of a fresh Q95 encoder with this slicing, of the compositor's own canvas of the worker's latest picture (with
    /// its overlay when composited). A fresh encoder repeats its sequence picture by picture, so equal bytes prove the exact variant.
    /// </summary>
    private static async Task<byte[]> FreshAsync(NativeWorkerHarness h, bool oneSlice, bool composite)
    {
        var position = Facts(h).SourcePositionMs;
        var now = h.Time.GetUtcNow();
        var assignment = composite ? h.Assignment : h.Assignment with { Overlay = "none", OverlayLayout = null };
        var assets = h.Assets;
        return await Sta.RunAsync(() =>
        {
            var family = Devices.Turzx.Family;
            using var compositor = new ScreenCompositor(assets, 64L << 20);
            var composed = compositor.Compose(family, assignment, position, Reading, now);
            composed.Warning.Should().BeNull();
            using var encoder = new MfH264IntraEncoder(family.Height, family.Width, fullRange: true, singleSlice: oneSlice);
            return encoder.Encode(PixelOps.Rotate(composed.Canvas, new ScreensOptions().TurzxLandscapeRotation));
        });
    }

    private static byte[][] Units(NativeWorkerHarness h) =>
        [.. h.Writes.Where(p => NativeWorkerHarness.Command(p) == TurzxVideoProtocol.DataCommand).Select(p => p[512..])];

    private static byte[][] TurzxUnits(Rig rig) =>
        [.. rig.Opener.Channels.SelectMany(c => c.Writes).Where(p => NativeWorkerHarness.Command(p) == TurzxVideoProtocol.DataCommand).Select(p => p[512..])];

    private static byte[][] H2Units(Rig rig) =>
        [.. rig.Opener.Channels.SelectMany(c => c.Writes).Where(p => ScreenProtocol.DecryptHeader(ScreenFamily.H2, p.AsSpan(0, 512))[0] == 121).Select(p => p[512..])];

    private static int Slices(byte[] unit) => H264AnnexB.Types(unit).Count(t => t == 5);

    private static bool IdrOnly(byte[] unit) => H264AnnexB.Types(unit) is var kinds && kinds.Contains(7) && kinds.Contains(8) && kinds.Contains(5)
        && !kinds.Any(t => t is >= 1 and <= 4);

    /// <summary>The slice count of a TURZX encoding or preview note ("... Q95, N slices...").</summary>
    private static int SlicesOf(string note)
    {
        const string marker = "Q95, ";
        note.Should().Contain(marker);
        var start = note.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        return int.Parse(note.AsSpan(start, note.IndexOf(" slices", start, StringComparison.Ordinal) - start), CultureInfo.InvariantCulture);
    }

    private static byte[] Ack(ScreenFamily family, byte[] packet)
    {
        var header = ScreenProtocol.DecryptHeader(family, packet.AsSpan(0, 512));
        var reply = new byte[512]; reply[0] = header[0]; reply[1] = 200; header.AsSpan(4, 4).CopyTo(reply.AsSpan(2)); return reply;
    }

    private static async Task<NativeVideoPerformanceDto?> Native(Rig rig) => (await rig.Manager.GetScreenPerformanceAsync()).SingleOrDefault()?.NativeVideo;

    /// <summary>A running manager with a verified TURZX (compact overlay, 15 fps), the options as given and, unless told not to, its native policy.</summary>
    private static async Task<Rig> RunningAsync(bool oneSlice, bool composite = false, bool native = true)
    {
        var rig = new Rig(o =>
        {
            Rig.AllOutput(o);
            o.NativeH264VerifiedScreenIds = [Devices.Turzx.Id];
            o.TurzxSingleSlice = oneSlice;
            o.TurzxCompositeTelemetry = composite;
        }, Devices.Turzx);
        try
        {
            rig.Opener.Configure = c => c.Reply = packet => Ack(ScreenFamily.Turzx, packet);
            if (native) await rig.Manager.SetScreenDeviceSettingsAsync(new(Devices.Turzx.Id, 0, ScreenEncodingPolicies.NativeH264IntraV1));
            var asset = await rig.Manager.UploadScreenAssetAsync(new("single slice", Convert.ToBase64String(Images.Gif(2, 2,
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
