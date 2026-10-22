using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UnifiedRgb.Contracts;
using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Screens.Protocol;

namespace UnifiedRgb.Screens.Tests;

/// <summary>
/// Default-off compatibility, pinned against the pre-S1b build: the exact JSON of the default trial request/result and native facts,
/// and a deterministic frozen-clock TURZX native transcript (commands, stamp-free headers, payload bytes, selected source indices and
/// counters). Set S1B_CAPTURE_DIR to also write the captured text for review.
/// </summary>
public sealed class NativeVideoDefaultOffTests
{
    private const string ExpectedJsonSha = "D1A8680AA8AD19167952AF6192CA82F4661BB40B5F9C3E7855FADC7646D7355F";
    private const string ExpectedTranscriptSha = "4C8C54D3E0275152AE763FFC3AE099DF56AE10BF30DD1B37EF72178379313AE3";

    [Fact]
    public void Default_trial_and_native_fact_JSON_is_byte_identical_to_the_pre_S1b_contract()
    {
        var text = DefaultJson();
        Capture("default-json.txt", text);
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text))).Should().Be(ExpectedJsonSha, text);
    }

    internal static string DefaultJson()
    {
        var web = ContractsJson.Configure(new JsonSerializerOptions());
        var request = new NativeVideoTrialRequestDto(Devices.Turzx.Id);
        var info = new NativeVideoTrialDto("trial", Devices.Turzx.Id, 10000, 30, null) { Quality = 95 };
        var facts = new NativeVideoPerformanceDto(3, 2, 1, 1234, 7, "device-clock", 1) { EncodingNote = "e", OverlayEncodingNote = "o" };
        var timing = new ScreenTimingDto(1, 2, 3, 4);
        var perf = new ScreenPerformanceDto(Devices.Turzx.Id, "Running", 5, 0, 30, 30, "trial", 3, 2, 0,
            new Dictionary<string, ScreenTimingDto> { ["work"] = timing }, 10, 20, 3, 1, 1, 2, 0, facts);
        var lines = new[]
        {
            JsonSerializer.Serialize(request, ContractsJsonContext.Default.NativeVideoTrialRequestDto),
            JsonSerializer.Serialize(info, ContractsJsonContext.Default.NativeVideoTrialDto),
            JsonSerializer.Serialize(facts, ContractsJsonContext.Default.NativeVideoPerformanceDto),
            JsonSerializer.Serialize(perf, ContractsJsonContext.Default.ScreenPerformanceDto),
            JsonSerializer.Serialize(request, web),
            JsonSerializer.Serialize(info, web),
            JsonSerializer.Serialize(request with { SingleSlice = true, Quality = 100, SimpleOverlayPng = true }, web),
            JsonSerializer.Serialize(info with { SingleSlice = true, Quality = 100, SimpleOverlayPng = true }, web),
        };
        return string.Join("\n---\n", lines).ReplaceLineEndings("\n");
    }

    [MediaFoundationFact]
    public async Task Default_off_native_wire_selection_and_counters_match_the_pre_S1b_transcript()
    {
        var text = await TranscriptAsync(null);
        Capture("default-wire-transcript.txt", text);
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text))).Should().Be(ExpectedTranscriptSha);
    }

    /// <summary>
    /// A fixed frozen-clock scenario: begin, overlay telemetry changes, overlay disabled and re-enabled, a rotation change (a new
    /// assignment), then the trial ends and the normal image path returns. Every write is listed (stamps zeroed, but checked unique
    /// and increasing within the native session), followed by the selected index and counters after each step.
    /// </summary>
    internal static async Task<string> TranscriptAsync(Func<NativeVideoTrialDto, NativeVideoTrialDto>? flags,
        Func<UnifiedRgb.Screens.Runtime.ScreenWorkerContext, UnifiedRgb.Screens.Runtime.ScreenWorkerContext>? customize = null)
    {
        using var h = await NativeWorkerHarness.StartAsync(NativeWorkerHarness.NewTrial("wire", flags), customize: customize);
        var text = new StringBuilder();
        var written = 0;
        void Step(string name)
        {
            var writes = h.Writes;
            text.Append("# ").Append(name).Append('\n');
            foreach (var packet in writes.Skip(written)) text.Append(NativeWorkerHarness.Line(packet)).Append('\n');
            written = writes.Length;
            var p = h.Worker.Performance;
            var v = p.NativeVideo;
            text.Append("facts ").Append(v is null ? "null" : $"{v.AccessUnits} {v.OverlayWrites} {v.DeviceBufferStatus} {v.SourcePositionMs} {v.SourceFrameIndex} {v.SourceClock} {v.LoopWraps} {v.EncodingNote} | {v.OverlayEncodingNote}")
                .Append(" frames ").Append(p.FramesWritten).Append(" dropped ").Append(p.Dropped).Append(" trial ").Append(p.TrialId).Append('\n');
            if (h.Trial is not null && p.SourceFrames is { } rows)
                text.Append("rows ").Append(string.Join(' ', rows.Select(r => $"{r.Sequence}:{r.Generation}:{r.Selection}:{r.SourceIndex}:{r.SourcePositionMs}:{r.CacheHit}"))).Append('\n');
        }

        await NativeWorkerHarness.SettleAsync(() => h.DataWrites >= 1, "first native access unit");
        Step("begin");
        for (var i = 1; i <= 9; i++)
        {
            if (i is 3 or 6) h.SetTelemetry(10 + (10 * i));
            await h.StepAsync();
            Step("step " + i);
        }

        await h.PostAsync(h.Assignment with { Overlay = "none" });
        Step("overlay none");
        await h.StepAsync();
        Step("overlay none step");
        await h.PostAsync(h.Assignment with { Overlay = "dashboard" });
        Step("overlay dashboard");
        h.SetTelemetry(77);
        await h.StepAsync();
        Step("telemetry 77");
        await h.PostAsync(h.Assignment with { Rotation = 180 });
        Step("rotation 180");
        await h.StepAsync();
        Step("rotation step");

        var native = h.Writes.Select(NativeWorkerHarness.Stamp).ToArray();
        native.Should().OnlyHaveUniqueItems();
        native.Zip(native.Skip(1)).Should().OnlyContain(s => unchecked((int)(s.Second - s.First)) > 0, "one native session's request tokens only increase");

        h.Trial = null;
        var before = h.Writes.Length;
        h.Time.Advance(TimeSpan.FromMilliseconds(NativeWorkerHarness.PeriodMs));
        await NativeWorkerHarness.SettleAsync(() => h.Writes is { } all && all.Length > before + 3
            && NativeWorkerHarness.Command(all[before + 3]) == ScreenProtocol.PushPngCommand, "stop controls, then the normal image path");
        Step("trial ended");
        return text.ToString();
    }

    private static void Capture(string name, string text)
    {
        if (Environment.GetEnvironmentVariable("S1B_CAPTURE_DIR") is { Length: > 0 } dir)
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, name), text);
        }
    }
}
