using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UnifiedRgb.Contracts;
using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Screens.Encoding;
using UnifiedRgb.Screens.Protocol;

namespace UnifiedRgb.Screens.Tests;

/// <summary>
/// Default-off compatibility, pinned against the pre-S1b build: the exact JSON of the default trial request/result and native facts,
/// and a frozen-clock TURZX native transcript with fixed wire parameters, source selections and counters. Encoded media bytes are
/// compared on the same platform: Windows' H.264/WIC encoders and Segoe UI rasterization are not a cross-OS byte contract.
/// Set S1B_CAPTURE_DIR to also write the complete media/header hashes for review.
/// </summary>
public sealed class NativeVideoDefaultOffTests
{
    private const string ExpectedJsonSha = "D1A8680AA8AD19167952AF6192CA82F4661BB40B5F9C3E7855FADC7646D7355F";

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
        text.Split('\n').Count(line => line.StartsWith("# ", StringComparison.Ordinal)).Should().Be(17);
    }

    /// <summary>
    /// A fixed frozen-clock scenario: begin, overlay telemetry changes, overlay disabled and re-enabled, a rotation change (a new
    /// assignment), then the trial ends and the normal image path returns. Every write is listed (stamps zeroed, but checked unique
    /// and increasing within the native session), followed by the selected index and counters after each step.
    /// </summary>
    internal static async Task<string> TranscriptAsync(Func<NativeVideoTrialDto, NativeVideoTrialDto>? flags,
        Func<UnifiedRgb.Screens.Runtime.ScreenWorkerContext, UnifiedRgb.Screens.Runtime.ScreenWorkerContext>? customize = null)
    {
        var trial = NativeWorkerHarness.NewTrial("wire", flags);
        using var h = await NativeWorkerHarness.StartAsync(trial, customize: customize);
        var text = new StringBuilder();
        var written = 0;
        async Task StepAsync(string name, int expectedPictures)
        {
            var p = await CompletedPictureAsync(() => h.Worker.Performance, expectedPictures);
            var writes = h.Writes;
            var stepWrites = writes.Skip(written).ToArray();
            foreach (var packet in stepWrites) AssertWirePacket(packet);
            if (!trial.Info.BakeTelemetry) AssertDefaultStep(expectedPictures, p, stepWrites);
            text.Append("# ").Append(name).Append('\n');
            foreach (var packet in stepWrites) text.Append(NativeWorkerHarness.Line(packet)).Append('\n');
            written = writes.Length;
            var v = p.NativeVideo;
            text.Append("facts ").Append(v is null ? "null" : $"{v.AccessUnits} {v.OverlayWrites} {v.DeviceBufferStatus} {v.SourcePositionMs} {v.SourceFrameIndex} {v.SourceClock} {v.LoopWraps} {v.EncodingNote} | {v.OverlayEncodingNote}")
                .Append(" frames ").Append(p.FramesWritten).Append(" dropped ").Append(p.Dropped).Append(" trial ").Append(p.TrialId).Append('\n');
            if (h.Trial is not null && p.SourceFrames is { } rows)
                text.Append("rows ").Append(string.Join(' ', rows.Select(r => $"{r.Sequence}:{r.Generation}:{r.Selection}:{r.SourceIndex}:{r.SourcePositionMs}:{r.CacheHit}"))).Append('\n');
        }

        await NativeWorkerHarness.SettleAsync(() => h.DataWrites >= 1, "first native access unit");
        await StepAsync("begin", 1);
        for (var i = 1; i <= 9; i++)
        {
            if (i is 3 or 6) h.SetTelemetry(10 + (10 * i));
            await h.StepAsync();
            await StepAsync("step " + i, i + 1);
        }

        await h.PostAsync(h.Assignment with { Overlay = "none" });
        await StepAsync("overlay none", 11);
        await h.StepAsync();
        await StepAsync("overlay none step", 12);
        await h.PostAsync(h.Assignment with { Overlay = "dashboard" });
        await StepAsync("overlay dashboard", 13);
        h.SetTelemetry(77);
        await h.StepAsync();
        await StepAsync("telemetry 77", 14);
        await h.PostAsync(h.Assignment with { Rotation = 180 });
        await StepAsync("rotation 180", 15);
        await h.StepAsync();
        await StepAsync("rotation step", 16);

        var native = h.Writes.Select(NativeWorkerHarness.Stamp).ToArray();
        native.Should().OnlyHaveUniqueItems();
        native.Zip(native.Skip(1)).Should().OnlyContain(s => unchecked((int)(s.Second - s.First)) > 0, "one native session's request tokens only increase");

        h.Trial = null;
        var before = h.Writes.Length;
        h.Time.Advance(TimeSpan.FromMilliseconds(NativeWorkerHarness.PeriodMs));
        await NativeWorkerHarness.SettleAsync(() => h.Writes is { } all && all.Length > before + 3
            && NativeWorkerHarness.Command(all[before + 3]) == ScreenProtocol.PushPngCommand, "stop controls, then the normal image path");
        await StepAsync("trial ended", 17);
        return text.ToString();
    }

    private static void AssertWirePacket(byte[] packet)
    {
        var header = ScreenProtocol.DecryptHeader(ScreenFamily.Turzx, packet.AsSpan(0, 512));
        var command = header[0];
        command.Should().BeOneOf((byte)111, (byte)112, (byte)41, (byte)15, (byte)121, (byte)102, (byte)123, (byte)114);
        var expected = new byte[500];
        expected[0] = command; expected[2] = 0x1A; expected[3] = 0x6D;
        header.AsSpan(4, 4).CopyTo(expected.AsSpan(4)); // session stamps are checked for uniqueness/order separately
        if (command is 121 or 102)
        {
            var size = packet.Length - 512;
            size.Should().BeGreaterThan(0);
            BinaryPrimitives.WriteInt32BigEndian(expected.AsSpan(8), size);
            if (command == 121)
            {
                size.Should().BeLessThanOrEqualTo(202752, "one complete fixture access unit fits the unchanged native chunk budget");
                var kinds = H264AnnexB.Types(packet.AsSpan(512));
                kinds.Should().Contain(7).And.Contain(8).And.Contain(5);
                kinds.Should().NotContain(t => t >= 1 && t <= 4, "every source picture remains independently decodable IDR");
            }
            else
            {
                ScreenProtocol.IsPng(packet.AsSpan(512)).Should().BeTrue();
                BinaryPrimitives.ReadInt32BigEndian(packet.AsSpan(528)).Should().Be(480, "native TURZX width");
                BinaryPrimitives.ReadInt32BigEndian(packet.AsSpan(532)).Should().Be(1920, "native TURZX height");
            }
        }
        else
        {
            packet.Should().HaveCount(512, "controls have no image payload");
            if (command == 15) expected[8] = 60; // host-selected pictures, unchanged consume-available cadence
        }
        header.Should().Equal(expected, "all wire fields except the independently checked stamp have a platform-independent contract");
    }

    private static void AssertDefaultStep(int picture, ScreenPerformanceDto performance, byte[][] writes)
    {
        byte[] commands = picture switch
        {
            1 => [111, 112, 41, 102, 15, 121, 102],
            4 or 7 or 11 or 13 or 14 or 15 => [121, 102],
            17 => [123, 111, 114, 102],
            _ => [121],
        };
        writes.Select(NativeWorkerHarness.Command).Should().Equal(commands, "picture {0}: controls, data and overlay order", picture);
        performance.FramesWritten.Should().Be(picture);
        performance.Samples.Should().Be(picture);
        performance.Dropped.Should().Be(0);
        if (picture == 17)
        {
            performance.NativeVideo.Should().BeNull();
            performance.TrialId.Should().BeNull();
            return;
        }

        int[] overlays = [2, 2, 2, 3, 3, 3, 4, 4, 4, 4, 5, 5, 6, 7, 8, 8];
        var facts = performance.NativeVideo!;
        var position = picture <= 10 ? (picture - 1) * 100 : (picture - 11) % 2 * 100;
        var sourceIndex = picture <= 10 ? (picture - 1) % 4 : (picture - 11) % 2;
        var generation = picture <= 10 ? 1 : 2 + (picture - 11) / 2;
        (facts.AccessUnits, facts.OverlayWrites, facts.DeviceBufferStatus, facts.SourcePositionMs, facts.SourceFrameIndex, facts.SourceClock, facts.LoopWraps)
            .Should().Be(((long)picture, (long)overlays[picture - 1], 0, (long)position, sourceIndex, "local-epoch", 0L));
        performance.TrialId.Should().Be("wire");
        var slices = H264AnnexB.Types(writes.Single(p => NativeWorkerHarness.Command(p) == 121).AsSpan(512)).Count(t => t == 5);
        facts.EncodingNote.Should().Be($"H.264 all-IDR Baseline Q95, {slices} slices; host/shared-clock-selected picture; live PNG overlay");
        facts.OverlayEncodingNote.Should().Be(picture is 11 or 12 ? null : "RGBA8 WIC PNG");
        performance.SourceFrames.Should().HaveCount(picture);
        var source = performance.SourceFrames![^1];
        (source.Sequence, source.Generation, source.Selection, source.SourceIndex, source.SourcePositionMs, source.CacheHit)
            .Should().Be(((long)picture, (long)generation, picture is 1 or 11 or 13 or 15 ? "start" : "advance", sourceIndex, (long)position, picture is >= 5 and <= 10));
    }

    private static async Task<ScreenPerformanceDto> CompletedPictureAsync(Func<ScreenPerformanceDto> snapshot, int expectedPictures)
    {
        ScreenPerformanceDto? completed = null;
        // A fake data packet is visible before overlay/facts and Wrote finish. Samples
        // is published under Add's lock after that work; both counters must reach this
        // frozen-clock picture before its packets, facts and source rows are captured.
        await Wait.UntilAsync(() =>
        {
            var p = snapshot();
            if (p.FramesWritten < expectedPictures || p.Samples < expectedPictures) return false;
            completed = p;
            return true;
        }, $"completed picture {expectedPictures}: frames and performance sample");
        return completed!;
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
