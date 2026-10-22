using System.Text.Json;
using UnifiedRgb.Contracts;
using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Screens.Protocol;
using UnifiedRgb.Screens.Runtime;

namespace UnifiedRgb.Screens.Tests;

/// <summary>
/// The two optional lab flags on the existing native trial request/result: default false and omitted from JSON, exact primary
/// constructors, TURZX-only validation before any I/O, and propagation to the stored trial. The lab fact records are optional
/// properties that old JSON never carries.
/// </summary>
public sealed class PreparedOverlayContractTests
{
    private static readonly JsonSerializerOptions Web = ContractsJson.Configure(new JsonSerializerOptions());

    [Fact]
    public void Default_false_flags_are_omitted_old_json_reads_false_and_true_flags_round_trip()
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
            json.Should().NotContainAny("reparedOverlay", "easureQueueAfterOverlay");
        }

        var old = JsonSerializer.Deserialize<NativeVideoTrialRequestDto>("{\"screenId\":\"s\"}", Web)!;
        (old.PreparedOverlay, old.MeasureQueueAfterOverlay).Should().Be((false, false));
        var oldInfo = JsonSerializer.Deserialize("{\"trialId\":\"t\",\"screenId\":\"s\",\"durationMs\":1000,\"fps\":30}", ContractsJsonContext.Default.NativeVideoTrialDto)!;
        (oldInfo.PreparedOverlay, oldInfo.MeasureQueueAfterOverlay).Should().Be((false, false));

        var lab = request with { PreparedOverlay = true, MeasureQueueAfterOverlay = true };
        var text = JsonSerializer.Serialize(lab, Web);
        text.Should().Contain("\"preparedOverlay\":true").And.Contain("\"measureQueueAfterOverlay\":true");
        JsonSerializer.Deserialize<NativeVideoTrialRequestDto>(text, Web).Should().Be(lab);
        var labInfo = info with { PreparedOverlay = true, MeasureQueueAfterOverlay = true };
        JsonSerializer.Deserialize(JsonSerializer.Serialize(labInfo, ContractsJsonContext.Default.NativeVideoTrialDto),
            ContractsJsonContext.Default.NativeVideoTrialDto).Should().Be(labInfo);
        typeof(NativeVideoTrialRequestDto).GetConstructors().Should().ContainSingle(c => c.GetParameters().Length == 4, "the primary constructor is unchanged");
        typeof(NativeVideoTrialDto).GetConstructors().Should().ContainSingle(c => c.GetParameters().Length == 5);
    }

    [Fact]
    public void Lab_fact_records_are_optional_and_round_trip_through_the_generated_context()
    {
        var facts = new NativeVideoPerformanceDto(3, 2, 1, 1234, 7, "device-clock", 1);
        var plain = JsonSerializer.Serialize(facts, ContractsJsonContext.Default.NativeVideoPerformanceDto);
        plain.Should().NotContainAny("preparedOverlay", "lastOverlayQueue");
        var full = facts with
        {
            PreparedOverlay = new("t", "inline-fallback", "encode failed: x", 4, 5, 6, 5, 3, 1, 0, true, 12.5, "Running", 1, 6, 5, 1, 2, 3, 0, 0,
                1, 3_686_400, 7.5, 9.25, 4096, 8192),
            LastOverlayQueue = new(2, 9, 1, 2, false, true, 1234, 0.5, 0.25, 0.75, 300),
        };
        var text = JsonSerializer.Serialize(full, ContractsJsonContext.Default.NativeVideoPerformanceDto);
        var back = JsonSerializer.Deserialize(text, ContractsJsonContext.Default.NativeVideoPerformanceDto)!;
        back.Should().Be(full);
        var prepared = full.PreparedOverlay with { Mode = "prepared", FallbackReason = null };
        JsonSerializer.Serialize(prepared, Web).Should().NotContain("fallbackReason");
        using var json = JsonDocument.Parse(text); // the generated context writes indented JSON
        var queue = json.RootElement.GetProperty("lastOverlayQueue");
        (queue.GetProperty("reportedBlocksBefore").GetInt32(), queue.GetProperty("reportedBlocksAfter").GetInt32(), queue.GetProperty("hostTimestampMs").GetDouble())
            .Should().Be((1, 2, 300.0));
        json.RootElement.GetProperty("preparedOverlay").GetProperty("fallbackReason").GetString().Should().Be("encode failed: x");
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Lab_flags_are_rejected_on_H2_before_any_IO(bool prepared, bool measure)
    {
        await using var rig = new Rig(Rig.AllOutput, Devices.H2);
        await FluentActions.Awaiting(() => rig.Manager.BeginNativeVideoTrialAsync(new(Devices.H2.Id) { PreparedOverlay = prepared, MeasureQueueAfterOverlay = measure }))
            .Should().ThrowAsync<ArgumentException>().WithMessage("*TURZX-only*");
        rig.Opener.Opens.Should().Be(0);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Lab_flags_are_rejected_on_TL_before_any_IO(bool prepared, bool measure)
    {
        await using var rig = new Rig(Rig.AllOutput, Devices.Tl1);
        await FluentActions.Awaiting(() => rig.Manager.BeginNativeVideoTrialAsync(new(Devices.Tl1.Id) { PreparedOverlay = prepared, MeasureQueueAfterOverlay = measure }))
            .Should().ThrowAsync<ArgumentException>();
        rig.Opener.Opens.Should().Be(0);
    }

    /// <summary>A RUNNING H2: the rejected lab request changes nothing (no trial, no native facts, no native command).</summary>
    [Fact]
    public async Task A_running_H2_screen_is_untouched_by_a_rejected_lab_request()
    {
        await using var rig = new Rig(Rig.AllOutput, Devices.H2);
        await rig.Manager.SetScreenSceneAsync(Images.Scene(new ScreenAssignmentDto(Devices.H2.Id, BackgroundColor: "#203040")));
        rig.Manager.Start();
        await Wait.UntilAsync(async () => (await rig.ScreenAsync(Devices.H2.Id)).FramesWritten > 0, "H2 running");
        await FluentActions.Awaiting(() => rig.Manager.BeginNativeVideoTrialAsync(new(Devices.H2.Id) { PreparedOverlay = true }))
            .Should().ThrowAsync<ArgumentException>().WithMessage("*TURZX-only*");
        await Task.Delay(200);
        var perf = (await rig.Manager.GetScreenPerformanceAsync()).Single();
        (perf.TrialId, perf.NativeVideo, perf.SourceFrames).Should().Be((null, null, null));
    }

    /// <summary>The returned and stored trial carry the flags; the running screen reports the lab facts only during that trial.</summary>
    [MediaFoundationFact]
    public async Task Flags_propagate_to_the_stored_trial_and_its_facts_without_persisting_anything()
    {
        await using var rig = new Rig(Rig.AllOutput, Devices.Turzx);
        rig.Opener.Configure = c => c.Reply = packet =>
        {
            var header = ScreenProtocol.DecryptHeader(ScreenFamily.Turzx, packet.AsSpan(0, 512));
            var reply = new byte[512]; reply[0] = header[0]; reply[1] = 200; header.AsSpan(4, 4).CopyTo(reply.AsSpan(2)); return reply;
        };
        var asset = await rig.Manager.UploadScreenAssetAsync(new("lab flags", Convert.ToBase64String(Images.Gif(2, 2,
            GifFrame.Fill(0, 0, 2, 2, 1, delayCs: 10), GifFrame.Fill(0, 0, 2, 2, 2, delayCs: 10)))));
        var scene = Images.Scene(new ScreenAssignmentDto(Devices.Turzx.Id, asset.Id, Overlay: "compact", Fps: 15));
        await rig.Manager.SetScreenSceneAsync(scene);
        rig.Manager.Start();
        await Wait.UntilAsync(async () => (await rig.ScreenAsync(Devices.Turzx.Id)).State == ScreenStates.Running, "initial image path");
        var path = Path.Combine(rig.DataDirectory, "screens", "scene.json");
        var original = File.ReadAllBytes(path);
        var trial = await rig.Manager.BeginNativeVideoTrialAsync(new(Devices.Turzx.Id, 60000, 30) { PreparedOverlay = true, MeasureQueueAfterOverlay = true });
        (trial.PreparedOverlay, trial.MeasureQueueAfterOverlay).Should().Be((true, true));
        await Wait.UntilAsync(async () => (await rig.Manager.GetScreenPerformanceAsync()).Single().NativeVideo is
            { PreparedOverlay: { PreparedSends: >= 1 } p, LastOverlayQueue: not null } && p.TrialId == trial.TrialId, "a prepared, measured overlay");
        var facts = (await rig.Manager.GetScreenPerformanceAsync()).Single().NativeVideo!;
        facts.PreparedOverlay!.Mode.Should().Be("prepared");
        facts.PreparedOverlay.ThreadsCreated.Should().Be(1);
        facts.LastOverlayQueue!.Prepared.Should().BeTrue();
        rig.Opener.Last.Writes.Count(p => ScreenProtocol.DecryptHeader(ScreenFamily.Turzx, p.AsSpan(0, 512))[0] == 122).Should().BeGreaterThanOrEqualTo(1);
        await rig.Manager.EndNativeVideoTrialAsync(trial.TrialId);
        await Wait.UntilAsync(async () => (await rig.Manager.GetScreenPerformanceAsync()).Single().NativeVideo is null, "ordinary image restored");
        File.ReadAllBytes(path).Should().Equal(original);
        (await rig.Manager.GetScreenSceneAsync()).Should().BeEquivalentTo(scene);
    }
}
