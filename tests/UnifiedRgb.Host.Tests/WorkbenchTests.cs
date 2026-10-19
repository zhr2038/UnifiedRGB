using System.Reflection;
using System.Text.Json;
using UnifiedRgb.App.Ipc;
using UnifiedRgb.App.Mvvm;
using UnifiedRgb.App.ViewModels;
using UnifiedRgb.App.Workbench;
using UnifiedRgb.Contracts;
using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Host.Services;

namespace UnifiedRgb.Host.Tests;

#pragma warning disable VSTHRD003 // Regression tests deliberately hold RPC tasks behind latches to change the UI draft mid-request.

public sealed class WorkbenchTests
{
    private static readonly JsonSerializerOptions Options = ContractsJson.Configure(new JsonSerializerOptions());
    private static readonly ControllerDto Controller = new("lianli:rf:02-00-00-00-00-03", "Lian Li", "H2", "LianLi", "H2", "", [new(0, "ring", "Ring", 24, 0, 0, "H2", 1, FaceOnFill: true)], 60, "Active", null);
    private static readonly ZoneRefDto Target = new(Controller.Id, 0);
    private static readonly AssignmentDto Wave = new([Target], "Stacking", ["#FF0000"], .75f, 1, false, 73, "Accepted full stacking with H2-to-top reverse-flow wave return", "LConnect", "Vendor", null, new([new(24, [new(Target, true)])]), new(4000, 600, 24, .25));
    private static readonly EffectDto Effect = new("Stacking", "Stacking", 1, 3, true, true, true, ["#FF0000"]);

    [Fact]
    public void Existing_wave_assignment_roundtrips_without_losing_path_direction_seed_or_return()
    {
        var draft = Draft(); draft.Dirty.Should().BeFalse();
        draft.NameLabel.Should().Be("全机堆积 · 波浪回退");
        JsonSerializer.Serialize(draft.Build(), Options).Should().Be(JsonSerializer.Serialize(Wave, Options));
        draft.Brightness = 87;
        var edited = draft.Build();
        edited.Brightness.Should().Be(.87f); edited.Seed.Should().Be(73);
        edited.SpatialPath!.Stages[0].Bindings[0].Reverse.Should().BeTrue();
        edited.LoopReturn.Should().Be(Wave.LoopReturn);
    }

    [Fact]
    public void Legacy_frames_assignment_is_preserved_until_user_edits()
    {
        var original = Wave with { Strategy = "Frames", FrameSetId = "owner-animation", SpatialPath = null, LoopReturn = null, LoopPolicy = null };
        var draft = Draft(original);
        JsonSerializer.Serialize(draft.Build(), Options).Should().Be(JsonSerializer.Serialize(original, Options));
        draft.Name = "我的帧动画";
        draft.Build().FrameSetId.Should().Be("owner-animation");
    }

    [Theory]
    [InlineData(double.NaN, 4000)]
    [InlineData(-1, 4000)]
    [InlineData(101, 4000)]
    public void Invalid_draft_values_fail_before_any_rpc(double brightness, int unused)
    {
        _ = unused;
        var draft = Draft(); draft.Brightness = brightness;
        ((Action)(() => draft.Build())).Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Non_stacking_effect_cannot_silently_drop_the_enabled_return()
    {
        var draft = Draft();
        draft.Effect = new(new("Breathing", "Breathing", 1, 3, true, true, true, []));
        draft.ReturnEnabled.Should().BeFalse("switching effect clears incompatible options before applying");
        draft.ReturnEnabled = true;
        ((Action)(() => draft.Build())).Should().Throw<InvalidOperationException>().WithMessage("*波浪回退*");
    }

    [Fact]
    public void Duplicate_zone_is_rejected_without_mutating_the_path()
    {
        var draft = Draft();
        ((Action)(() => draft.AddStage([new(Controller, Controller.Zones[0])]))).Should().Throw<InvalidOperationException>();
        draft.Stages.Should().HaveCount(1); draft.Dirty.Should().BeFalse();
    }

    [Fact]
    public void Effect_changes_normalize_palette_and_return_options_without_a_failed_apply()
    {
        var draft = Draft(Wave with { Colors = ["#9709F7"] });
        draft.Effect = new(new("Rainbow", "Rainbow", 0, 0, true, true, true, []));
        draft.Colors.Should().BeEmpty(); draft.ReturnEnabled.Should().BeFalse(); draft.HasColors.Should().BeFalse();
        draft.Build().Colors.Should().BeEmpty(); draft.Build().LoopReturn.Should().BeNull();
        draft.Effect = new(Effect);
        draft.Colors.Should().Be("#9709F7");
        draft.Strategy = draft.Strategies.Single(s => s.Id == "Program");
        draft.SnapSpeed.Should().BeFalse(); draft.ReturnEnabled.Should().BeFalse();
    }

    [Fact]
    public void H2_direction_is_physical_and_stays_aligned_across_high_low_and_uniform_effects()
    {
        var draft = Draft();
        draft.Stages[0].Bindings[0].Reverse.Should().BeFalse("accepted Stacking reverse compensates its High projection");
        draft.Effect = new(new("Wave", "Wave", 1, 2, true, true, true, ["#FF0000", "#0000FF"]));
        draft.Build().SpatialPath!.Stages[0].Bindings[0].Reverse.Should().BeFalse();
        draft.Effect = new(new("Breathing", "Breathing", 1, 3, true, false, true, ["#FF0000"]));
        draft.Build().SpatialPath!.Stages[0].Bindings[0].Reverse.Should().BeFalse();
        draft.Effect = new(Effect);
        draft.Build().SpatialPath!.Stages[0].Bindings[0].Reverse.Should().BeTrue();
        draft.Stages[0].Bindings[0].Reverse = true;
        draft.Build().SpatialPath!.Stages[0].Bindings[0].Reverse.Should().BeFalse("the user can explicitly reverse the physical axis");
    }
    [Theory]
    [InlineData("RainbowMorph")]
    [InlineData("Twinkle")]
    public void Incompatible_vendor_family_routes_choose_the_shared_program_path_in_the_editor(string effect)
    {
        var draft = Draft(); draft.Effect = new(new(effect, effect, 0, 0, true, true, true, []));
        draft.Strategy!.Id.Should().Be("Program");
        draft.CanUseVendor.Should().BeFalse();
        draft.Build().LoopReturn.Should().BeNull();
        draft.Build().SpatialPath.Should().NotBeNull();
    }

    [Fact]
    public void Actual_effect_and_strategy_controls_keep_the_model_selection_and_never_write_on_change()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var (vm, rpc) = Workspace();
                vm.Draft.Effects.Add(new(Effect));
                vm.Draft.Load(Wave, [new(Controller, Controller.Zones[0])]);
                var rainbow = new EffectChoice(new("RainbowMorph", "RainbowMorph", 0, 0, true, true, true, []));
                vm.Draft.Effects.Add(rainbow);
                var view = new App.Views.LightingStudioView { DataContext = vm };
                view.Measure(new System.Windows.Size(1200, 800)); view.Arrange(new System.Windows.Rect(0, 0, 1200, 800));
                PumpBindings();
                var picker = (System.Windows.Controls.ComboBox)view.FindName("EffectPicker");
                picker.SelectedItem = rainbow;
                view.UpdateLayout();
                PumpBindings();
                vm.Draft.Effect!.Id.Should().Be("RainbowMorph", "the actual ComboBox selection must reach the model");
                vm.Draft.UseSpatialPath.Should().BeTrue();
                vm.Draft.Strategy!.Id.Should().Be("Program");
                vm.Draft.Colors.Should().BeEmpty(); vm.Draft.ReturnEnabled.Should().BeFalse();
                ((System.Windows.Controls.ComboBox)view.FindName("StrategyPicker")).SelectedItem.Should().Be(vm.Draft.Strategy);
                rpc.Writes.Should().BeEmpty();
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join(TimeSpan.FromSeconds(30)).Should().BeTrue();
        failure.Should().BeNull();
    }
    private static void PumpBindings()
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        _ = System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false));
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }

    [Fact]
    public void Blackout_backup_uses_a_name_supported_by_the_deployed_legacy_Host()
    {
        System.Text.RegularExpressions.Regex.IsMatch(StudioViewModel.BeforeLightsOffProfile, "^[A-Za-z0-9][A-Za-z0-9 _.\\-]{0,63}$").Should().BeTrue();
        ProfileStore.IsValidName(StudioViewModel.BeforeLightsOffProfile).Should().BeTrue();
        DisplayNames.Scene(StudioViewModel.BeforeLightsOffProfile).Should().Be("熄灯前自动备份");
    }

    [Fact]
    public void Chinese_profile_names_roundtrip_in_the_actual_profile_store()
    {
        var directory = Path.Combine(Path.GetTempPath(), "UnifiedRgb-ChineseProfiles-" + Guid.NewGuid().ToString("N"));
        var store = new ProfileStore(directory);
        try
        {
            var profile = new ProfileDto("皮卡丘 · 电光接力", new([Wave]));
            store.Save(profile);
            store.List().Single().Name.Should().Be(profile.Name);
            JsonSerializer.Serialize(store.Load(profile.Name), Options).Should().Be(JsonSerializer.Serialize(profile, Options));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                foreach (var file in Directory.EnumerateFiles(directory)) File.Delete(file);
                Directory.Delete(directory);
            }
        }
    }

    [Theory]
    [InlineData("CON.foo")]
    [InlineData("NUL.txt")]
    [InlineData("../我的场景")]
    [InlineData("文件/场景")]
    [InlineData("名称.")]
    public void Chinese_name_support_keeps_path_and_Windows_reserved_name_guards(string name) => ProfileStore.IsValidName(name).Should().BeFalse();

    [Fact]
    public async Task Reading_and_editing_workbench_never_sends_writes()
    {
        var (vm, rpc) = Workspace();
        await vm.RefreshAsync();
        vm.Draft.Brightness = 50; vm.Draft.ReturnSeconds = 3;
        vm.SelectedZone = vm.Zones[0]; vm.PreviewCommand.Execute(null); vm.Page = 1;
        rpc.Writes.Should().BeEmpty();
        vm.Draft.Dirty.Should().BeTrue();
    }

    [Fact]
    public async Task Applying_RGB_draft_preserves_current_screen_scene_and_unknown_assignment_fields()
    {
        var (vm, rpc) = Workspace();
        await vm.RefreshAsync(); vm.Draft.Brightness = 80;
        var newerScreens = new ScreenSceneDto([new("screen:h2:test", Overlay: "dashboard")]);
        rpc.Scene = rpc.Scene with { Screens = newerScreens };
        await vm.ApplyAsync();
        rpc.Scene.Screens.Should().BeSameAs(newerScreens);
        rpc.Scene.Assignments.Single().Seed.Should().Be(73);
        rpc.Scene.Assignments.Single().LoopReturn.Should().Be(Wave.LoopReturn);
        rpc.Writes.Should().Equal("SetSceneAsync");
    }

    [Fact]
    public async Task Another_RGB_edit_is_not_overwritten_by_a_stale_draft()
    {
        var (vm, rpc) = Workspace(); await vm.RefreshAsync(); vm.Draft.Speed = 50;
        rpc.Scene = rpc.Scene with { Assignments = [Wave with { Speed = .25f }] };
        await ((Func<Task>)vm.ApplyAsync).Should().ThrowAsync<InvalidOperationException>().WithMessage("*其他操作修改*");
        rpc.Writes.Should().BeEmpty();
    }

    [Fact]
    public async Task Blackout_covers_every_zone_and_restore_keeps_new_screen_edits()
    {
        var (vm, rpc) = Workspace(); await vm.RefreshAsync();
        var second = new ZoneDto(1, "second", "Linear", 4, 0, 0);
        rpc.Controllers = [Controller with { Zones = [Controller.Zones[0], second] }];
        await vm.BlackoutAsync();
        rpc.Scene.Assignments.Single().Targets.Should().HaveCount(2);
        rpc.Scene.Assignments.Single().Brightness.Should().Be(0);
        var newer = new ScreenSceneDto([new("screen:h2:test", BackgroundColor: "#102030")]);
        rpc.Scene = rpc.Scene with { Screens = newer };
        await vm.RestoreCommand.ExecuteAsync();
        rpc.Scene.Assignments.Should().ContainSingle().Which.Should().Be(Wave);
        rpc.Scene.Screens.Should().BeSameAs(newer);
    }

    [Fact]
    public void Discovery_ports_do_not_make_healthy_devices_look_failed_but_empty_is_not_healthy()
    {
        var port = Controller with { Id = "enedram:p0", Zones = [], State = "Blocked" };
        MainViewModel.TrayStateOf(true, [Controller, port]).Should().Be(TrayState.Ok);
        MainViewModel.TrayStateOf(true, [port]).Should().Be(TrayState.Attention);
        MainViewModel.TrayStateOf(true, []).Should().Be(TrayState.Attention);
        MainViewModel.TrayStateOf(true, [Controller, port with { Id = "unknown:blocked" }]).Should().Be(TrayState.Attention);
    }

    [Fact]
    public async Task Automatic_refresh_keeps_unsaved_draft_and_disconnection_clears_live_claims()
    {
        var (vm, _) = Workspace(); await vm.RefreshAsync(); vm.Draft.Brightness = 70;
        await vm.RefreshAsync(); vm.Draft.Brightness.Should().Be(70); vm.Draft.Dirty.Should().BeTrue();
        vm.SetConnected(false); vm.Health.Should().Contain("未连接"); vm.Period.Should().Be("—");
    }

    [Fact]
    public async Task Rediscovered_RAM_appears_in_topology_without_replacing_a_dirty_lighting_draft()
    {
        var (vm, rpc) = Workspace(); await vm.RefreshAsync(); vm.Draft.Brightness = 37;
        var existing = vm.Zones[0]; existing.IsChosen = true;
        rpc.Controllers = [Controller, Controller with { Id = "enedram:p0:0x71", DeviceClass = "EneDram", DeviceType = "ENE", Zones = [new(0, "RAM", "Linear", 8, 0, 0)] }];
        await vm.RefreshAsync();
        vm.Zones.Should().Contain(z => z.Controller.Id == "enedram:p0:0x71");
        vm.Zones[0].Should().BeSameAs(existing); existing.IsChosen.Should().BeTrue();
        vm.Draft.Brightness.Should().Be(37); vm.Draft.Dirty.Should().BeTrue(); rpc.Writes.Should().BeEmpty();
    }

    [Fact]
    public async Task Explicit_refresh_cannot_erase_an_edit_started_after_the_read()
    {
        var (vm, rpc) = Workspace(); await vm.RefreshAsync();
        var read = new TaskCompletionSource<SceneDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        rpc.ReadScene = () => read.Task;
        var pending = vm.RefreshAsync(true); vm.Draft.Brightness = 38; read.SetResult(rpc.Scene); await pending;
        vm.Draft.Brightness.Should().Be(38); vm.Draft.Dirty.Should().BeTrue();
    }

    [Fact]
    public async Task Delete_uses_the_confirmed_assignment_even_when_selection_changes_during_RPC()
    {
        var (vm, rpc) = Workspace(true);
        var other = Wave with { Targets = [new("other:controller", 0)], SpatialPath = null, Seed = 74, GroupName = "B" };
        rpc.Scene = rpc.Scene with { Assignments = [Wave, other] }; await vm.RefreshAsync();
        var read = new TaskCompletionSource<SceneDto>(TaskCreationOptions.RunContinuationsAsynchronously); rpc.ReadScene = () => read.Task;
        var pending = vm.RemoveAssignmentCommand.ExecuteAsync(); vm.SelectedAssignment = vm.Assignments[1];
        read.SetResult(rpc.Scene); await pending;
        rpc.Scene.Assignments.Should().ContainSingle().Which.Should().Be(other);
        vm.SelectedAssignment!.Dto.Should().Be(other); vm.SelectedAssignment.Index.Should().Be(0);
        rpc.SubmittedScenes.Should().OnlyContain(s => s.Screens == null);
    }

    [Fact]
    public async Task Apply_uses_the_original_assignment_when_selection_changes_during_RPC()
    {
        var (vm, rpc) = Workspace(true);
        var other = Wave with { Targets = [new("other:controller", 0)], SpatialPath = null, Seed = 74, GroupName = "B" };
        rpc.Scene = rpc.Scene with { Assignments = [Wave, other] }; await vm.RefreshAsync(); vm.Draft.Brightness = 70;
        var read = new TaskCompletionSource<SceneDto>(TaskCreationOptions.RunContinuationsAsynchronously); rpc.ReadScene = () => read.Task;
        var pending = vm.ApplyAsync(); vm.SelectedAssignment = vm.Assignments[1]; read.SetResult(rpc.Scene); await pending;
        rpc.Scene.Assignments[0].Brightness.Should().BeApproximately(.7f, .001f); rpc.Scene.Assignments[1].Should().Be(other);
        vm.SelectedAssignment!.Dto.Should().Be(other);
    }

    [Fact]
    public async Task RGB_only_actions_keep_paused_screens_paused_and_do_not_submit_a_screen_scene()
    {
        var (vm, rpc) = Workspace(); await vm.RefreshAsync(); rpc.ScreensPaused = true;
        vm.Draft.Brightness = 80; await vm.ApplyAsync(); await vm.BlackoutAsync(); await vm.RestoreCommand.ExecuteAsync();
        rpc.ScreensPaused.Should().BeTrue(); rpc.SubmittedScenes.Should().OnlyContain(s => s.Screens == null);
        rpc.Profiles[StudioViewModel.BeforeLightsOffProfile].Scene.Screens.Should().BeNull();
    }

    [Fact]
    public async Task Package_frames_are_labelled_immutable_and_cannot_be_edited_as_a_generator()
    {
        var (vm, rpc) = Workspace();
        rpc.Scene = new([Wave with { Strategy = "Frames", FrameSetId = "package:content:binding", EffectId = "StaticColor", Colors = [] }], Package: new("content", "binding", "rgb24-hold-physical-ms-v1"));
        await vm.RefreshAsync(); vm.CanEditDraft.Should().BeFalse(); vm.ApplyCommand.CanExecute(null).Should().BeFalse();
        vm.SceneDescription.Should().Contain("独立场景包"); vm.PeriodTitle.Should().Be("循环周期");
        await ((Func<Task>)vm.ApplyAsync).Should().ThrowAsync<InvalidOperationException>().WithMessage("*不可*修改*");
        rpc.Writes.Should().BeEmpty(); vm.NewCommand.Execute(null); vm.CanEditDraft.Should().BeTrue();
    }

    [Fact]
    public async Task Blackout_backup_and_restore_preserve_immutable_package_provenance()
    {
        var (vm, rpc) = Workspace();
        var provenance = new ScenePackageRefDto("content", "binding", "rgb24-hold-physical-ms-v1");
        rpc.Scene = new([Wave with { Strategy = "Frames", FrameSetId = "package:content:binding" }], Package: provenance);
        await vm.RefreshAsync(); await vm.BlackoutAsync();
        rpc.Profiles[StudioViewModel.BeforeLightsOffProfile].Scene.Package.Should().Be(provenance);
        await vm.RestoreCommand.ExecuteAsync(); rpc.Scene.Package.Should().Be(provenance);
        rpc.SubmittedScenes.Should().OnlyContain(s => s.Screens == null);
    }

    private static SceneDraft Draft(AssignmentDto? assignment = null)
    {
        var draft = new SceneDraft(); draft.Effects.Add(new(Effect));
        draft.Load(assignment ?? Wave, [new(Controller, Controller.Zones[0])]); return draft;
    }
    private static (StudioViewModel, WorkbenchRpcProxy) Workspace(bool confirm = false)
    {
        var service = DispatchProxy.Create<IRgbService, WorkbenchRpcProxy>();
        var rpc = (WorkbenchRpcProxy)(object)service;
        rpc.Scene = new([Wave], new([new("screen:h2:test")])); rpc.Controllers = [Controller]; rpc.Effects = [Effect];
        return (new(new FakeConnection(service).Connect(), InlineDispatcher.Instance, ex => throw ex, _ => confirm), rpc);
    }
}

public class WorkbenchRpcProxy : DispatchProxy
{
    public SceneDto Scene { get; set; } = new([]);
    public IReadOnlyList<ControllerDto> Controllers { get; set; } = [];
    public IReadOnlyList<EffectDto> Effects { get; set; } = [];
    public List<string> Writes { get; } = [];
    public Dictionary<string, ProfileDto> Profiles { get; } = [];
    public List<SceneDto> SubmittedScenes { get; } = [];
    public bool ScreensPaused { get; set; }
    public Func<Task<SceneDto>>? ReadScene { get; set; }
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        var method = targetMethod?.Name ?? throw new InvalidOperationException();
        switch (method)
        {
            case "ListControllersAsync": return Task.FromResult(Controllers);
            case "ListEffectsAsync": return Task.FromResult(Effects);
            case "GetSceneAsync": return ReadScene?.Invoke() ?? Task.FromResult(Scene);
            case "SetSceneAsync":
                var next = (SceneDto)args![0]!; SubmittedScenes.Add(next);
                if (next.Screens is not null) ScreensPaused = false;
                Scene = next.Screens is null ? next with { Screens = Scene.Screens } : next;
                Writes.Add(method); return Task.CompletedTask;
            case "SaveProfileAsync": var profile = (ProfileDto)args![0]!; Profiles[profile.Name] = profile; Writes.Add(method); return Task.CompletedTask;
            case "LoadProfileAsync": return Task.FromResult(Profiles[(string)args![0]!]);
            default: if (method.StartsWith("add_", StringComparison.Ordinal) || method.StartsWith("remove_", StringComparison.Ordinal)) return null; throw new InvalidOperationException("Unexpected RPC: " + method);
        }
    }
}
