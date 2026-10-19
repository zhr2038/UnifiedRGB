using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using StreamJsonRpc;
using UnifiedRgb.App.Ipc;
using UnifiedRgb.App.Mvvm;
using UnifiedRgb.App.ViewModels;
using UnifiedRgb.Contracts;
using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Core.Engine;
using UnifiedRgb.Host.Ipc;
using UnifiedRgb.Host.Services;

namespace UnifiedRgb.Host.Tests;

/// <summary>
/// STACK-RETURN (design/OPUS-STACKING-WAVE-RETURN-20261018.md): the optional loop return through the DTO, the mapper, the
/// profile store, the scene RPCs and the GUI summary. Absent = exact historical DTO/JSON; malformed or unsupported options
/// never reach the running scene or disk; Root's offline profile12 (read only) loads, maps, saves and applies.
/// </summary>
public sealed class StackingReturnHostTests
{
    private static readonly LoopReturnDto Accepted = new(4000, 600, 24, 0.25);

    private static ZoneRefDto Z(string id) => new(id, 0);

    /// <summary>gpu | fan1 ‖ dram1 | fan2 reversed: every stage a multiple of 12, over virtual controllers.</summary>
    private static SpatialPathDto PathDto() => new(
    [
        new SpatialStageDto(12, [new SpatialBindingDto(Z("virtual:gpu"))]),
        new SpatialStageDto(24, [new SpatialBindingDto(Z("virtual:fan1")), new SpatialBindingDto(Z("virtual:dram1"))]),
        new SpatialStageDto(36, [new SpatialBindingDto(Z("virtual:fan2"), Reverse: true)]),
    ]);

    private static AssignmentDto Stacking(LoopReturnDto? r, SpatialPathDto? path = null, string strategy = "Vendor", string effect = "Stacking") =>
        new([Z("virtual:fan1"), Z("virtual:fan2"), Z("virtual:gpu"), Z("virtual:dram1")], effect, ["#FF0000"], 0.75f, 1f, false, 1, "Case",
            "LConnect", strategy, null, path ?? PathDto(), r);

    private static ProfileDto Demo(string file)
    {
        var json = File.ReadAllBytes(Path.Combine(RepoRoot(), "bringup", "demo-profiles", file));
        return JsonSerializer.Deserialize<ProfileDto>(json, ContractsJson.Configure(new JsonSerializerOptions()))!;
    }

    [Fact]
    public void The_mapper_round_trips_the_return_and_keeps_an_absent_one_absent()
    {
        var a = DtoMapper.FromDto(Stacking(Accepted));
        a.LoopReturn.Should().Be(new LoopReturn(4000, 600, 24, 0.25));
        DtoMapper.ToDto(new Scene([a])).Assignments[0].LoopReturn.Should().Be(Accepted);
        var odd = new LoopReturnDto(1234.5, 0, 2, 0.5);
        DtoMapper.ToDto(new Scene([DtoMapper.FromDto(Stacking(odd))])).Assignments[0].LoopReturn.Should().Be(odd, "fractional and boundary values survive");
        DtoMapper.FromDto(Stacking(null)).LoopReturn.Should().BeNull();
        DtoMapper.ToDto(new Scene([DtoMapper.FromDto(Stacking(null))])).Assignments[0].LoopReturn.Should().BeNull();
    }

    [Fact]
    public void Json_without_the_field_is_unchanged_and_with_it_uses_the_profile_names()
    {
        Encoding.UTF8.GetString(ProfileStore.Serialize(ProfileStoreTests.Sample())).Should().NotContainEquivalentOf("loopReturn");
        var opts = ContractsJson.Configure(new JsonSerializerOptions());
        var without = JsonSerializer.Serialize(Stacking(null), opts);
        without.Should().NotContainEquivalentOf("loopReturn", "an absent return is not written");
        JsonSerializer.Serialize(Stacking(null) with { LoopReturn = Accepted }, opts).Replace(",\"loopReturn\":{\"durationMs\":4000,\"holdMs\":600,\"waveWidthDots\":24,\"waveStrength\":0.25}", string.Empty)
            .Should().Be(without, "the return is one appended property, nothing else changes");
        JsonSerializer.Deserialize<AssignmentDto>(without, opts)!.LoopReturn.Should().BeNull("older JSON loads without a return");
        var with = JsonSerializer.Serialize(Stacking(Accepted), opts);
        JsonSerializer.Deserialize<AssignmentDto>(with, opts)!.LoopReturn.Should().Be(Accepted);
        var partial = without.Replace("\"spatialPath\":", "\"loopReturn\":{\"durationMs\":2500},\"spatialPath\":");
        JsonSerializer.Deserialize<AssignmentDto>(partial, opts)!.LoopReturn.Should().Be(Accepted with { DurationMs = 2500 }, "omitted fields take the design defaults");
    }

    public static TheoryData<string, AssignmentDto, string> BadDtos => new()
    {
        { "strength NaN", Stacking(Accepted with { WaveStrength = double.NaN }), "wave strength" },
        { "strength 0.9", Stacking(Accepted with { WaveStrength = 0.9 }), "wave strength" },
        { "width 1", Stacking(Accepted with { WaveWidthDots = 1 }), "wave width" },
        { "duration 999", Stacking(Accepted with { DurationMs = 999 }), "duration" },
        { "duration inf", Stacking(Accepted with { DurationMs = double.PositiveInfinity }), "duration" },
        { "hold 2001", Stacking(Accepted with { HoldMs = 2001 }), "hold" },
        { "Program", Stacking(Accepted, strategy: "Program"), "only with Vendor Stacking" },
        { "Runway", Stacking(Accepted, effect: "Runway"), "only with Vendor Stacking" },
        { "no path", Stacking(Accepted) with { SpatialPath = null }, "explicit spatial path" },
    };

    [Theory]
    [MemberData(nameof(BadDtos))]
    public void Malformed_or_unsupported_returns_are_rejected_by_the_mapper(string name, AssignmentDto dto, string message)
    {
        var act = () => DtoMapper.FromDto(dto);
        act.Should().Throw<ArgumentException>(name).WithMessage($"*{message}*");
    }

    [Fact]
    public async Task Save_profile_rejects_a_malformed_return_and_keeps_the_existing_file_bytes()
    {
        await using var host = await TestHost.StartAsync();
        var (rpc, svc, pipe) = await ConnectAsync(host);
        using (rpc)
        await using (pipe)
        {
            await svc.SaveProfileAsync(new ProfileDto("Stack", new SceneDto([Stacking(Accepted)])));
            var file = Path.Combine(host.Options.ResolvedDataDirectory, "profiles", "Stack.json");
            var before = File.ReadAllBytes(file);
            var stamp = File.GetLastWriteTimeUtc(file);
            Encoding.UTF8.GetString(before).Should().Contain("\"loopReturn\"");

            foreach (var bad in new[] { Stacking(Accepted with { WaveStrength = 0.9 }), Stacking(Accepted, strategy: "Program"), Stacking(Accepted) with { SpatialPath = null } })
            {
                var overwrite = () => svc.SaveProfileAsync(new ProfileDto("Stack", new SceneDto([bad])));
                await overwrite.Should().ThrowAsync<RemoteInvocationException>();
                File.ReadAllBytes(file).Should().Equal(before, "the rejected profile never reaches disk");
                File.GetLastWriteTimeUtc(file).Should().Be(stamp);
                var create = () => svc.SaveProfileAsync(new ProfileDto("Odd", new SceneDto([bad])));
                await create.Should().ThrowAsync<RemoteInvocationException>();
            }

            (await svc.ListProfilesAsync()).Select(p => p.Name).Should().Equal("Stack");
            (await svc.LoadProfileAsync("Stack")).Scene.Assignments[0].LoopReturn.Should().Be(Accepted);
        }
    }

    [Fact]
    public async Task Root_profile12_loads_maps_saves_and_applies_offline_and_profile11_stays_without_a_return()
    {
        var accepted = Demo("11-inner-group-full-stacking.json");
        var demo = Demo("12-stacking-wave-return.json");
        accepted.Scene.Assignments.Single().LoopReturn.Should().BeNull();
        demo.Scene.Assignments.Single().LoopReturn.Should().Be(Accepted);
        var a11 = DtoMapper.FromDto(accepted.Scene).Assignments.Single();
        var a12 = DtoMapper.FromDto(demo.Scene).Assignments.Single();
        a11.LoopReturn.Should().BeNull();
        a12.LoopReturn.Should().Be(new LoopReturn(4000, 600, 24, 0.25));
        a12.Path.Should().Be(a11.Path, "the same accepted path");
        a12.Path!.Stages.Select(s => s.Length).Should().Equal(36, 36, 36, 12, 24);
        (a12.Targets.SequenceEqual(a11.Targets), a12.EffectId, a12.Strategy, a12.Params.Speed, a12.Params.Brightness, a12.Params.Direction)
            .Should().Be((true, a11.EffectId, a11.Strategy, a11.Params.Speed, a11.Params.Brightness, a11.Params.Direction));

        await using var host = await TestHost.StartAsync();
        var (rpc, svc, pipe) = await ConnectAsync(host);
        using (rpc)
        await using (pipe)
        {
            await svc.SaveProfileAsync(demo with { Name = "Return" });
            var loaded = (await svc.LoadProfileAsync("Return")).Scene.Assignments.Single();
            loaded.LoopReturn.Should().Be(Accepted);
            loaded.SpatialPath!.Stages.Select(s => s.Length).Should().Equal(36, 36, 36, 12, 24);
            await svc.ApplyProfileAsync("Return");
            host.Engine.Loop.Scene.Assignments.Single().LoopReturn.Should().Be(a12.LoopReturn, "absence of the controllers is not a reason to reject it");
            (await svc.GetSceneAsync()).Assignments.Single().LoopReturn.Should().Be(Accepted);

            await svc.SaveProfileAsync(accepted with { Name = "Accepted" });
            await svc.ApplyProfileAsync("Accepted");
            host.Engine.Loop.Scene.Assignments.Single().LoopReturn.Should().BeNull("profile11 keeps playing the accepted loop");
        }
    }

    [Fact]
    public async Task Scene_rpcs_round_trip_the_return_merges_keep_it_and_a_bad_one_changes_nothing()
    {
        await using var host = await TestHost.StartAsync();
        var (rpc, svc, pipe) = await ConnectAsync(host);
        using (rpc)
        await using (pipe)
        {
            await svc.SetSceneAsync(new SceneDto([Stacking(Accepted)]));
            (await svc.GetSceneAsync()).Assignments.Single().LoopReturn.Should().Be(Accepted);

            await svc.SetAssignmentAsync(new AssignmentDto([Z("virtual:fan2")], "StaticColor", ["#123456"]));
            var scene = await svc.GetSceneAsync();
            scene.Assignments[0].LoopReturn.Should().Be(Accepted, "the pruned group keeps its authored return");
            scene.Assignments[0].SpatialPath!.Stages.Select(s => s.Length).Should().Equal(12, 24);
            scene.Assignments[1].LoopReturn.Should().BeNull();

            var running = host.Engine.Loop.Scene;
            var bad = () => svc.SetSceneAsync(new SceneDto([Stacking(Accepted with { WaveWidthDots = 97 })]));
            await bad.Should().ThrowAsync<RemoteInvocationException>();
            host.Engine.Loop.Scene.Should().BeSameAs(running, "a rejected scene is never activated");
        }
    }

    [Fact]
    public async Task The_scene_page_summarises_the_return_and_keeps_it_in_the_assignment()
    {
        await using var host = await TestHost.StartAsync();
        var (rpc, svc, pipe) = await ConnectAsync(host);
        using (rpc)
        await using (pipe)
        {
            var controllers = await svc.ListControllersAsync();
            await using var client = new PipeClient("UnifiedRgb-unused-" + Guid.NewGuid().ToString("N"));
            var vm = new SceneViewModel(client, InlineDispatcher.Instance, _ => { });
            vm.Show(controllers, new SceneDto([Stacking(Accepted), Stacking(null)]));
            var item = vm.Assignments[0];
            item.Preview.Should().BeNull();
            item.Targets.Should().StartWith("Spatial path, low → high source: 12: ")
                .And.Contain("\nPreview unavailable for explicit spatial paths.\n")
                .And.EndWith("\nLoop return after full: hold 600 ms, then 4000 ms reverse-flow drain (wave 24 dots, ripple 0.25).");
            item.Dto.LoopReturn.Should().Be(Accepted, "the GUI keeps the DTO it shows, so profile saves round-trip the options");
            SceneViewModel.ReturnText(new LoopReturnDto(1234.5, 0, 2, 0.5)).Should()
                .Be("Loop return after full: hold 0 ms, then 1234.5 ms reverse-flow drain (wave 2 dots, ripple 0.5).");
            vm.Assignments[1].Targets.Should().EndWith("\nPreview unavailable for explicit spatial paths.").And.NotContain("Loop return");
        }
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
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            if (File.Exists(Path.Combine(d.FullName, "AGENTS.md")) && Directory.Exists(Path.Combine(d.FullName, "bringup")))
            {
                return d.FullName;
            }
        }

        throw new DirectoryNotFoundException("repository root");
    }
}
