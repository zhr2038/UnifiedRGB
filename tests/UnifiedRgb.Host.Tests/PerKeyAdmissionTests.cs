using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using UnifiedRgb.App.Ipc;
using UnifiedRgb.App.Mvvm;
using UnifiedRgb.App.ViewModels;
using UnifiedRgb.App.Workbench;
using UnifiedRgb.Contracts;
using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Devices;
using UnifiedRgb.Host.Services;

namespace UnifiedRgb.Host.Tests;

#pragma warning disable VSTHRD003 // A latch holds the restore's controller read so it can be cancelled mid-request.

/// <summary>
/// Fake-only per-key admission: a Ready capability (a local route map that passed the backend's map gates) is admitted whatever
/// the restoration diagnostics say; only an actual Failed report or a capability that is not Ready blocks, and that is checked
/// before any request. Aggregate whole-keyboard colour is never per-key. Nothing here sends to a device or proves optical output
/// or a physical restore.
/// </summary>
public sealed partial class PerKeyAdmissionTests : IDisposable
{
    private const string KeyboardId = "asusr:synthetic:2";
    private const string ListCall = nameof(IRgbService.ListControllersAsync);
    private const string GetCall = nameof(IRgbService.GetSceneAsync);
    private const string SetCall = nameof(IRgbService.SetSceneAsync);
    private const string SaveCall = nameof(IRgbService.SaveProfileAsync);
    private const string LoadCall = nameof(IRgbService.LoadProfileAsync);
    private const string ReadyLabel = "逐灯可用 · 2 灯位";
    private const string FailedLabel = "逐键不可用 · 原始灯光恢复失败，不发送逐键输出";
    private const string HeldLabel = "逐键未启用 · 当前分区仍为整键盘单色";
    private static readonly JsonSerializerOptions Options = ContractsJson.Configure(new JsonSerializerOptions());
    private static readonly ZoneRefDto Keys = new(KeyboardId, 0);
    private static readonly ZoneRefDto Case = new("case:one", 0);
    private static readonly ZoneRefDto Whole = new("asusr:synthetic:3", 0);
    private static readonly ZoneRefDto Held = new("asusr:synthetic:4", 0);
    private static readonly EffectDto Static = new("StaticColor", "Static", 1, 1, false, false, false, ["#FFFFFF"]);
    private static readonly PerKeyCapability ReadyMap = new(PerKeyState.Ready, "Local map gates passed; optical playback is not certified.", "rog-rf64-c081-v1", 2, 128);
    private static readonly PerKeyCapability NoMap = new(PerKeyState.MissingMap, "no local map", null, 0, 128);
    // A Ready map whose backend reports an actual restore failure: the one remaining refusal of a Ready per-key zone.
    private static readonly ZoneDto FailedZone = Restoration(PerKeyZone(ReadyMap), PerKeyRestorationStates.Failed, "snapshot read failed");
    private static readonly ControllerDto CaseController = new(Case.ControllerId, "test", "case", "Virtual", "Linear", "",
        [new(0, "zone", "Linear", 4, 0, 0)], 60, ControllerStates.Active, null, true);
    private static readonly ControllerDto WholeKeyboard = new(Whole.ControllerId, "Test", "Keyboard", "AsusRog", "Keyboard", "",
        [AggregateZoneDto(NoMap)], 30, ControllerStates.Active, null, true);
    // A Ready map on the aggregate shape: the zone stays the whole-keyboard Point, so its output stays one colour.
    private static readonly ControllerDto HeldKeyboard = WholeKeyboard with { Id = Held.ControllerId, Zones = [AggregateZoneDto(ReadyMap)] };

    // Every frozen PerKeyState has its own truthful label; a new state must not silently fall through to the default.
    private static readonly Dictionary<PerKeyState, string> Labels = new()
    {
        [PerKeyState.Unsupported] = "不支持逐键",
        [PerKeyState.Disabled] = "整键盘单色是独立能力",
        [PerKeyState.MissingMap] = "逐键需校准 · 缺少本机物理映射",
        [PerKeyState.InvalidMap] = "逐键需校准 · 映射无效",
        [PerKeyState.TransportMismatch] = "逐键需校准 · 当前路由没有对应映射",
        [PerKeyState.Unverified] = "逐键需校准 · 待物理逐键扫描",
        [PerKeyState.ControlUnverified] = "逐键需校准 · 灯光控制待校验",
        [PerKeyState.KeyOrderUnverified] = "逐键需校准 · 键位顺序待校验",
        [PerKeyState.Ready] = ReadyLabel,
    };

    private readonly List<IDisposable> _owned = [];

    [Fact]
    public void Host_maps_every_capability_state_with_restoration_unproven_and_admits_exactly_the_ready_map()
    {
        DtoMapper.RestorationNotReported.Should().NotContainAny("forbidden", "not admitted", "no per-key output");
        Labels.Keys.Should().BeEquivalentTo(Enum.GetValues<PerKeyState>());
        foreach (var state in Enum.GetValues<PerKeyState>())
        {
            var capability = new PerKeyCapability(state, $"reason {state}", "rog-rf64-c081-v1", 2, 128);
            var zone = PerKeyZone(capability);
            zone.PerKey!.State.Should().Be(state.ToString());
            zone.PerKey.Restoration.Should().Be(PerKeyRestorationStates.Unproven, "no backend reports restoration, and it is never invented ({0})", state);
            zone.PerKey.RestorationReason.Should().Be(DtoMapper.RestorationNotReported);
            var result = PerKeyAdmission.Evaluate(zone);
            result.AllowsOutput.Should().Be(state == PerKeyState.Ready, "unproven restoration is diagnostic; only Ready is admitted ({0})", state);
            result.Verdict.Should().Be(state == PerKeyState.Ready ? PerKeyAdmissionVerdict.Admitted : PerKeyAdmissionVerdict.CapabilityBlocked);
            PerKeyDisplay.Zone(zone).Should().Contain(Labels[state]).And.NotContain("已准许");

            // The same status on the aggregate whole-keyboard zone is not a per-key target: it outputs one replicated colour.
            var aggregate = AggregateZoneDto(capability);
            PerKeyAdmission.IsAggregate(aggregate).Should().BeTrue();
            PerKeyAdmission.Evaluate(aggregate).Verdict.Should().Be(PerKeyAdmissionVerdict.NotPerKey);
            PerKeyDisplay.Zone(aggregate).Should().Be(state == PerKeyState.Ready ? HeldLabel : PerKeyDisplay.Zone(zone));
        }
    }

    [Fact]
    public void Host_maps_the_software_frame_for_review_only_and_a_failing_frame_source_only_omits_it()
    {
        var descriptor = new ControllerDescriptor(new(KeyboardId), "Test", "Keyboard", DeviceType.Keyboard, DeviceClass.AsusRog, "",
            [new ZoneDescriptor(new(0), "keys", ZoneLayout.Linear(2)) { PerKey = ReadyMap }], Capabilities.DirectOnly(30));
        var frame = new PerKeyFrame(7, [new(200, "SyntheticA", 0, 0, new Rgb(1, 2, 3)), new(3, "SyntheticB", 1, 4, new Rgb(4, 5, 6))]);
        var dto = DtoMapper.ToDto(new ControllerEntry(descriptor, new FrameAdapter(() => frame)));
        dto.PerKeyFrame!.Index.Should().Be(7);
        dto.PerKeyFrame.Keys.Select(k => k.LedId).Should().Equal((byte)200, (byte)3);
        dto.PerKeyFrame.Keys.Select(k => k.Color).Should().Equal("#010203", "#040506");
        PerKeyAdmission.Evaluate(dto.Zones.Single()).Verdict.Should().Be(PerKeyAdmissionVerdict.Admitted, "the Ready map admits; the frame is review data only");
        var item = new ControllerItem(dto);
        item.ChinesePerKey.Should().Be(ReadyLabel);
        item.PerKeyFrameSummary.Should().Contain("非实机证明");
        item.PerKeyPixels.Select(k => k.Key).Should().Equal("SyntheticA", "SyntheticB");
        var json = JsonSerializer.Serialize(dto, Options);
        JsonSerializer.Serialize(JsonSerializer.Deserialize<ControllerDto>(json, Options), Options).Should().Be(json);

        var failing = DtoMapper.ToDto(new ControllerEntry(descriptor, new FrameAdapter(() => throw new IOException("frame read failed"))));
        failing.PerKeyFrame.Should().BeNull();
        failing.Zones.Single().PerKey!.State.Should().Be("Ready");
        new ControllerItem(failing).PerKeyFrameSummary.Should().Be("无逐键帧 · 整体颜色或逐键未就绪");
    }

    [Fact]
    public void Older_host_json_without_restoration_reads_unproven_is_admitted_when_ready_and_legacy_zones_keep_their_shape()
    {
        var node = JsonNode.Parse(JsonSerializer.Serialize(Keyboard(Restoration(PerKeyZone(ReadyMap), PerKeyRestorationStates.Verified, "fake")), Options))!;
        var status = node["zones"]![0]!["perKey"]!.AsObject();
        status.Remove("restoration").Should().BeTrue();
        status.Remove("restorationReason").Should().BeTrue();
        var older = JsonSerializer.Deserialize<ControllerDto>(node.ToJsonString(), Options)!.Zones.Single();
        older.PerKey!.Restoration.Should().Be(PerKeyRestorationStates.Unproven);
        older.PerKey.RestorationReason.Should().BeNull();
        PerKeyAdmission.Evaluate(older).Should().Be(new PerKeyAdmissionResult(PerKeyAdmissionVerdict.Admitted,
            "Per-key map is Ready for rog-rf64-c081-v1 (2 keys); restoration diagnostics report Unproven (informational only); optical output is not certified."));

        var legacy = JsonSerializer.Serialize(CaseController, Options);
        legacy.Should().NotContain("perKey", "neither perKey nor perKeyFrame is written for a device without per-key status");
        JsonSerializer.Deserialize<ControllerDto>(legacy, Options)!.Zones.Single().PerKey.Should().BeNull();
    }

    [Fact]
    public void Admission_needs_only_a_ready_map_an_actual_restore_failure_blocks_and_restoration_cannot_lift_a_blocked_capability()
    {
        var ready = PerKeyZone(ReadyMap);
        foreach (var word in new[] { PerKeyRestorationStates.Unproven, PerKeyRestorationStates.Verified, "Pending", "verified", "" })
        {
            var zone = Restoration(ready, word, "diagnostic text");
            PerKeyAdmission.Evaluate(zone).Verdict.Should().Be(PerKeyAdmissionVerdict.Admitted, "restoration word '{0}' is informational", word);
            PerKeyDisplay.Zone(zone).Should().Be(ReadyLabel);
        }
        PerKeyAdmission.Evaluate(Complete(ready)).Verdict.Should().Be(PerKeyAdmissionVerdict.Admitted);
        PerKeyDisplay.Zone(Complete(ready)).Should().Be(ReadyLabel);

        PerKeyAdmission.Evaluate(FailedZone).Should().Be(new PerKeyAdmissionResult(PerKeyAdmissionVerdict.RestorationFailed, "snapshot read failed"));
        PerKeyDisplay.Zone(FailedZone).Should().Be(FailedLabel);
        PerKeyAdmission.Evaluate(Restoration(ready, PerKeyRestorationStates.Failed)).Reason.Should().Contain("preflight failed");

        var missing = PerKeyAdmission.Evaluate(Restoration(PerKeyZone(NoMap), PerKeyRestorationStates.Verified));
        missing.Should().Be(new PerKeyAdmissionResult(PerKeyAdmissionVerdict.CapabilityBlocked, "Per-key capability is MissingMap: no local map"));
        PerKeyAdmission.Evaluate(Complete(PerKeyZone(new PerKeyCapability(PerKeyState.KeyOrderUnverified, "order", "rog-rf64-c081-v1", 2, 128))))
            .Verdict.Should().Be(PerKeyAdmissionVerdict.CapabilityBlocked, "a complete restoration report does not make a map Ready");

        PerKeyAdmission.Evaluate(WholeKeyboard.Zones.Single()).AllowsOutput.Should().BeTrue("whole-keyboard colour is not per-key output");
        PerKeyAdmission.Evaluate(HeldKeyboard.Zones.Single()).AllowsOutput.Should().BeTrue("a Ready map on the aggregate shape still outputs one colour");
        PerKeyAdmission.Evaluate(Restoration(HeldKeyboard.Zones.Single(), PerKeyRestorationStates.Failed)).AllowsOutput.Should().BeTrue("aggregate colour is independent");
        PerKeyDisplay.Zone(HeldKeyboard.Zones.Single()).Should().Be(HeldLabel);
        PerKeyAdmission.Evaluate(ready with { Mapped = false }).Verdict.Should().Be(PerKeyAdmissionVerdict.Admitted, "only the unmapped Point shape is aggregate");
        PerKeyAdmission.Evaluate(Restoration(PerKeyZone(NoMap), PerKeyRestorationStates.Verified) with { Mapped = false, Layout = "Point" })
            .Verdict.Should().Be(PerKeyAdmissionVerdict.NotPerKey);
        PerKeyAdmission.Evaluate(CaseController.Zones.Single()).Verdict.Should().Be(PerKeyAdmissionVerdict.NotPerKey);
        PerKeyDisplay.Zone(CaseController.Zones.Single()).Should().Be(PerKeyDisplay.NotPerKey);
    }

    [Fact]
    public void Blocked_lists_each_refused_per_key_target_once_and_leaves_unknown_targets_to_the_host()
    {
        var keyboard = Keyboard(FailedZone);
        var blocked = PerKeyAdmission.Blocked([Keys, Keys, Case, Whole, Held, new(KeyboardId, 9), new("absent:one", 0)], [keyboard, CaseController, WholeKeyboard, HeldKeyboard]);
        var only = blocked.Should().ContainSingle().Subject;
        only.Target.Should().Be(Keys);
        only.Controller.Should().BeSameAs(keyboard);
        only.Result.Verdict.Should().Be(PerKeyAdmissionVerdict.RestorationFailed);
        PerKeyAdmission.Blocked([Keys, Case, Whole, Held], [Keyboard(PerKeyZone(ReadyMap)), CaseController, WholeKeyboard, HeldKeyboard]).Should().BeEmpty();
        PerKeyAdmission.Blocked([Keys], [Keyboard(PerKeyZone(NoMap))])
            .Should().ContainSingle().Which.Result.Verdict.Should().Be(PerKeyAdmissionVerdict.CapabilityBlocked);

        var refusal = FluentActions.Invoking(() => PerKeyDisplay.RequireAdmitted([Keys, Case, Whole], [keyboard, CaseController, WholeKeyboard]))
            .Should().Throw<InvalidOperationException>().Which.Message;
        refusal.Should().StartWith("逐键输出未准许，未发送任何请求；草稿、当前场景与亮度保持不变。").And.Contain("原始灯光恢复失败");
        refusal.Split('\n').Should().HaveCount(2, "only the per-key zone is listed");
        FluentActions.Invoking(() => PerKeyDisplay.RequireAdmitted([Keys, Case, Whole, Held], [Keyboard(PerKeyZone(ReadyMap)), CaseController, WholeKeyboard, HeldKeyboard]))
            .Should().NotThrow();
    }

    [Fact]
    public async Task Studio_refuses_a_failed_per_key_zone_before_reading_the_scene_keeps_the_draft_and_sends_a_ready_map_once()
    {
        var (vm, rpc, errors) = Studio(Keyboard(FailedZone));
        await vm.RefreshAsync();
        vm.Zones.Single(z => z.Ref == Keys).Detail.Should().Contain("原始灯光恢复失败");
        vm.Draft.Brightness = 70;
        vm.Draft.Dirty.Should().BeTrue();
        var running = rpc.Scene;
        rpc.Calls.Clear();

        await vm.ApplyCommand.ExecuteAsync();

        errors.Should().ContainSingle().Which.Should().BeOfType<InvalidOperationException>()
            .Which.Message.Should().Contain("逐键输出未准许").And.Contain("原始灯光恢复失败");
        rpc.Calls.Should().Equal(ListCall);
        rpc.Scene.Should().BeSameAs(running);
        vm.Draft.Dirty.Should().BeTrue();
        vm.Draft.Brightness.Should().Be(70);
        vm.ApplyCommand.CanExecute(null).Should().BeTrue("the command re-enables after a refusal");

        // The Host's fresh report is a Ready map with restoration only Unproven (what it really maps): the draft is sent once.
        rpc.Controllers = [CaseController, Keyboard(PerKeyZone(ReadyMap))];
        rpc.Calls.Clear();
        await vm.ApplyCommand.ExecuteAsync();
        errors.Should().ContainSingle();
        rpc.Calls.Take(3).Should().Equal(ListCall, GetCall, SetCall);
        rpc.Scene.Assignments.Should().ContainSingle().Which.Brightness.Should().BeApproximately(.7f, .001f);
    }

    [Theory]
    [InlineData(false, "缺少本机物理映射")]
    [InlineData(true, HeldLabel)]
    public async Task Studio_sends_whole_keyboard_colour_on_the_aggregate_zone_unchanged(bool readyMap, string label)
    {
        var aggregate = (readyMap ? HeldKeyboard : WholeKeyboard) with { Id = KeyboardId };
        var (vm, rpc, errors) = Studio(aggregate);
        await vm.RefreshAsync();
        vm.Zones.Single(z => z.Ref == Keys).Detail.Should().Contain(label);
        vm.Draft.Brightness = 40;
        rpc.Calls.Clear();

        await vm.ApplyCommand.ExecuteAsync();

        errors.Should().BeEmpty();
        rpc.Calls.Take(3).Should().Equal(ListCall, GetCall, SetCall);
        var sent = rpc.Scene.Assignments.Should().ContainSingle().Subject;
        sent.Targets.Should().Equal(Keys);
        sent.Brightness.Should().BeApproximately(.4f, .001f);
    }

    [Fact]
    public async Task Legacy_effect_and_group_pages_refuse_a_failed_zone_before_requiring_the_host_and_pass_a_ready_map_on()
    {
        await using var client = new PipeClient("UnifiedRgb-unused-" + Guid.NewGuid().ToString("N"));
        var errors = new List<Exception>();
        using var fx = new EffectsViewModel(client, InlineDispatcher.Instance, errors.Add) { LiveApply = false };
        fx.Effects.Add(Static);
        fx.SelectedEffect = Static;
        fx.SetControllers([CaseController, Keyboard(FailedZone)]);
        foreach (var z in fx.Zones) z.IsSelected = true;

        await fx.ApplyCommand.ExecuteAsync();

        errors.Should().ContainSingle().Which.Should().BeOfType<InvalidOperationException>().Which.Message.Should().Contain("逐键输出未准许");
        fx.Zones.Should().HaveCount(2).And.OnlyContain(z => z.IsSelected);
        fx.ApplyCommand.CanExecute(null).Should().BeTrue();

        // A Ready map (restoration Unproven) and the aggregate whole-keyboard zone are not refused: the request goes on to the
        // (absent) Host connection.
        foreach (var keyboard in new[] { Keyboard(PerKeyZone(ReadyMap)), WholeKeyboard with { Id = KeyboardId } })
        {
            fx.SetControllers([CaseController, keyboard]);
            foreach (var z in fx.Zones) z.IsSelected = true;
            await fx.ApplyCommand.ExecuteAsync();
            errors[^1].Should().BeOfType<HostNotConnectedException>();
        }
        errors.Should().HaveCount(3);

        var scene = new SceneViewModel(client, InlineDispatcher.Instance, errors.Add);
        scene.Show([Keyboard(FailedZone)], new([]));
        scene.Candidate = scene.AvailableZones.Single();
        scene.AddToGroupCommand.Execute(null);
        await scene.ApplyGroupCommand.ExecuteAsync();
        errors.Should().HaveCount(4);
        errors[3].Should().BeOfType<InvalidOperationException>().Which.Message.Should().Contain("逐键输出未准许");
        scene.GroupZones.Should().ContainSingle();
        scene.ApplyGroupCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public async Task Saved_scene_apply_is_checked_on_its_stored_targets_before_any_apply_request()
    {
        var service = DispatchProxy.Create<IRgbService, PerKeyRpc>();
        var rpc = (PerKeyRpc)(object)service;
        rpc.Controllers = [CaseController, Keyboard(FailedZone), HeldKeyboard];
        rpc.Backup = new("Evening", new([new([Case, Keys], "Breathing", ["#FFCC00"])]));

        await FluentActions.Awaiting(() => PerKeyDisplay.RequireProfileAdmittedAsync(service, "Evening"))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("逐键输出未准许*");
        rpc.Calls.Should().Equal(LoadCall, ListCall);

        rpc.Backup = new("Evening", new([new([Case, Held], "Breathing", ["#FFCC00"])]));
        await PerKeyDisplay.RequireProfileAdmittedAsync(service, "Evening");
        rpc.Controllers = [CaseController, Keyboard(PerKeyZone(ReadyMap)), HeldKeyboard];
        rpc.Backup = new("Evening", new([new([Case, Keys], "Breathing", ["#FFCC00"])]));
        await PerKeyDisplay.RequireProfileAdmittedAsync(service, "Evening");
        rpc.Calls.Should().Equal(LoadCall, ListCall, LoadCall, ListCall, LoadCall, ListCall);
        rpc.Submitted.Should().BeEmpty();
    }

    [Fact]
    public async Task Lights_off_leaves_a_failed_per_key_zone_out_and_says_so_and_includes_a_ready_map()
    {
        var (actions, rpc) = CasePower(FailedZone);
        var running = rpc.Scene;

        var result = await actions.OffAsync();

        result.Changed.Should().BeTrue();
        result.Message.Should().EndWith("1 个逐键分区未准许逐键输出，未包含在熄灯指令中，保持原状。");
        rpc.Calls.Should().Equal(GetCall, ListCall, SaveCall, SetCall);
        rpc.Submitted.Should().ContainSingle().Which.Assignments.Should().ContainSingle().Which.Targets.Should().Equal(Case, Whole, Held);
        rpc.Backup!.Scene.Assignments.Should().BeSameAs(running.Assignments);
        actions.Status.State.Should().Be(CasePowerRequestState.OffRequested);

        // A Ready map with restoration only Unproven is ordinary output: black reaches it with every other zone.
        var (ready, readyRpc) = CasePower(PerKeyZone(ReadyMap));
        (await ready.OffAsync()).Message.Should().NotContain("未准许");
        readyRpc.Submitted.Should().ContainSingle().Which.Assignments.Should().ContainSingle().Which.Targets.Should().Equal(Case, Keys, Whole, Held);

        // Only a refused per-key zone active: nothing can be turned off, nothing is saved or sent.
        var (alone, only) = CasePower(FailedZone);
        only.Controllers = [Keyboard(FailedZone)];
        await alone.Awaiting(a => a.OffAsync()).Should().ThrowAsync<InvalidOperationException>().WithMessage("没有可以熄灭的灯光分区。");
        only.Calls.Should().Equal(GetCall, ListCall);
        alone.Status.State.Should().Be(CasePowerRequestState.Failed);
    }

    [Fact]
    public async Task Restore_refuses_a_recovery_point_with_a_failed_per_key_zone_before_any_write_and_keeps_it()
    {
        var (actions, rpc) = CasePower(FailedZone);
        var backup = new ProfileDto(CasePowerActions.BackupProfileName, new([new([Case, Keys], "Breathing", ["#FFCC00"])]));
        rpc.Backup = backup;

        var refusal = await actions.Awaiting(a => a.RestoreBackupAsync()).Should().ThrowAsync<InvalidOperationException>();

        refusal.Which.Message.Should().Contain("逐键输出未准许");
        rpc.Calls.Should().Equal(LoadCall, ListCall);
        rpc.Submitted.Should().BeEmpty();
        rpc.Backup.Should().BeSameAs(backup);
        actions.Status.State.Should().Be(CasePowerRequestState.Failed);

        // Once the fake Host no longer reports the failure (a Ready map, restoration Unproven), the kept point restores as a whole.
        rpc.Controllers = [CaseController, Keyboard(PerKeyZone(ReadyMap))];
        (await actions.RestoreBackupAsync()).Changed.Should().BeTrue();
        rpc.Submitted.Should().ContainSingle().Which.Assignments.Should().Equal(backup.Scene.Assignments);
        actions.Status.State.Should().Be(CasePowerRequestState.RestoreRequested);
    }

    [Fact]
    public async Task Cancelling_restore_during_the_admission_read_sends_nothing_and_reports_failed_not_unconfirmed()
    {
        var (actions, rpc) = CasePower(PerKeyZone(ReadyMap));
        rpc.Backup = new(CasePowerActions.BackupProfileName, new([new([Case], "Breathing", ["#FFCC00"])]));
        var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rpc.ListOverride = async ct =>
        {
            reading.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return rpc.Controllers;
        };
        using var cts = new CancellationTokenSource();

        var restore = actions.RestoreBackupAsync(cts.Token);
        await reading.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cts.CancelAsync();

        await FluentActions.Awaiting(() => restore).Should().ThrowAsync<OperationCanceledException>();
        rpc.Calls.Should().Equal(LoadCall, ListCall);
        rpc.Submitted.Should().BeEmpty();
        actions.Status.State.Should().Be(CasePowerRequestState.Failed);
    }

    public void Dispose()
    {
        foreach (var owned in _owned) owned.Dispose();
    }

    private static ZoneDto PerKeyZone(PerKeyCapability capability) =>
        DtoMapper.ToDto(new ZoneDescriptor(new(0), "keys", ZoneLayout.Linear(2)) { PerKey = capability });

    private static ZoneDto AggregateZoneDto(PerKeyCapability capability) =>
        DtoMapper.ToDto(AggregateZone.Descriptor("Keyboard") with { PerKey = capability });

    private static ZoneDto Restoration(ZoneDto zone, string restoration, string? reason = null) =>
        zone with { PerKey = zone.PerKey! with { Restoration = restoration, RestorationReason = reason } };

    /// <summary>Restoration diagnostics Verified with every perkey-restoration-v1 component for the zone's own transport (fake only; never required).</summary>
    private static ZoneDto Complete(ZoneDto zone) =>
        zone with
        {
            PerKey = zone.PerKey! with
            {
                Restoration = PerKeyRestorationStates.Verified, RestorationReason = null, RestorationCapability = Capability(zone.PerKey!.Transport),
            },
        };

    private static PerKeyRestorationCapabilityDto Capability(string? transport) =>
        new(PerKeyRestorationContract.Version, PerKeyRestorationStates.Verified, "synthetic snapshot and restore preflight", transport, SyntheticScope,
            [.. PerKeyRestorationContract.Required.Select(c => new PerKeyRestorationComponentDto(c, PerKeyRestorationStates.Verified))]);

    private static ControllerDto Keyboard(ZoneDto zone) =>
        new(KeyboardId, "Test", "Keyboard", "AsusRog", "Keyboard", "", [zone], 30, ControllerStates.Active, null, true);

    private (StudioViewModel Vm, PerKeyRpc Rpc, List<Exception> Errors) Studio(ControllerDto keyboard)
    {
        var service = DispatchProxy.Create<IRgbService, PerKeyRpc>();
        var rpc = (PerKeyRpc)(object)service;
        rpc.Controllers = [CaseController, keyboard];
        rpc.Effects = [Static];
        rpc.Scene = new([new([Keys], "StaticColor", ["#00FF00"])]);
        var errors = new List<Exception>();
        var vm = new StudioViewModel(new FakeConnection(service).Connect(), InlineDispatcher.Instance, errors.Add, _ => true);
        _owned.Add(vm);
        return (vm, rpc, errors);
    }

    private (CasePowerActions Actions, PerKeyRpc Rpc) CasePower(ZoneDto keys)
    {
        var service = DispatchProxy.Create<IRgbService, PerKeyRpc>();
        var rpc = (PerKeyRpc)(object)service;
        rpc.Scene = new([new([Case, Keys, Whole, Held], "Breathing", ["#FFCC00"])]);
        rpc.Controllers = [CaseController, Keyboard(keys), WholeKeyboard, HeldKeyboard];
        var actions = new CasePowerActions(() => service);
        _owned.Add(actions);
        return (actions, rpc);
    }

    private sealed class FrameAdapter(Func<PerKeyFrame> frame) : IDeviceAdapter, IPerKeyFrameSource
    {
        public string Vendor => "Test";
        public DeviceClass Class => DeviceClass.AsusRog;
        public event Action<HotplugEvent>? Hotplug { add { } remove { } }
        public PerKeyFrame? ReadPerKeyFrame(ControllerId id) => frame();
        public Task<IReadOnlyList<ControllerDescriptor>> DiscoverAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<ControllerDescriptor>>([]);
        public Task OpenAsync(ControllerId id, CancellationToken ct) => Task.CompletedTask;
        public void WriteFrame(ControllerId id, ZoneId zone, ReadOnlySpan<Rgb> colors) => throw new InvalidOperationException("no device writes in per-key DTO tests");
        public Task SetFirmwareModeAsync(ControllerId id, FirmwareMode mode, CancellationToken ct) => throw new NotSupportedException();
        public Telemetry? ReadTelemetry(ControllerId id) => null;
        public Task CloseAsync(ControllerId id) => Task.CompletedTask;
    }
}

/// <summary>Records every RPC in order; the controller read can be held (and cancelled) through <see cref="ListOverride"/>.</summary>
public class PerKeyRpc : DispatchProxy
{
    public SceneDto Scene { get; set; } = new([]);
    public IReadOnlyList<ControllerDto> Controllers { get; set; } = [];
    public IReadOnlyList<EffectDto> Effects { get; set; } = [];
    public ProfileDto? Backup { get; set; }
    public List<string> Calls { get; } = [];
    public List<SceneDto> Submitted { get; } = [];
    public Func<CancellationToken, Task<IReadOnlyList<ControllerDto>>>? ListOverride { get; set; }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        var method = targetMethod?.Name ?? throw new InvalidOperationException();
        if (method.StartsWith("add_", StringComparison.Ordinal) || method.StartsWith("remove_", StringComparison.Ordinal)) return null;
        Calls.Add(method);
        var ct = args?.OfType<CancellationToken>().FirstOrDefault() ?? default;
        switch (method)
        {
            case nameof(IRgbService.ListControllersAsync): return ListOverride?.Invoke(ct) ?? Task.FromResult(Controllers);
            case nameof(IRgbService.ListEffectsAsync): return Task.FromResult(Effects);
            case nameof(IRgbService.GetSceneAsync): return Task.FromResult(Scene);
            case nameof(IRgbService.SaveProfileAsync): Backup = (ProfileDto)args![0]!; return Task.CompletedTask;
            case nameof(IRgbService.LoadProfileAsync): return Task.FromResult(Backup ?? throw new FileNotFoundException());
            case nameof(IRgbService.SetSceneAsync):
                var next = (SceneDto)args![0]!;
                Submitted.Add(next);
                Scene = next.Screens is null ? next with { Screens = Scene.Screens } : next;
                return Task.CompletedTask;
            default: throw new InvalidOperationException("Unexpected fake RPC: " + method);
        }
    }
}
