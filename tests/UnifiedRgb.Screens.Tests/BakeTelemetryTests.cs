using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using UnifiedRgb.Contracts;
using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Screens.Composition;
using UnifiedRgb.Screens.Encoding;
using UnifiedRgb.Screens.Media;
using UnifiedRgb.Screens.Protocol;
using UnifiedRgb.Screens.Runtime;
using static UnifiedRgb.Screens.Tests.PreparedOverlayKit;

namespace UnifiedRgb.Screens.Tests;

/// <summary>
/// The temporary TURZX BakeTelemetry native trial: an additive, default-false, JSON-omitted flag on the existing trial records; TURZX-only
/// and refused with the PNG-overlay lab flags before any I/O; and, in the worker, the compositor's exact live overlay inside the access
/// unit instead of the separate PNG layer (no encoded-picture cache, no per-update PNG writes, one transparent clear before the first baked
/// access unit when a session that drew the PNG layer switches into it, the exact inline PNG again when it ends) with a display reading
/// sampled at most every 2 s (<see cref="CompositeTelemetryTests"/> covers the sampling and the configured mode). Frozen-clock fake
/// channels only; the picture checks decode the actual access units with the Windows inbox decoder. BAKE_CAPTURE_DIR also writes the
/// measured picture metrics.
/// </summary>
public sealed class BakeTelemetryTests
{
    private static readonly JsonSerializerOptions Web = ContractsJson.Configure(new JsonSerializerOptions());
    private const string PinnedDefaultTranscriptSha = "4C8C54D3E0275152AE763FFC3AE099DF56AE10BF30DD1B37EF72178379313AE3"; // NativeVideoDefaultOffTests
    internal const string BakedNote = "telemetry composited before encoding (BakeTelemetry trial; 2 s telemetry sample; no PNG overlay)";

    private static int _pictures;

    private static NativeVideoTrial Baked(string id) => NativeWorkerHarness.NewTrial(id, t => t with { BakeTelemetry = true });

    [Fact]
    public void The_flag_is_additive_default_false_omitted_from_JSON_and_round_trips()
    {
        var request = new NativeVideoTrialRequestDto("s");
        var info = new NativeVideoTrialDto("t", "s", 10000, 30, null) { Quality = 95 };
        foreach (var json in new[]
        {
            JsonSerializer.Serialize(request, Web), JsonSerializer.Serialize(info, Web),
            JsonSerializer.Serialize(request, ContractsJsonContext.Default.NativeVideoTrialRequestDto),
            JsonSerializer.Serialize(info, ContractsJsonContext.Default.NativeVideoTrialDto),
        })
        {
            json.Should().NotContainEquivalentOf("bakeTelemetry");
        }

        JsonSerializer.Deserialize<NativeVideoTrialRequestDto>("{\"screenId\":\"s\"}", Web)!.BakeTelemetry.Should().BeFalse();
        JsonSerializer.Deserialize("{\"trialId\":\"t\",\"screenId\":\"s\",\"durationMs\":1000,\"fps\":30}", ContractsJsonContext.Default.NativeVideoTrialDto)!
            .BakeTelemetry.Should().BeFalse();

        var baked = request with { BakeTelemetry = true };
        var text = JsonSerializer.Serialize(baked, Web);
        text.Should().Contain("\"bakeTelemetry\":true");
        JsonSerializer.Deserialize<NativeVideoTrialRequestDto>(text, Web).Should().Be(baked);
        JsonSerializer.Deserialize(JsonSerializer.Serialize(baked, ContractsJsonContext.Default.NativeVideoTrialRequestDto),
            ContractsJsonContext.Default.NativeVideoTrialRequestDto).Should().Be(baked);
        var bakedInfo = info with { BakeTelemetry = true };
        JsonSerializer.Deserialize(JsonSerializer.Serialize(bakedInfo, ContractsJsonContext.Default.NativeVideoTrialDto),
            ContractsJsonContext.Default.NativeVideoTrialDto).Should().Be(bakedInfo);
        JsonSerializer.Deserialize<NativeVideoTrialDto>(JsonSerializer.Serialize(bakedInfo, Web), Web).Should().Be(bakedInfo);

        typeof(NativeVideoTrialRequestDto).GetConstructors().Should().ContainSingle(c => c.GetParameters().Length == 4, "the primary constructor is unchanged");
        typeof(NativeVideoTrialDto).GetConstructors().Should().ContainSingle(c => c.GetParameters().Length == 5);
        foreach (var type in new[] { typeof(NativeVideoTrialRequestDto), typeof(NativeVideoTrialDto) })
        {
            var setter = type.GetProperty(nameof(NativeVideoTrialDto.BakeTelemetry))!.SetMethod!;
            setter.ReturnParameter.GetRequiredCustomModifiers().Should().Contain(typeof(System.Runtime.CompilerServices.IsExternalInit), "init-only, like the other lab flags");
        }
    }

    [Theory]
    [InlineData("H2")]
    [InlineData("TL")]
    public async Task Baked_telemetry_is_refused_outside_TURZX_before_any_IO(string which)
    {
        var device = which == "H2" ? Devices.H2 : Devices.Tl1;
        await using var rig = new Rig(Rig.AllOutput, device);
        var refused = FluentActions.Awaiting(() => rig.Manager.BeginNativeVideoTrialAsync(new(device.Id) { BakeTelemetry = true }));
        if (which == "H2") await refused.Should().ThrowAsync<ArgumentException>().WithMessage("*TURZX-only*");
        else await refused.Should().ThrowAsync<ArgumentException>();
        rig.Opener.Opens.Should().Be(0);
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public async Task Baked_telemetry_cannot_be_combined_with_a_png_overlay_lab_flag(bool prepared, bool measure, bool simple)
    {
        await using var rig = new Rig(Rig.AllOutput, Devices.Turzx);
        await FluentActions.Awaiting(() => rig.Manager.BeginNativeVideoTrialAsync(new(Devices.Turzx.Id)
                { BakeTelemetry = true, PreparedOverlay = prepared, MeasureQueueAfterOverlay = measure, SimpleOverlayPng = simple }))
            .Should().ThrowAsync<ArgumentException>().WithMessage("*cannot be combined*");
        rig.Opener.Opens.Should().Be(0);
    }

    /// <summary>Manager → stored trial → worker: the returned trial carries the flag, the session writes no overlay PNG, nothing persists.</summary>
    [MediaFoundationFact]
    public async Task The_manager_forwards_the_flag_and_the_running_TURZX_writes_no_overlay_png_during_the_trial()
    {
        await using var rig = new Rig(Rig.AllOutput, Devices.Turzx);
        rig.Opener.Configure = c => c.Reply = packet =>
        {
            var header = ScreenProtocol.DecryptHeader(ScreenFamily.Turzx, packet.AsSpan(0, 512));
            var reply = new byte[512]; reply[0] = header[0]; reply[1] = 200; header.AsSpan(4, 4).CopyTo(reply.AsSpan(2)); return reply;
        };
        var asset = await rig.Manager.UploadScreenAssetAsync(new("baked", Convert.ToBase64String(Images.Gif(2, 2,
            GifFrame.Fill(0, 0, 2, 2, 1, delayCs: 10), GifFrame.Fill(0, 0, 2, 2, 2, delayCs: 10)))));
        var scene = Images.Scene(new ScreenAssignmentDto(Devices.Turzx.Id, asset.Id, Overlay: "compact", Fps: 15));
        await rig.Manager.SetScreenSceneAsync(scene);
        rig.Manager.Start();
        await Wait.UntilAsync(async () => (await rig.ScreenAsync(Devices.Turzx.Id)).State == ScreenStates.Running, "initial image path");
        var path = Path.Combine(rig.DataDirectory, "screens", "scene.json");
        var original = File.ReadAllBytes(path);
        var trial = await rig.Manager.BeginNativeVideoTrialAsync(new(Devices.Turzx.Id, 60000, 15) { BakeTelemetry = true });
        (trial.BakeTelemetry, trial.PreparedOverlay, trial.SimpleOverlayPng, trial.Quality).Should().Be((true, false, false, 95));
        await Wait.UntilAsync(async () => (await rig.Manager.GetScreenPerformanceAsync()).Single().NativeVideo is { AccessUnits: >= 3 }, "baked pictures");
        var facts = (await rig.Manager.GetScreenPerformanceAsync()).Single().NativeVideo!;
        facts.EncodingNote.Should().EndWith(BakedNote);
        facts.OverlayEncodingNote.Should().Be(ScreenWorker.BakedOverlayNote);
        (facts.OverlayWrites, facts.PreparedOverlay, facts.LastOverlayQueue).Should().Be((1L, null, null), "only Begin's own transparent PNG");
        WorkerPngs(rig.Opener.Channels.SelectMany(c => c.Writes)).Should().BeEmpty();
        await rig.Manager.EndNativeVideoTrialAsync(trial.TrialId);
        await Wait.UntilAsync(async () => (await rig.Manager.GetScreenPerformanceAsync()).Single().NativeVideo is null, "ordinary image restored");
        File.ReadAllBytes(path).Should().Equal(original);
        (await rig.Manager.GetScreenSceneAsync()).Should().BeEquivalentTo(scene);
    }

    [MediaFoundationTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_false_flag_is_inert_and_a_baked_transcript_has_no_worker_png(bool bake)
    {
        var text = await NativeVideoDefaultOffTests.TranscriptAsync(t => t with { BakeTelemetry = bake });
        Capture($"transcript-bake-{bake}.txt", text);
        var sha = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));
        if (!bake)
        {
            sha.Should().Be(PinnedDefaultTranscriptSha, "an explicit false flag is the pre-S1b default byte for byte");
            return;
        }

        sha.Should().NotBe(PinnedDefaultTranscriptSha);
        var lines = text.Split('\n');
        var start = Array.FindIndex(lines, l => l.StartsWith("15 ", StringComparison.Ordinal)); // Begin's cadence control
        var end = Array.IndexOf(lines, "# trial ended");
        lines.Take(start).Count(l => l.StartsWith("102 ", StringComparison.Ordinal)).Should().Be(1, "Begin's own transparent PNG");
        lines[start..end].Should().NotContain(l => l.StartsWith("102 ", StringComparison.Ordinal), "no worker PNG at begin, telemetry changes, overlay none/dashboard or rotation");
        lines.Count(l => l.Contains(BakedNote + " | " + ScreenWorker.BakedOverlayNote, StringComparison.Ordinal)).Should().Be(14,
            "begin, nine steps, dashboard again, telemetry 77, rotation 180 and its step; the two overlay-none pictures bake nothing");
    }

    [MediaFoundationFact]
    public async Task Every_baked_access_unit_carries_the_exact_live_overlay_and_no_picture_is_cached()
    {
        var live = new Live();
        using var h = await StartAsync(Baked("bake"), live);
        await NativeWorkerHarness.SettleAsync(() => h.DataWrites >= 1, "first access unit");
        Commands(h.Writes).Should().Equal(111, 112, 41, ScreenProtocol.PushPngCommand, 15, TurzxVideoProtocol.DataCommand);
        var metrics = new List<string>();
        var first = await MatchAsync(h, AccessUnit(h, 0), h.Assignment, live.Value);
        metrics.Add("begin " + first);
        Capture("baked-picture-metrics.txt", string.Join('\n', metrics));
        first.AssertBaked();

        for (var i = 1; i <= 9; i++)
        {
            var old = live.Value;
            var changed = i is 3 or 6;
            if (changed) live.Set(h, 10 + (10 * i));
            // a changed reading reaches the picture when the 2 s display sample is due; the 100 ms steps in between reuse the sample
            await PicturesForAsync(h, changed ? (int)ScreenWorker.TelemetrySamplePeriod.TotalMilliseconds : NativeWorkerHarness.PeriodMs);
            await NativeWorkerHarness.SettleAsync(() => true, "the picture's overlay step");
            var match = await MatchAsync(h, AccessUnit(h, h.DataWrites - 1), h.Assignment, live.Value, ReferenceEquals(old, live.Value) ? null : old);
            metrics.Add($"step {i} {match}");
            Capture("baked-picture-metrics.txt", string.Join('\n', metrics));
            match.AssertBaked();
            if (match.Other is { } previous)
                previous.Should().BeGreaterThan(match.Bad + 40, "the new telemetry text is in the picture, not the previous one");
        }

        WorkerPngs(h.Writes).Should().BeEmpty();
        var facts = Facts(h);
        (facts.AccessUnits, facts.OverlayWrites).Should().Be((48L, 1L), "begin, seven single steps and two 2 s (20-picture) steps");
        facts.EncodingNote.Should().EndWith(BakedNote);
        facts.OverlayEncodingNote.Should().Be(ScreenWorker.BakedOverlayNote);
        var rows = h.Worker.Performance.SourceFrames!;
        rows.Select(r => r.SourceIndex).Distinct().Count().Should().BeLessThan(rows.Count, "the 400 ms loop repeats source frames");
        rows.Should().OnlyContain(r => !r.CacheHit, "a baked picture is never served from the encoded-picture cache");
    }

    [MediaFoundationFact]
    public async Task The_png_overlay_path_still_caches_repeated_source_frames_for_contrast()
    {
        using var h = await NativeWorkerHarness.StartAsync(NativeWorkerHarness.NewTrial("plain"));
        await NativeWorkerHarness.SettleAsync(() => h.DataWrites >= 1, "first access unit");
        for (var i = 0; i < 6; i++) await PictureAsync(h);
        h.Worker.Performance.SourceFrames!.Should().Contain(r => r.CacheHit);
        Facts(h).EncodingNote.Should().EndWith("live PNG overlay");
    }

    [MediaFoundationFact]
    public async Task Switching_into_and_out_of_baked_mode_clears_the_png_plane_once_and_restores_the_exact_png_once()
    {
        var live = new Live();
        using var h = await StartAsync(NativeWorkerHarness.NewTrial("plain"), live);
        await NativeWorkerHarness.SettleAsync(() => h.DataWrites >= 1, "first access unit");
        live.Set(h, 20);
        await PictureAsync(h);
        await NativeWorkerHarness.SettleAsync(() => true, "overlay");
        WorkerPngs(h.Writes).Should().HaveCount(2, "the first overlay and its telemetry update");
        var transparent = BeginPng(h.Writes);

        var mark = h.Writes.Length;
        var data = h.DataWrites;
        await SwitchTrialAsync(h, Baked("bake"));
        var entered = h.Writes.Skip(mark).ToArray();
        Commands(entered).Should().Equal(ScreenProtocol.PushPngCommand, TurzxVideoProtocol.DataCommand);
        entered[0][512..].Should().Equal(transparent, "one transparent clear, BEFORE the first baked access unit");
        (await MatchAsync(h, AccessUnit(h, data), h.Assignment, live.Value)).AssertBaked();
        Facts(h).OverlayEncodingNote.Should().Be(ScreenWorker.BakedOverlayNote);

        mark = h.Writes.Length;
        for (var i = 0; i < 4; i++)
        {
            live.Set(h, 30 + i);
            await PictureAsync(h);
        }

        await NativeWorkerHarness.SettleAsync(() => true, "overlay steps");
        Commands(h.Writes.Skip(mark)).Should().OnlyContain(c => c == TurzxVideoProtocol.DataCommand, "telemetry changes write no PNG while baked");

        mark = h.Writes.Length;
        await SwitchTrialAsync(h, NativeWorkerHarness.NewTrial("plain again"));
        var left = h.Writes.Skip(mark).ToArray();
        Commands(left).Should().Equal(TurzxVideoProtocol.DataCommand, ScreenProtocol.PushPngCommand);
        var now = h.Time.GetUtcNow();
        var exact = await Sta.RunAsync(() => ScreenWorker.PrepareOverlayPng(ScreenWorker.RenderVideoOverlay(new OverlayRenderer(), Devices.Turzx.Family,
            h.Assignment, live.Value, now)!, h.Assignment, new ScreensOptions().TurzxLandscapeRotation, false).Bytes);
        left[1][512..].Should().Equal(exact, "the exact inline PNG of the current overlay and telemetry");
        Facts(h).EncodingNote.Should().EndWith("live PNG overlay");
        Facts(h).OverlayEncodingNote.Should().Be("RGBA8 WIC PNG");
        Commands(h.Writes).Count(c => c == 41).Should().Be(1, "one native session throughout");

        mark = h.Writes.Length;
        live.Set(h, 55);
        await PictureAsync(h);
        await NativeWorkerHarness.SettleAsync(() => true, "overlay");
        Commands(h.Writes.Skip(mark)).Should().Equal(TurzxVideoProtocol.DataCommand, ScreenProtocol.PushPngCommand);
    }

    [MediaFoundationFact]
    public async Task A_prepared_overlay_session_switching_to_baked_clears_its_plane_once_and_stops_preparing()
    {
        var live = new Live();
        using var encoder = new OverlayEncoder();
        using var h = await StartAsync(Prepared("prepared"), live, c => c with { PrepareOverlayForTest = encoder.Encode });
        await NativeWorkerHarness.SettleAsync(() => h.DataWrites >= 1, "first access unit");
        await IdleAsync(h);
        await PictureAsync(h);
        await NativeWorkerHarness.SettleAsync(() => true, "prepared overlay");
        WorkerPngs(h.Writes).Should().HaveCount(1, "the prepared overlay");
        var calls = encoder.Calls;

        var mark = h.Writes.Length;
        await SwitchTrialAsync(h, Baked("bake"));
        var entered = h.Writes.Skip(mark).ToArray();
        Commands(entered).Should().Equal(ScreenProtocol.PushPngCommand, TurzxVideoProtocol.DataCommand);
        entered[0][512..].Should().Equal(BeginPng(h.Writes), "the single clear precedes the first baked access unit");
        live.Set(h, 66);
        await PictureAsync(h);
        await NativeWorkerHarness.SettleAsync(() => true, "picture");
        WorkerPngs(h.Writes).Should().HaveCount(2, "the prepared overlay and the single clear");
        encoder.Calls.Should().Be(calls, "nothing more is prepared while baked");
        Facts(h).PreparedOverlay.Should().BeNull("a baked trial reports no prepared-overlay facts");
    }

    [MediaFoundationFact]
    public async Task Assignment_changes_keep_the_overlay_in_the_picture_and_expiry_ends_with_the_normal_stop()
    {
        var live = new Live();
        using var h = await StartAsync(Baked("bake"), live);
        await NativeWorkerHarness.SettleAsync(() => h.DataWrites >= 1, "first access unit");

        var data = h.DataWrites;
        await h.PostAsync(h.Assignment with { Rotation = 180 });
        await NativeWorkerHarness.SettleAsync(() => true, "overlay");
        (await MatchAsync(h, AccessUnit(h, data), h.Assignment, live.Value)).AssertBaked();

        await h.PostAsync(h.Assignment with { Overlay = "none" });
        await NativeWorkerHarness.SettleAsync(() => true, "overlay");
        Facts(h).OverlayEncodingNote.Should().BeNull("nothing to bake: the plain no-overlay picture, as without the flag");
        Facts(h).EncodingNote.Should().EndWith("live PNG overlay", "the existing no-overlay wording is unchanged");

        data = h.DataWrites;
        await h.PostAsync(h.Assignment with { Overlay = "dashboard" });
        await NativeWorkerHarness.SettleAsync(() => true, "overlay");
        (await MatchAsync(h, AccessUnit(h, data), h.Assignment, live.Value)).AssertBaked();
        WorkerPngs(h.Writes).Should().BeEmpty("the plane stayed transparent from Begin");

        (await EndTrialAsync(h)).Should().Equal(123, 111, 114, ScreenProtocol.PushPngCommand);
        WorkerPngs(h.Writes).Should().BeEmpty();
        h.Worker.Performance.NativeVideo.Should().BeNull();
    }

    [MediaFoundationFact]
    public async Task Blackout_and_wake_in_a_baked_trial_send_only_the_normal_controls_and_a_fresh_transparent_begin()
    {
        var dark = 0;
        var directory = Path.Combine(Path.GetTempPath(), "urgb-screens-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var live = new Live();
            using var h = await StartAsync(Baked("bake"), live,
                c => c with { WholeCaseBlackout = () => Volatile.Read(ref dark) != 0, BacklightStateDirectory = directory });
            await NativeWorkerHarness.SettleAsync(() => h.DataWrites >= 1, "first access unit");
            await PictureAsync(h);
            var before = h.Writes.Length;
            Volatile.Write(ref dark, 1);
            await Wait.UntilAsync(() => h.Worker.Snapshot().State == ScreenStates.Sleeping, "sleeping");
            Commands(h.Writes.Skip(before)).Should().Equal(14, 123, 111, 114);

            before = h.Writes.Length;
            var data = h.DataWrites;
            Volatile.Write(ref dark, 0);
            h.Time.Advance(TimeSpan.FromMilliseconds(NativeWorkerHarness.PeriodMs));
            await NativeWorkerHarness.SettleAsync(() => h.DataWrites > data && h.Worker.Snapshot().State == ScreenStates.Running, "awake again");
            var woke = Commands(h.Writes.Skip(before));
            woke.Take(6).Should().Equal(111, 112, 41, ScreenProtocol.PushPngCommand, 15, TurzxVideoProtocol.DataCommand);
            woke.Count(c => c == ScreenProtocol.PushPngCommand).Should().Be(1, "only the new session's transparent Begin PNG");
            WorkerPngs(h.Writes).Should().BeEmpty();
            (await MatchAsync(h, AccessUnit(h, data), h.Assignment, live.Value)).AssertBaked();
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    /// <summary>
    /// Host cost scopes at the actual 1920x480 canvas (desktop numbers, recorded with BAKE_CAPTURE_DIR; never device facts): what baking
    /// adds to EVERY picture (blending the text-cached overlay layer into the canvas the compositor flattens anyway) versus what the PNG path pays per overlay change before its
    /// separate 102 write (the worker's exact OverlayCanvas + WIC PNG). Both paths render the layer itself once per text change.
    /// </summary>
    [MediaFoundationFact]
    public async Task Host_cost_of_baking_versus_preparing_the_png_layer_is_recorded()
    {
        var lines = await Sta.RunAsync(() =>
        {
            var family = Devices.Turzx.Family;
            var assignment = new ScreenAssignmentDto(Devices.Turzx.Id, Overlay: "dashboard");
            var renderer = new OverlayRenderer();
            var layer = ScreenWorker.RenderVideoOverlay(renderer, family, assignment, Nv12Rasters.Telemetry, Nv12Rasters.Telemetry.Timestamp)!;
            var canvas = Nv12Rasters.Scene(family.Width, family.Height);
            var rotation = new ScreensOptions().TurzxLandscapeRotation;
            var png = ScreenWorker.PrepareOverlayPng(layer, assignment, rotation, false).Bytes;
            png.Length.Should().BeGreaterThan(0);
            var telemetry = 0;
            return new[]
            {
                $"host {Environment.MachineName} {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}; overlay PNG {png.Length} B",
                // The compositor flattens every canvas anyway; baking adds the blend of the (text-cached) layer.
                Time("baked: BlendOver cached layer (every picture)", 60, () => PixelOps.BlendOver(canvas, layer, 0, 0)),
                Time("png path: OverlayCanvas + WIC PNG (per overlay change)", 30, () => ScreenWorker.PrepareOverlayPng(layer, assignment, rotation, false)),
                Time("either path: overlay layer render (per text change)", 30, () => renderer.Render(family.Width, family.Height, OverlayShape.Rectangle,
                    assignment.Overlay, System.Windows.Media.Color.FromRgb(255, 200, 40), Nv12Rasters.Telemetry with { CpuUsagePercent = ++telemetry % 100 },
                    Nv12Rasters.Telemetry.Timestamp, null)),
            };
        });
        Capture("baked-host-cost.txt", string.Join('\n', lines));
    }

    private static string Time(string label, int runs, Action work)
    {
        for (var i = 0; i < 5; i++) work();
        var ms = new double[runs];
        for (var i = 0; i < runs; i++)
        {
            var start = System.Diagnostics.Stopwatch.GetTimestamp();
            work();
            ms[i] = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }

        Array.Sort(ms);
        return string.Create(CultureInfo.InvariantCulture, $"{label}: runs {runs} median {ms[runs / 2]:F3} p95 {ms[(int)(runs * 0.95)]:F3} max {ms[^1]:F3} ms");
    }

    /// <summary>
    /// Steps <paramref name="ms"/> of frozen time one period at a time, one complete picture each (a single larger jump would also admit
    /// the newest due slot at once: two pictures).
    /// </summary>
    internal static async Task PicturesForAsync(NativeWorkerHarness h, int ms)
    {
        for (var t = 0; t < ms; t += NativeWorkerHarness.PeriodMs) await PictureAsync(h);
    }

    internal sealed class Live
    {
        public PcTelemetryDto Value = Telemetry(new DateTimeOffset(2026, 10, 22, 12, 0, 0, TimeSpan.Zero), 10);

        public void Set(NativeWorkerHarness h, double cpu) => Volatile.Write(ref Value, Telemetry(h.Time.GetUtcNow(), cpu));
    }

    internal static PcTelemetryDto Telemetry(DateTimeOffset at, double cpu) =>
        new(at, cpu, 8L << 30, 32L << 30, 50, null, 2L << 30, 8L << 30, "harness", null);

    internal static Task<NativeWorkerHarness> StartAsync(NativeVideoTrial trial, Live live, Func<ScreenWorkerContext, ScreenWorkerContext>? more = null) =>
        NativeWorkerHarness.StartAsync(trial, customize: c =>
        {
            c = c with { Telemetry = () => Volatile.Read(ref live.Value) };
            return more is null ? c : more(c);
        });

    /// <summary>The access unit of the picture whose data packets follow the first <paramref name="dataBefore"/> ones.</summary>
    internal static byte[] AccessUnit(NativeWorkerHarness h, int dataBefore) =>
        [.. h.Writes.Where(p => NativeWorkerHarness.Command(p) == TurzxVideoProtocol.DataCommand).Skip(dataBefore).SelectMany(p => p[512..])];

    /// <summary>
    /// Pixels (of 1920x480) whose largest channel error exceeds 40 between the worker's decoded access unit and a FRESH Q95 encode/decode
    /// of: the compositor's own canvas WITH the overlay (the exact legacy layout and live data; <see cref="Bad"/>), the same canvas WITHOUT
    /// it (what the PNG path encodes; <see cref="VideoOnly"/>), and optionally other telemetry. <see cref="Codec"/> is the inherent Q95
    /// 4:2:0 loss of that fresh round trip against the composed canvas itself (saturated text edges), and the MAE is against the canvas.
    /// (First run compared directly with the canvas and failed on that codec loss alone: retained in RESULT.en.md.)
    /// </summary>
    internal sealed record PictureMatch(int Bad, int VideoOnly, int? Other, int Codec, double Mae)
    {
        public void AssertBaked()
        {
            Bad.Should().BeLessThan(1920 * 480 / 1000, "the worker encoded exactly the composed canvas with its overlay");
            VideoOnly.Should().BeGreaterThan(Math.Max(2000, 20 * Bad), "the overlay is inside the picture");
        }

        public override string ToString() => string.Create(CultureInfo.InvariantCulture,
            $"bad>40 {Bad} video-only>40 {VideoOnly} other-telemetry>40 {Other?.ToString(CultureInfo.InvariantCulture) ?? "-"} codec-loss>40 {Codec} mae-vs-canvas {Mae:F3}");
    }

    internal static async Task<PictureMatch> MatchAsync(NativeWorkerHarness h, byte[] accessUnit, ScreenAssignmentDto assignment,
        PcTelemetryDto? telemetry, PcTelemetryDto? other = null)
    {
        var position = Facts(h).SourcePositionMs;
        var now = h.Time.GetUtcNow();
        var assets = h.Assets;
        return await Sta.RunAsync(() =>
        {
            var family = Devices.Turzx.Family;
            var rotation = new ScreensOptions().TurzxLandscapeRotation;
            PixelFrame Decoded(byte[] unit) => PixelOps.Rotate(MfH264IntraEncoder.Decode(unit, family.Height, family.Width, fullRange: true), 360 - rotation);
            PixelFrame RoundTrip(PixelFrame canvas)
            {
                using var encoder = new MfH264IntraEncoder(family.Height, family.Width, fullRange: true);
                return Decoded(encoder.Encode(PixelOps.Rotate(canvas, rotation)));
            }

            var decoded = Decoded(accessUnit);
            using var compositor = new ScreenCompositor(assets, 64L << 20);
            var expected = compositor.Compose(family, assignment, position, telemetry, now);
            expected.Warning.Should().BeNull();
            var fresh = RoundTrip(expected.Canvas);
            var videoOnly = RoundTrip(compositor.Compose(family, assignment with { Overlay = "none", OverlayLayout = null }, position, telemetry, now).Canvas);
            int? otherBad = other is null ? null : Count(decoded, RoundTrip(compositor.Compose(family, assignment, position, other, now).Canvas), out _);
            if (Environment.GetEnvironmentVariable("BAKE_CAPTURE_DIR") is { Length: > 0 } dir && Interlocked.Increment(ref _pictures) is var n && n <= 4)
            {
                var stem = Path.Combine(dir, $"picture-{n:D3}");
                Directory.CreateDirectory(dir);
                File.WriteAllBytes(stem + "-decoded.png", WpfCodec.EncodePng(decoded));
                File.WriteAllBytes(stem + "-expected.png", WpfCodec.EncodePng(expected.Canvas));
            }
            var codec = Count(fresh, expected.Canvas, out _);
            Count(decoded, expected.Canvas, out var mae);
            return new PictureMatch(Count(decoded, fresh, out _), Count(decoded, videoOnly, out _), otherBad, codec, mae);
        });
    }

    internal static int Count(PixelFrame actual, PixelFrame expected, out double mae)
    {
        actual.Width.Should().Be(expected.Width);
        actual.Height.Should().Be(expected.Height);
        var bad = 0;
        long sum = 0;
        for (var i = 0; i < actual.Pixels.Length; i += 4)
        {
            var worst = 0;
            for (var c = 0; c < 3; c++)
            {
                var d = Math.Abs(actual.Pixels[i + c] - expected.Pixels[i + c]);
                sum += d;
                worst = Math.Max(worst, d);
            }

            if (worst > 40) bad++;
        }

        mae = sum / (actual.Pixels.Length / 4 * 3.0);
        return bad;
    }

    internal static void Capture(string name, string text)
    {
        if (Environment.GetEnvironmentVariable("BAKE_CAPTURE_DIR") is { Length: > 0 } dir)
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, name), text);
        }
    }
}
