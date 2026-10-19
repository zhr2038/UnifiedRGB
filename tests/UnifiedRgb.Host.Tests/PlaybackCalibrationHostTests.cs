using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using StreamJsonRpc;
using UnifiedRgb.Contracts;
using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Core.Engine;
using UnifiedRgb.Devices.EneDram;
using UnifiedRgb.Host.Ipc;

namespace UnifiedRgb.Host.Tests;

/// <summary>
/// NATIVE-PLAYBACK-CALIBRATION C5 and NATIVE-DYNAMIC-SYNC-FIX §A in the Host: the source file states the nominal 1 / 0 and
/// sleep mode 2, older OUTPUT files bind those defaults, the DTO defaults to 1 / 0 on the wire, and the runtime-only
/// Get/SetPlaybackCalibration RPCs replace the snapshot atomically without persisting it.
/// </summary>
[Collection("Host")]
public sealed class PlaybackCalibrationHostTests
{
    private static readonly JsonSerializerOptions Json = ContractsJson.Configure(new JsonSerializerOptions());

    [Fact]
    public void Source_appsettings_state_the_nominal_calibration_and_sleep_mode_two()
    {
        var path = Path.Combine(RepoRoot(), "src", "UnifiedRgb.Host", "appsettings.json");
        var o = Bind(File.ReadAllText(path));
        (o.LianLi.StreamedPlaybackRate, o.LianLi.PhaseTrimMs).Should().Be((1.0, 0.0), "the source default is 1 / 0, never a candidate");
        o.EneDram.SleepMode.Should().Be(2);
        var text = File.ReadAllText(path);
        text.Should().Contain("\"StreamedPlaybackRate\": 1,").And.Contain("\"PhaseTrimMs\": 0,").And.Contain("\"SleepMode\": 2");
        new PlaybackCalibration(o.LianLi.StreamedPlaybackRate, o.LianLi.PhaseTrimMs).Should().Be(PlaybackCalibration.Default);
    }

    [Fact]
    public void Settings_written_before_these_keys_bind_rate_one_and_sleep_mode_two()
    {
        // an OUTPUT file of the deployed baseline: an offset, no rate; an EneDram section without SleepMode
        var o = Bind("""
            { "UnifiedRgb": {
                "LianLi": { "WireFormatVerified": true, "PhaseTrimMs": -500 },
                "EneDram": { "FrameBudgetMs": 100 } } }
            """);
        (o.LianLi.StreamedPlaybackRate, o.LianLi.PhaseTrimMs).Should().Be((1.0, -500.0));
        o.EneDram.SleepMode.Should().Be(2, "the value every earlier build sent");
        EneDramDefaults.SleepMode.Should().Be(2);

        var explicitValues = Bind("""
            { "UnifiedRgb": { "LianLi": { "StreamedPlaybackRate": 2, "PhaseTrimMs": 0 }, "EneDram": { "SleepMode": 0 } } }
            """);
        (explicitValues.LianLi.StreamedPlaybackRate, explicitValues.LianLi.PhaseTrimMs, explicitValues.EneDram.SleepMode).Should().Be((2.0, 0.0, 0));
    }

    [Fact]
    public void The_dto_defaults_to_one_and_zero_on_the_wire_and_the_status_snapshot_is_optional()
    {
        JsonSerializer.Deserialize<PlaybackCalibrationDto>("{}", Json).Should().Be(new PlaybackCalibrationDto(1, 0));
        new PlaybackCalibrationDto().Should().Be(new PlaybackCalibrationDto(1, 0));
        var wire = JsonSerializer.Serialize(new PlaybackCalibrationDto(2, -500), Json);
        wire.Should().Be("""{"streamedPlaybackRate":2,"phaseTrimMs":-500}""");
        JsonSerializer.Deserialize<PlaybackCalibrationDto>(wire, Json).Should().Be(new PlaybackCalibrationDto(2, -500));

        var status = new StatusDto(DateTimeOffset.UnixEpoch, "t", 1, 0, new ClockStatusDto(60, 60, 0, 0, 0, 0, 0), [],
            PlaybackCalibration: new PlaybackCalibrationDto(0.5, 120));
        var json = JsonSerializer.Serialize(status, Json);
        JsonSerializer.Deserialize<StatusDto>(json, Json)!.PlaybackCalibration.Should().Be(new PlaybackCalibrationDto(0.5, 120));
        var older = JsonNode.Parse(json)!.AsObject();
        older.Remove("playbackCalibration").Should().BeTrue();
        JsonSerializer.Deserialize<StatusDto>(older.ToJsonString(), Json)!.PlaybackCalibration.Should().BeNull("a status from an older Host has none");
    }

    [Fact]
    public async Task The_rpcs_replace_the_snapshot_at_runtime_only_and_refuse_invalid_values_whole()
    {
        await using var host = await TestHost.StartAsync();
        var (rpc, svc, pipe) = await ConnectAsync(host);
        using (rpc)
        await using (pipe)
        {
            (await svc.GetPlaybackCalibrationAsync()).Should().Be(new PlaybackCalibrationDto(1, 0));
            (await svc.GetStatusAsync()).PlaybackCalibration.Should().Be(new PlaybackCalibrationDto(1, 0));

            (await svc.SetPlaybackCalibrationAsync(new PlaybackCalibrationDto(2, -500))).Should().Be(new PlaybackCalibrationDto(2, -500));
            (await svc.GetPlaybackCalibrationAsync()).Should().Be(new PlaybackCalibrationDto(2, -500));
            (await svc.GetStatusAsync()).PlaybackCalibration.Should().Be(new PlaybackCalibrationDto(2, -500));
            host.Engine.PlaybackCalibration.Should().Be(new PlaybackCalibration(2, -500));
            host.Engine.Loop.PlaybackCalibration.Should().Be(new PlaybackCalibration(2, -500));

            foreach (var invalid in new[] { new PlaybackCalibrationDto(5, 0), new PlaybackCalibrationDto(0.2, 0), new PlaybackCalibrationDto(2, 2500), new PlaybackCalibrationDto(3, -2001) })
            {
                var act = () => svc.SetPlaybackCalibrationAsync(invalid);
                await act.Should().ThrowAsync<RemoteInvocationException>($"{invalid} is outside the validated range");
                (await svc.GetPlaybackCalibrationAsync()).Should().Be(new PlaybackCalibrationDto(2, -500), "neither value of a refused pair is applied");
            }

            var none = () => svc.SetPlaybackCalibrationAsync(null!);
            await none.Should().ThrowAsync<RemoteInvocationException>();
            (await svc.GetPlaybackCalibrationAsync()).Should().Be(new PlaybackCalibrationDto(2, -500));

            (host.Options.LianLi.StreamedPlaybackRate, host.Options.LianLi.PhaseTrimMs).Should().Be((1.0, 0.0), "the configured value is not rewritten");
            var dir = host.Options.ResolvedDataDirectory; // absent when the Host has written nothing yet
            var persisted = (Directory.Exists(dir) ? Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories) : [])
                .Where(f => !string.Equals(Path.GetFileName(f), "status.json", StringComparison.OrdinalIgnoreCase))
                .Where(f => File.ReadAllText(f).Contains("treamedPlaybackRate", StringComparison.Ordinal));
            persisted.Should().BeEmpty("the snapshot is runtime-only: no profile, scene or settings file carries it");
        }
    }

    [Fact]
    public async Task The_engine_starts_with_the_configured_calibration()
    {
        await using var host = await TestHost.StartAsync(seed: o =>
        {
            o.LianLi.StreamedPlaybackRate = 2;
            o.LianLi.PhaseTrimMs = -250;
        });
        host.Engine.PlaybackCalibration.Should().Be(new PlaybackCalibration(2, -250));
        var (rpc, svc, pipe) = await ConnectAsync(host);
        using (rpc)
        await using (pipe)
        {
            (await svc.GetPlaybackCalibrationAsync()).Should().Be(new PlaybackCalibrationDto(2, -250));
        }
    }

    [Theory]
    [InlineData(8.0, 0.0)]
    [InlineData(double.NaN, 0.0)]
    [InlineData(1.0, -2500.0)]
    [InlineData(2.0, double.PositiveInfinity)]
    public async Task A_configured_calibration_the_setter_would_refuse_leaves_the_nominal_one_in_force(double rate, double trimMs)
    {
        await using var host = await TestHost.StartAsync(seed: o =>
        {
            o.LianLi.StreamedPlaybackRate = rate;
            o.LianLi.PhaseTrimMs = trimMs;
        });
        host.Engine.PlaybackCalibration.Should().Be(PlaybackCalibration.Default, "an invalid configured pair is rejected whole, with an error log");
    }

    private static UnifiedRgbOptions Bind(string json)
    {
        var o = new UnifiedRgbOptions();
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
        new ConfigurationBuilder().AddJsonStream(stream).Build().GetSection(UnifiedRgbOptions.Section).Bind(o);
        return o;
    }

    private static async Task<(JsonRpc Rpc, IRgbService Svc, NamedPipeClientStream Pipe)> ConnectAsync(TestHost host)
    {
        var pipe = new NamedPipeClientStream(".", host.Options.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(5000);
        var rpc = new JsonRpc(new HeaderDelimitedMessageHandler(pipe, pipe, PipeServer.CreateFormatter()));
        var svc = rpc.Attach<IRgbService>();
        rpc.StartListening();
        return (rpc, svc, pipe);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "src", "UnifiedRgb.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new DirectoryNotFoundException("repository root (src/UnifiedRgb.sln) not found");
    }
}
