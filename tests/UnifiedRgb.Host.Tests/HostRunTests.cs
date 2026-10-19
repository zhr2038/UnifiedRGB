using System.Globalization;
using System.Text.Json;
using UnifiedRgb.Cli;
using UnifiedRgb.Contracts;
using UnifiedRgb.Contracts.Dto;
using Xunit.Abstractions;

namespace UnifiedRgb.Host.Tests;

[Collection("Host")]
public class HostRunTests(ITestOutputHelper output)
{
    private static readonly JsonSerializerOptions Json = ContractsJson.Configure(new JsonSerializerOptions());

    /// <summary>M2 gate: 60 s against Virtual; status.json per-device fps within 5 % of MaxFps, 0 dropped, jitter stats present.</summary>
    [Fact]
    public async Task Host_runs_60s_against_virtual_and_status_json_reports_healthy_devices()
    {
        var seconds = int.TryParse(Environment.GetEnvironmentVariable("URGB_SOAK_SECONDS"), CultureInfo.InvariantCulture, out var s) ? s : 60;
        await using var host = await TestHost.StartAsync();
        await Task.Delay(TimeSpan.FromSeconds(seconds));

        // One shared read (ReadWrite|Delete + transient retry): the writer replaces the file every second, and a plain
        // File.ReadAllText (FileShare.Read) collides with that rename. The file lives in this host's own temp data
        // directory, so a live service Host writing %ProgramData%\UnifiedRgb\status.json cannot contend with it.
        var json = await Services.StatusWriter.ReadSharedAsync(host.StatusPath);
        output.WriteLine(json);
        var status = JsonSerializer.Deserialize<StatusDto>(json, Json)!;

        status.Devices.Should().HaveCount(9);
        status.UptimeSeconds.Should().BeGreaterThan(seconds - 2);
        status.Clock.TargetFps.Should().Be(60);
        status.Clock.JitterP50Ms.Should().BeGreaterThan(0);
        status.Clock.JitterP99Ms.Should().BeGreaterThan(0).And.BeLessThan(2.0);
        status.Clock.Ticks.Should().BeGreaterThan((long)(60 * (seconds - 2)));
        foreach (var d in status.Devices)
        {
            d.State.Should().Be(ControllerStates.Active, d.Id);
            d.Fps.Should().BeInRange(d.MaxFps * 0.95, d.MaxFps * 1.05, d.Id);
            d.Dropped.Should().Be(0, d.Id);
            d.LastError.Should().BeNull(d.Id);
            d.Written.Should().BeGreaterThan((long)(d.MaxFps * (seconds - 2) * 0.95), d.Id);
        }
    }

    [Fact]
    public async Task Cli_lists_controllers_and_sets_scene_over_the_pipe()
    {
        await using var host = await TestHost.StartAsync();
        var pipe = host.Options.PipeName;

        var (code, stdout, stderr) = await CliAsync("--pipe", pipe, "list");
        code.Should().Be(0, stderr);
        stdout.Should().Contain("virtual:fan1").And.Contain("virtual:mb").And.Contain("Active");

        (code, stdout, stderr) = await CliAsync("--pipe", pipe, "scene", "set", "Meteor", "--colors", "#ff0000,#00FF00", "--speed", "0.8");
        code.Should().Be(0, stderr);
        var scene = host.Engine.Loop.Scene;
        scene.Assignments.Should().ContainSingle();
        scene.Assignments[0].EffectId.Should().Be("Meteor");
        scene.Assignments[0].Targets.Should().HaveCount(12); // 4 fans + strip + gpu + 2 dram + mb(4 zones)
        scene.Assignments[0].Params.Speed.Should().Be(0.8f);

        (code, _, stderr) = await CliAsync("--pipe", pipe, "scene", "set", "StaticColor", "--targets", "virtual:gpu:0", "--colors", "#0000ff");
        code.Should().Be(0, stderr);
        await Task.Delay(200);
        var gpu = host.Engine.Adapters.Controllers.Single(c => c.Descriptor.Id.Value == "virtual:gpu");
        ((Devices.Virtual.VirtualAdapter)gpu.Adapter).Snapshot(gpu.Descriptor.Id, new Devices.ZoneId(0))[0]
            .Should().Be(new Devices.Rgb(0, 0, 255));

        (code, stdout, _) = await CliAsync("--pipe", pipe, "status", "--json");
        code.Should().Be(0);
        JsonSerializer.Deserialize<StatusDto>(stdout, Json)!.Devices.Should().HaveCount(9);
    }

    [Fact]
    public async Task Invalid_scene_is_rejected_over_rpc_and_previous_scene_kept()
    {
        await using var host = await TestHost.StartAsync();
        var before = host.Engine.Loop.Scene;

        var (code, _, stderr) = await CliAsync("--pipe", host.Options.PipeName, "scene", "set", "NoSuchEffect");
        code.Should().Be(1);
        stderr.Should().Contain("NoSuchEffect");

        (code, _, _) = await CliAsync("--pipe", host.Options.PipeName, "scene", "set", "Rainbow", "--targets", "virtual:gpu:7");
        code.Should().Be(1);

        (code, _, _) = await CliAsync("--pipe", host.Options.PipeName, "scene", "set", "Rainbow", "--colors", "#12");
        code.Should().Be(1);

        host.Engine.Loop.Scene.Should().BeSameAs(before);
    }

    [Fact]
    public async Task Cli_reports_missing_host_and_bad_usage()
    {
        var (code, _, stderr) = await CliAsync("--pipe", "UnifiedRgb-nobody-" + Guid.NewGuid().ToString("N"), "list");
        code.Should().Be(1);
        stderr.Should().Contain("Cannot connect");

        (code, _, _) = await CliAsync();
        code.Should().Be(2);
    }

    [Fact]
    public async Task Second_host_on_the_same_pipe_name_does_not_share_the_pipe()
    {
        await using var host = await TestHost.StartAsync();
        using var server = new Ipc.PipeServer(host.Options, host.Get<IRgbService>(), Microsoft.Extensions.Logging.Abstractions.NullLogger<Ipc.PipeServer>.Instance);
        using var cts = new CancellationTokenSource();
        var start = () => server.StartAsync(cts.Token);
        await start.Should().ThrowAsync<Exception>().Where(ex => ex is UnauthorizedAccessException || ex is IOException);
        var act = () => server.Listening.WaitAsync(TimeSpan.FromSeconds(5));
        await act.Should().ThrowAsync<Exception>();
        await cts.CancelAsync();
        await server.StopAsync(CancellationToken.None);
    }

    private static async Task<(int Code, string Stdout, string Stderr)> CliAsync(params string[] args)
    {
        using var o = new StringWriter(CultureInfo.InvariantCulture);
        using var e = new StringWriter(CultureInfo.InvariantCulture);
        var code = await CliApp.RunAsync(args, o, e, trust: PipeTrust.ElevatedOwnerOrCurrentUser);
        return (code, o.ToString(), e.ToString());
    }
}
