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
using UnifiedRgb.Devices;
using UnifiedRgb.Host.Ipc;
using UnifiedRgb.Host.Services;

namespace UnifiedRgb.Host.Tests;

/// <summary>
/// SPATIAL-PATH (Codex design 2026-10-18): the optional path through the DTO, the profile store, every scene RPC and the
/// GUI summary. Absent path = exact historical DTO/JSON; a malformed path never reaches the running scene or disk.
/// </summary>
public sealed class SpatialPathHostTests
{
    private static ZoneRefDto Z(string id) => new(id, 0);

    /// <summary>gpu | fan1 ‖ dram1 (parallel) | fan2 reversed — over virtual controllers.</summary>
    private static SpatialPathDto PathDto() => new(
    [
        new SpatialStageDto(12, [new SpatialBindingDto(Z("virtual:gpu"))]),
        new SpatialStageDto(24, [new SpatialBindingDto(Z("virtual:fan1")), new SpatialBindingDto(Z("virtual:dram1"))]),
        new SpatialStageDto(36, [new SpatialBindingDto(Z("virtual:fan2"), Reverse: true)]),
    ]);

    private static AssignmentDto Group(SpatialPathDto? path) =>
        new([Z("virtual:fan1"), Z("virtual:fan2"), Z("virtual:gpu"), Z("virtual:dram1")], "Runway", ["#FF0000"], 0.5f, 1f, false, 1, "Case",
            "Generic", "Program", null, path);

    private static void ShouldMatch(SpatialPathDto? actual, SpatialPathDto expected)
    {
        actual.Should().NotBeNull();
        actual!.Stages.Select(s => s.Length).Should().Equal(expected.Stages.Select(s => s.Length));
        for (var i = 0; i < expected.Stages.Count; i++)
        {
            actual.Stages[i].Bindings.Should().Equal(expected.Stages[i].Bindings);
        }
    }

    [Fact]
    public void The_mapper_round_trips_a_path_and_keeps_an_absent_one_absent()
    {
        var a = DtoMapper.FromDto(Group(PathDto()));
        a.Path!.Resolve()[new ZoneRef(new ControllerId("virtual:fan2"), new ZoneId(0))].Should().Be(new SpatialSpan(36, 36, true, 72));
        ShouldMatch(DtoMapper.ToDto(new Scene([a])).Assignments[0].SpatialPath, PathDto());
        DtoMapper.FromDto(Group(null)).Path.Should().BeNull();
        DtoMapper.ToDto(new Scene([DtoMapper.FromDto(Group(null))])).Assignments[0].SpatialPath.Should().BeNull();
    }

    [Fact]
    public void Json_without_the_field_is_the_historical_profile_byte_for_byte()
    {
        var legacy = ProfileStoreTests.Sample();
        var bytes = ProfileStore.Serialize(legacy);
        Encoding.UTF8.GetString(bytes).Should().NotContainEquivalentOf("spatialPath", "an absent path is not written");
        var opts = ContractsJson.Configure(new JsonSerializerOptions());
        var json = JsonSerializer.Serialize(Group(null), opts);
        json.Should().NotContainEquivalentOf("spatialPath");
        JsonSerializer.Deserialize<AssignmentDto>(json, opts)!.SpatialPath.Should().BeNull("older JSON loads without a path");
        var withPath = JsonSerializer.Serialize(Group(PathDto()), opts);
        withPath.Should().Contain("\"spatialPath\":{\"stages\"");
        ShouldMatch(JsonSerializer.Deserialize<AssignmentDto>(withPath, opts)!.SpatialPath, PathDto());
    }

    public static TheoryData<string, SpatialPathDto, string> BadDtos => new()
    {
        { "null stages", new SpatialPathDto(null!), "stage list" },
        { "null bindings", new SpatialPathDto([new SpatialStageDto(12, null!)]), "binding list" },
        { "null binding", new SpatialPathDto([new SpatialStageDto(12, [null!])]), "without a target" },
        { "null target", new SpatialPathDto([new SpatialStageDto(12, [new SpatialBindingDto(null!)])]), "without a target" },
        { "unbound target", new SpatialPathDto([new SpatialStageDto(12, [new SpatialBindingDto(Z("virtual:gpu"))])]), "does not bind target" },
        { "unknown binding", new SpatialPathDto([.. PathDto().Stages, new SpatialStageDto(12, [new SpatialBindingDto(Z("virtual:mb"))])]), "not a target" },
        { "length 0", new SpatialPathDto([.. PathDto().Stages.Take(2), new SpatialStageDto(0, [new SpatialBindingDto(Z("virtual:fan2"))])]), "outside 1..720" },
        { "total 721", new SpatialPathDto([.. PathDto().Stages.Take(2), new SpatialStageDto(685, [new SpatialBindingDto(Z("virtual:fan2"))])]), "exceeds 720" },
    };

    [Theory]
    [MemberData(nameof(BadDtos))]
    public void Malformed_dtos_are_rejected_by_the_mapper(string name, SpatialPathDto path, string message)
    {
        var act = () => DtoMapper.FromDto(Group(path));
        act.Should().Throw<ArgumentException>(name).WithMessage($"*{message}*");
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

    [Fact]
    public async Task Scene_and_profile_rpcs_round_trip_the_path_and_reject_a_bad_one_without_side_effects()
    {
        await using var host = await TestHost.StartAsync();
        var (rpc, svc, pipe) = await ConnectAsync(host);
        using (rpc)
        await using (pipe)
        {
            await svc.SetSceneAsync(new SceneDto([Group(PathDto())]));
            ShouldMatch((await svc.GetSceneAsync()).Assignments.Single().SpatialPath, PathDto());
            host.Engine.Loop.Scene.Assignments[0].Path!.TotalLength.Should().Be(72);

            // a rejected scene leaves the running scene (and active.json) exactly as it was
            var activeBefore = File.ReadAllBytes(Path.Combine(host.Options.ResolvedDataDirectory, "profiles", "active.json"));
            var before = host.Engine.Loop.Scene;
            var badPath = new SpatialPathDto(PathDto().Stages.Take(2).ToArray()); // fan2 unbound
            var bad = () => svc.SetSceneAsync(new SceneDto([Group(badPath)]));
            await bad.Should().ThrowAsync<RemoteInvocationException>();
            host.Engine.Loop.Scene.Should().BeSameAs(before);
            File.ReadAllBytes(Path.Combine(host.Options.ResolvedDataDirectory, "profiles", "active.json")).Should().Equal(activeBefore);
            var badAssign = () => svc.SetAssignmentAsync(Group(badPath));
            await badAssign.Should().ThrowAsync<RemoteInvocationException>();
            host.Engine.Loop.Scene.Should().BeSameAs(before);

            // profiles: Save → Load → Apply keep the path; a bad path is never written
            var profile = new ProfileDto("Spatial", new SceneDto([Group(PathDto())]));
            await svc.SaveProfileAsync(profile);
            ShouldMatch((await svc.LoadProfileAsync("Spatial")).Scene.Assignments[0].SpatialPath, PathDto());
            var reject = () => svc.SaveProfileAsync(new ProfileDto("Broken", new SceneDto([Group(badPath)])));
            await reject.Should().ThrowAsync<RemoteInvocationException>();
            (await svc.ListProfilesAsync()).Select(p => p.Name).Should().Equal("Spatial");
            var overwrite = () => svc.SaveProfileAsync(new ProfileDto("Spatial", new SceneDto([Group(badPath)])));
            await overwrite.Should().ThrowAsync<RemoteInvocationException>();
            ShouldMatch((await svc.LoadProfileAsync("Spatial")).Scene.Assignments[0].SpatialPath, PathDto());

            await svc.SetSceneAsync(new SceneDto([Group(null)]));
            host.Engine.Loop.Scene.Assignments[0].Path.Should().BeNull();
            await svc.ApplyProfileAsync("Spatial");
            host.Engine.Loop.Scene.Assignments[0].Path.Should().Be(DtoMapper.FromDto(PathDto()));
            ShouldMatch(new ProfileStore(host.Options).LoadActive()!.Scene.Assignments[0].SpatialPath, PathDto());
        }
    }

    /// <summary>R3: a Vendor Stacking group over two virtual targets with an 18 + 18 path (not multiples of 12).</summary>
    private static AssignmentDto OddStacking(string strategy = "Vendor", string effect = "Stacking") =>
        new([Z("virtual:fan1"), Z("virtual:gpu")], effect, ["#FF0000"], 0.5f, 1f, false, 1, "Case", "LConnect", strategy, null,
            new SpatialPathDto([new SpatialStageDto(18, [new SpatialBindingDto(Z("virtual:fan1"))]), new SpatialStageDto(18, [new SpatialBindingDto(Z("virtual:gpu"))])]));

    [Fact]
    public void R3_the_mapper_rejects_a_vendor_stacking_stage_that_is_not_a_multiple_of_twelve()
    {
        var act = () => DtoMapper.FromDto(OddStacking());
        act.Should().Throw<ArgumentException>().WithMessage("*Stacking stage 0 is 18 source dots, not a multiple of 12*");
        DtoMapper.FromDto(OddStacking(strategy: "Program")).Path!.TotalLength.Should().Be(36, "Program has no 12-dot deposit rule");
        DtoMapper.FromDto(OddStacking(effect: "Runway")).Path!.TotalLength.Should().Be(36, "other shared effects are unaffected");
    }

    [Fact]
    public async Task R3_save_profile_rejects_an_unplayable_stacking_path_and_keeps_the_existing_file_bytes()
    {
        await using var host = await TestHost.StartAsync();
        var (rpc, svc, pipe) = await ConnectAsync(host);
        using (rpc)
        await using (pipe)
        {
            await svc.SaveProfileAsync(new ProfileDto("Stack", new SceneDto([Group(PathDto())])));
            var file = Path.Combine(host.Options.ResolvedDataDirectory, "profiles", "Stack.json");
            var before = File.ReadAllBytes(file);
            var stamp = File.GetLastWriteTimeUtc(file);

            var overwrite = () => svc.SaveProfileAsync(new ProfileDto("Stack", new SceneDto([OddStacking()])));
            await overwrite.Should().ThrowAsync<RemoteInvocationException>();
            File.ReadAllBytes(file).Should().Equal(before, "the rejected profile never reaches disk");
            File.GetLastWriteTimeUtc(file).Should().Be(stamp);
            var create = () => svc.SaveProfileAsync(new ProfileDto("Odd", new SceneDto([OddStacking()])));
            await create.Should().ThrowAsync<RemoteInvocationException>();
            (await svc.ListProfilesAsync()).Select(p => p.Name).Should().Equal("Stack");
        }
    }

    [Fact]
    public async Task R3_an_offline_valid_profile_whose_controllers_are_all_absent_saves_and_applies()
    {
        // Root's offline candidate (read only): eight real-hardware targets, none present on the virtual test host
        var json = File.ReadAllBytes(Path.Combine(RepoRoot(), "bringup", "demo-profiles", "07-spatial-red-stacking.json"));
        var demo = JsonSerializer.Deserialize<ProfileDto>(json, ContractsJson.Configure(new JsonSerializerOptions()))!;
        var a = DtoMapper.FromDto(demo.Scene).Assignments.Single();
        a.Path!.TotalLength.Should().Be(144);
        a.Path.Stages.Skip(2).SelectMany(s => s.Bindings).Should().OnlyContain(b => b.Reverse, "top, side, bottom (high source end, deposited first) all locally reversed");
        a.Path.Stages.Take(2).SelectMany(s => s.Bindings).Should().OnlyContain(b => !b.Reverse);

        await using var host = await TestHost.StartAsync();
        var (rpc, svc, pipe) = await ConnectAsync(host);
        using (rpc)
        await using (pipe)
        {
            await svc.SaveProfileAsync(demo with { Name = "Offline" });
            ShouldMatch((await svc.LoadProfileAsync("Offline")).Scene.Assignments[0].SpatialPath, demo.Scene.Assignments[0].SpatialPath!);
            await svc.ApplyProfileAsync("Offline");
            host.Engine.Loop.Scene.Assignments[0].Path.Should().Be(a.Path, "absence of the controllers is not a reason to reject it");
        }
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

    [Fact]
    public async Task Taking_a_zone_into_another_assignment_unbinds_it_and_drops_its_empty_stage()
    {
        await using var host = await TestHost.StartAsync();
        var (rpc, svc, pipe) = await ConnectAsync(host);
        using (rpc)
        await using (pipe)
        {
            await svc.SetSceneAsync(new SceneDto([Group(PathDto())]));
            await svc.SetAssignmentAsync(new AssignmentDto([Z("virtual:fan2")], "StaticColor", ["#123456"]));
            var scene = await svc.GetSceneAsync();
            scene.Assignments.Should().HaveCount(2);
            scene.Assignments[0].Targets.Should().Equal(Z("virtual:fan1"), Z("virtual:gpu"), Z("virtual:dram1"));
            var rest = scene.Assignments[0].SpatialPath!;
            rest.Stages.Select(s => s.Length).Should().Equal(12, 24);
            rest.Stages[1].Bindings.Should().Equal(new SpatialBindingDto(Z("virtual:fan1")), new SpatialBindingDto(Z("virtual:dram1")));
            scene.Assignments[1].SpatialPath.Should().BeNull();

            await svc.SetAssignmentAsync(new AssignmentDto([Z("virtual:dram1")], "StaticColor", ["#654321"]));
            (await svc.GetSceneAsync()).Assignments[0].SpatialPath!.Stages[1].Bindings.Should().Equal(new SpatialBindingDto(Z("virtual:fan1")));
        }
    }

    [Fact]
    public async Task The_scene_page_summarises_the_path_and_never_shows_a_flat_preview()
    {
        await using var host = await TestHost.StartAsync();
        var (rpc, svc, pipe) = await ConnectAsync(host);
        using (rpc)
        await using (pipe)
        {
            var controllers = await svc.ListControllersAsync();
            await using var client = new PipeClient("UnifiedRgb-unused-" + Guid.NewGuid().ToString("N"));
            var vm = new SceneViewModel(client, InlineDispatcher.Instance, _ => { });
            vm.Show(controllers, new SceneDto([Group(PathDto()), Group(null) with { Targets = [Z("virtual:strip")], SpatialPath = null }]));
            var item = vm.Assignments[0];
            item.Preview.Should().BeNull("an explicit path has no honest flat preview");
            var name = (string id) => controllers.Single(c => c.Id == id).Name;
            item.Targets.Should().StartWith("Spatial path, low → high source: 12: ")
                .And.Contain($" | 24: {name("virtual:fan1")}/").And.Contain(" ‖ ")
                .And.Contain($" | 36: {name("virtual:fan2")}/").And.EndWith("\nPreview unavailable for explicit spatial paths.");
            item.Targets.Should().Contain(" ~", "the reversed binding is marked");
            vm.Assignments[1].Preview.Should().NotBeNull("an assignment without a path keeps its preview");
            vm.Assignments[1].Targets.Should().NotContain("Spatial path");
        }
    }
}
