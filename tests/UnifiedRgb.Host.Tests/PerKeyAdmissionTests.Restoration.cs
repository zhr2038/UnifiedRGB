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
using C = UnifiedRgb.Contracts.Dto.PerKeyRestorationContract;
using V = UnifiedRgb.Contracts.PerKeyAdmissionVerdict;

namespace UnifiedRgb.Host.Tests;

/// <summary>
/// Revision 3: the perkey-restoration-v1 capability must already be reported complete (effect, inactive static bank, brightness,
/// profile, RF scope) for the same transport and a stated scope before any per-key output request. Every gap denies before a
/// request, with zero sends, and leaves aggregate output, the draft, the running scene and brightness as they are. These are fake
/// reports only: nothing here is a restore command or a key map, or evidence that any keyboard can restore its lighting.
/// </summary>
public sealed partial class PerKeyAdmissionTests
{
    private const string SyntheticScope = "synthetic device/firmware/profile/RF scope (fake)";
    private const string AllFive = "（待证实：原始效果、非活动静态色库、亮度、配置文件、2.4G 范围）";
    private const string Mismatch = "原始灯光恢复能力与当前传输、版本或设备范围不一致";
    private static readonly JsonSerializerOptions WebOptions = new(JsonSerializerDefaults.Web);

    // Each case edits the complete Ready status of PerKeyZone(ReadyMap): the expected verdict and a fragment of the Chinese text.
    private static readonly Dictionary<string, (Func<PerKeyStatusDto, PerKeyStatusDto> Edit, V Verdict, string Text)> Reports = new()
    {
        ["complete"] = (s => s, V.Admitted, "逐键已准许 · 2 键（实机效果未验收）"),
        ["verified-word-without-capability"] = (s => s with { RestorationCapability = null }, V.RestorationIncomplete, AllFive),
        ["missing-effect"] = (Without(C.Effect), V.RestorationIncomplete, "（待证实：原始效果）"),
        ["missing-inactive-static-bank"] = (Without(C.InactiveStaticBank), V.RestorationIncomplete, "（待证实：非活动静态色库）"),
        ["missing-brightness"] = (Without(C.Brightness), V.RestorationIncomplete, "（待证实：亮度）"),
        ["missing-profile"] = (Without(C.Profile), V.RestorationIncomplete, "（待证实：配置文件）"),
        ["missing-rf-scope"] = (Without(C.RfScope), V.RestorationIncomplete, "（待证实：2.4G 范围）"),
        ["no-components"] = (EditComponents(_ => []), V.RestorationIncomplete, AllFive),
        ["components-omitted-on-the-wire"] = (WithCapability(c => c with { Components = null! }), V.RestorationIncomplete, AllFive),
        ["profile-unproven"] = (Marked(C.Profile, PerKeyRestorationStates.Unproven), V.RestorationIncomplete, "（待证实：配置文件）"),
        ["brightness-word-lowercase"] = (Marked(C.Brightness, "verified"), V.RestorationIncomplete, "（待证实：亮度）"),
        ["rf-scope-reported-twice"] = (EditComponents(c => c.Append(c.Single(x => x.Component == C.RfScope))), V.RestorationIncomplete, "（待证实：2.4G 范围）"),
        ["capability-not-verified"] = (WithCapability(c => c with { State = PerKeyRestorationStates.Unproven }), V.RestorationIncomplete, "（待证实：恢复能力整体状态）"),
        ["capability-failed"] = (WithCapability(c => c with { State = PerKeyRestorationStates.Failed }), V.RestorationFailed, "原始灯光快照或恢复预检失败"),
        ["effect-failed"] = (Marked(C.Effect, PerKeyRestorationStates.Failed), V.RestorationFailed, "原始灯光快照或恢复预检失败"),
        ["status-word-unproven"] = (s => s with { Restoration = PerKeyRestorationStates.Unproven }, V.RestorationUnproven, "映射门控已就绪，但原始灯光完整恢复未证实"),
        ["status-word-failed"] = (s => s with { Restoration = PerKeyRestorationStates.Failed }, V.RestorationFailed, "原始灯光快照或恢复预检失败"),
        ["other-contract-version"] = (WithCapability(c => c with { Contract = "perkey-restoration-v2" }), V.RestorationMismatch, Mismatch),
        ["contract-case-differs"] = (WithCapability(c => c with { Contract = "PERKEY-RESTORATION-V1" }), V.RestorationMismatch, Mismatch),
        ["other-transport"] = (WithCapability(c => c with { Transport = "rog-wired-1c24-v1" }), V.RestorationMismatch, Mismatch),
        ["capability-transport-missing"] = (WithCapability(c => c with { Transport = null }), V.RestorationMismatch, Mismatch),
        ["status-transport-missing"] = (s => s with { Transport = null, RestorationCapability = s.RestorationCapability! with { Transport = null } }, V.RestorationMismatch, Mismatch),
        ["no-scope"] = (WithCapability(c => c with { Scope = " " }), V.RestorationMismatch, Mismatch),
        ["unknown-component"] = (EditComponents(c => c.Append(new("Macro", PerKeyRestorationStates.Verified))), V.RestorationMismatch, Mismatch),
        ["null-component-entry"] = (EditComponents(c => c.Append(null!)), V.RestorationMismatch, Mismatch),
        ["map-not-ready"] = (s => s with { State = nameof(PerKeyState.KeyOrderUnverified) }, V.CapabilityBlocked, "逐键不可用 · 键位顺序待校验"),
    };

    public static TheoryData<string> ReportNames
    {
        get
        {
            var names = new TheoryData<string>();
            foreach (var name in Reports.Keys) names.Add(name);
            return names;
        }
    }

    [Theory]
    [MemberData(nameof(ReportNames))]
    public void Restoration_capability_admits_only_a_complete_v1_report_for_the_same_transport_and_a_stated_scope(string report)
    {
        var (edit, verdict, text) = Reports[report];
        var zone = Edited(Complete(PerKeyZone(ReadyMap)), edit);

        var result = PerKeyAdmission.Evaluate(zone);

        result.Verdict.Should().Be(verdict);
        result.AllowsOutput.Should().Be(verdict == V.Admitted);
        var label = PerKeyDisplay.Zone(zone);
        label.Should().Contain(text);
        if (verdict != V.Admitted)
            label.Should().NotContain("已准许").And.Contain(verdict == V.CapabilityBlocked ? "逐键不可用" : "不发送逐键输出");
        PerKeyAdmission.Blocked([Keys], [Keyboard(zone)]).Should().HaveCount(verdict == V.Admitted ? 0 : 1);

        // The same report on the aggregate whole-keyboard zone never blocks its single colour; only a complete one reads as held.
        var aggregate = AggregateZoneDto(ReadyMap) with { PerKey = zone.PerKey };
        PerKeyAdmission.Evaluate(aggregate).Verdict.Should().Be(V.NotPerKey);
        PerKeyDisplay.Zone(aggregate).Should().Be(verdict == V.Admitted ? "逐键未启用 · 当前分区仍为整键盘单色" : label);
    }

    [Fact]
    public void Restoration_contract_words_and_verdict_values_are_fixed_and_reasons_name_what_is_missing()
    {
        C.Version.Should().Be("perkey-restoration-v1");
        C.Required.Should().Equal("Effect", "InactiveStaticBank", "Brightness", "Profile", "RfScope");
        // Verdicts appended after Admitted: every earlier value is unchanged.
        Enum.GetValues<V>().Select(v => ((int)v, v.ToString())).Should().Equal((0, "NotPerKey"), (1, "CapabilityBlocked"),
            (2, "RestorationUnproven"), (3, "RestorationFailed"), (4, "Admitted"), (5, "RestorationIncomplete"), (6, "RestorationMismatch"));

        var complete = Complete(PerKeyZone(ReadyMap)).PerKey!;
        PerKeyAdmission.UnprovenComponents(complete).Should().BeEmpty();
        PerKeyAdmission.EvaluateStatus(complete).Reason.Should().Contain("perkey-restoration-v1").And.Contain("rog-rf64-c081-v1")
            .And.Contain(SyntheticScope).And.Contain("optical output is not certified");

        var bare = complete with { RestorationCapability = null };
        PerKeyAdmission.UnprovenComponents(bare).Should().Equal(C.Required);
        PerKeyAdmission.EvaluateStatus(bare).Reason.Should()
            .Be("Restoration is reported Verified without the itemised perkey-restoration-v1 capability; Effect, InactiveStaticBank, Brightness, Profile, RfScope are not proven.");
        PerKeyAdmission.EvaluateStatus(Without(C.Brightness)(Without(C.Effect)(complete))).Reason.Should()
            .Be("Original-lighting restoration is incomplete: Effect, Brightness not verified.");
        PerKeyAdmission.EvaluateStatus(Marked(C.Profile, PerKeyRestorationStates.Failed)(complete)).Reason.Should()
            .Be("The restoration capability reports a failure (Profile): synthetic snapshot and restore preflight");

        // A Failed word anywhere wins over every other gap, including an unknown contract and an unproven status word.
        var failedElsewhere = Marked(C.Effect, PerKeyRestorationStates.Failed)(WithCapability(c => c with { Contract = "perkey-restoration-v2" })(complete));
        PerKeyAdmission.EvaluateStatus(failedElsewhere).Verdict.Should().Be(V.RestorationFailed);
        PerKeyAdmission.EvaluateStatus(failedElsewhere with { Restoration = PerKeyRestorationStates.Unproven }).Verdict.Should().Be(V.RestorationFailed);
        PerKeyAdmission.EvaluateStatus(complete with { Restoration = PerKeyRestorationStates.Failed, RestorationReason = "snapshot read failed" })
            .Should().Be(new PerKeyAdmissionResult(V.RestorationFailed, "snapshot read failed"));
        // A complete capability does not lift an unproven status word, and the older default text is unchanged.
        PerKeyAdmission.EvaluateStatus(complete with { Restoration = PerKeyRestorationStates.Unproven })
            .Should().Be(new PerKeyAdmissionResult(V.RestorationUnproven, "Complete original-lighting restoration is not proven."));
        PerKeyDisplay.Component("Macro").Should().Be("Macro");
    }

    [Fact]
    public void Restoration_capability_is_additive_json_that_round_trips_and_the_current_host_never_reports_it()
    {
        var keyboard = Keyboard(Complete(PerKeyZone(ReadyMap)));
        var json = JsonSerializer.Serialize(keyboard, Options);
        var status = JsonNode.Parse(json)!["zones"]![0]!["perKey"]!.AsObject();
        var capability = status["restorationCapability"]!.AsObject();
        capability.Select(p => p.Key).Should().Equal("contract", "state", "reason", "transport", "scope", "components");
        capability["contract"]!.GetValue<string>().Should().Be("perkey-restoration-v1");
        capability["components"]!.AsArray().Select(c => c!["component"]!.GetValue<string>()).Should().Equal(C.Required);
        var read = JsonSerializer.Deserialize<ControllerDto>(json, Options)!;
        JsonSerializer.Serialize(read, Options).Should().Be(json);
        PerKeyAdmission.Evaluate(read.Zones.Single()).Verdict.Should().Be(V.Admitted);

        // An older App reads the same object and skips the unknown member; a Host must therefore say Verified only with a
        // complete capability (INTERFACE-PROPOSAL PK-RESTORE-1).
        var older = status.Deserialize<OlderPerKeyStatus>(WebOptions)!;
        older.Should().Be(new OlderPerKeyStatus("Ready", ReadyMap.Reason, "rog-rf64-c081-v1", 2, 128, "Verified", null));

        // An older Host (or a report that loses the object) carrying the Verified word alone is incomplete, not admitted.
        status.Remove("restorationCapability").Should().BeTrue();
        var bare = JsonSerializer.Deserialize<ControllerDto>(status.Root.ToJsonString(), Options)!.Zones.Single();
        bare.PerKey!.RestorationCapability.Should().BeNull();
        PerKeyAdmission.Evaluate(bare).Verdict.Should().Be(V.RestorationIncomplete);

        var lost = JsonNode.Parse(json)!;
        lost["zones"]![0]!["perKey"]!["restorationCapability"]!.AsObject().Remove("components").Should().BeTrue();
        var partial = JsonSerializer.Deserialize<ControllerDto>(lost.ToJsonString(), Options)!.Zones.Single();
        PerKeyAdmission.Evaluate(partial).Verdict.Should().Be(V.RestorationIncomplete);
        PerKeyDisplay.Zone(partial).Should().Contain(AllFive);

        // The current Host maps no restoration capability for any state, so nothing it reports can be admitted.
        foreach (var state in Enum.GetValues<PerKeyState>())
        {
            var mapped = PerKeyZone(new PerKeyCapability(state, "fake", "rog-rf64-c081-v1", 2, 128));
            mapped.PerKey!.RestorationCapability.Should().BeNull();
            JsonSerializer.Serialize(mapped, Options).Should().NotContain("restorationCapability");
            PerKeyAdmission.Evaluate(mapped).AllowsOutput.Should().BeFalse();
        }
    }

    [Fact]
    public async Task Studio_refuses_a_verified_word_or_an_incomplete_capability_before_reading_the_scene_and_sends_once_when_complete()
    {
        var (vm, rpc, errors) = Studio(Keyboard(Restoration(PerKeyZone(ReadyMap), PerKeyRestorationStates.Verified)));
        await vm.RefreshAsync();
        vm.Zones.Single(z => z.Ref == Keys).Detail.Should().Contain(AllFive);
        vm.Draft.Brightness = 70;
        var running = rpc.Scene;
        rpc.Calls.Clear();

        await vm.ApplyCommand.ExecuteAsync();

        errors.Should().ContainSingle().Which.Should().BeOfType<InvalidOperationException>()
            .Which.Message.Should().StartWith("逐键输出未准许，未发送任何请求").And.Contain(AllFive);
        rpc.Calls.Should().Equal(ListCall);
        rpc.Submitted.Should().BeEmpty();
        rpc.Scene.Should().BeSameAs(running);
        (vm.Draft.Dirty, vm.Draft.Brightness).Should().Be((true, 70));

        rpc.Controllers = [CaseController, Keyboard(Edited(Complete(PerKeyZone(ReadyMap)), Without(C.InactiveStaticBank)))];
        rpc.Calls.Clear();
        await vm.ApplyCommand.ExecuteAsync();
        errors.Should().HaveCount(2);
        errors[1].Message.Should().Contain("（待证实：非活动静态色库）");
        rpc.Calls.Should().Equal(ListCall);
        rpc.Submitted.Should().BeEmpty();
        rpc.Scene.Should().BeSameAs(running);

        rpc.Controllers = [CaseController, Keyboard(Complete(PerKeyZone(ReadyMap)))];
        rpc.Calls.Clear();
        await vm.ApplyCommand.ExecuteAsync();
        errors.Should().HaveCount(2);
        rpc.Calls.Take(3).Should().Equal(ListCall, GetCall, SetCall);
        rpc.Submitted.Should().ContainSingle().Which.Assignments.Should().ContainSingle().Which.Brightness.Should().BeApproximately(.7f, .001f);
    }

    [Fact]
    public async Task Studio_checks_the_fresh_controller_read_so_a_capability_withdrawn_after_refresh_still_sends_nothing()
    {
        var (vm, rpc, errors) = Studio(Keyboard(Complete(PerKeyZone(ReadyMap))));
        await vm.RefreshAsync();
        vm.Zones.Single(z => z.Ref == Keys).Detail.Should().Contain("逐键已准许");
        vm.Draft.Brightness = 55;
        var running = rpc.Scene;
        // The Host withdraws the capability (for example a fresh preflight found the profile changed) before the click.
        rpc.Controllers = [CaseController, Keyboard(Edited(Complete(PerKeyZone(ReadyMap)), Marked(C.Profile, PerKeyRestorationStates.Unproven)))];
        rpc.Calls.Clear();

        await vm.ApplyCommand.ExecuteAsync();

        errors.Should().ContainSingle().Which.Message.Should().Contain("（待证实：配置文件）");
        rpc.Calls.Should().Equal(ListCall);
        rpc.Submitted.Should().BeEmpty();
        rpc.Scene.Should().BeSameAs(running);
        (vm.Draft.Dirty, vm.Draft.Brightness).Should().Be((true, 55));
    }

    [Theory]
    [InlineData("verified-word-without-capability")]
    [InlineData("capability-failed")]
    [InlineData("other-transport")]
    [InlineData("complete")]
    public async Task Aggregate_whole_keyboard_colour_is_sent_unchanged_whatever_the_restoration_report(string report)
    {
        var zone = AggregateZoneDto(ReadyMap);
        var (vm, rpc, errors) = Studio(WholeKeyboard with { Id = KeyboardId, Zones = [Edited(Complete(zone), Reports[report].Edit)] });
        await vm.RefreshAsync();
        vm.Draft.Brightness = 40;
        rpc.Calls.Clear();

        await vm.ApplyCommand.ExecuteAsync();

        errors.Should().BeEmpty();
        rpc.Calls.Take(3).Should().Equal(ListCall, GetCall, SetCall);
        var sent = rpc.Submitted.Should().ContainSingle().Which.Assignments.Should().ContainSingle().Subject;
        sent.Targets.Should().Equal(Keys);
        sent.Brightness.Should().BeApproximately(.4f, .001f);
    }

    [Fact]
    public async Task Saved_scene_and_legacy_pages_deny_an_incomplete_capability_before_any_request()
    {
        var incomplete = Keyboard(Edited(Complete(PerKeyZone(ReadyMap)), Without(C.RfScope)));
        var service = DispatchProxy.Create<IRgbService, PerKeyRpc>();
        var rpc = (PerKeyRpc)(object)service;
        rpc.Controllers = [CaseController, incomplete, HeldKeyboard];
        rpc.Backup = new("Evening", new([new([Case, Keys], "Breathing", ["#FFCC00"])]));

        (await FluentActions.Awaiting(() => PerKeyDisplay.RequireProfileAdmittedAsync(service, "Evening"))
            .Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("（待证实：2.4G 范围）");
        rpc.Calls.Should().Equal(LoadCall, ListCall);
        rpc.Submitted.Should().BeEmpty();

        await using var client = new PipeClient("UnifiedRgb-unused-" + Guid.NewGuid().ToString("N"));
        var errors = new List<Exception>();
        using var fx = new EffectsViewModel(client, InlineDispatcher.Instance, errors.Add) { LiveApply = false };
        fx.Effects.Add(Static);
        fx.SelectedEffect = Static;
        fx.SetControllers([CaseController, incomplete]);
        foreach (var z in fx.Zones) z.IsSelected = true;
        await fx.ApplyCommand.ExecuteAsync();
        // Refused before the (absent) Host connection is even required.
        errors.Should().ContainSingle().Which.Should().BeOfType<InvalidOperationException>().Which.Message.Should().Contain("（待证实：2.4G 范围）");
        fx.Zones.Should().HaveCount(2).And.OnlyContain(z => z.IsSelected);

        var scene = new SceneViewModel(client, InlineDispatcher.Instance, errors.Add);
        scene.Show([incomplete], new([]));
        scene.Candidate = scene.AvailableZones.Single();
        scene.AddToGroupCommand.Execute(null);
        await scene.ApplyGroupCommand.ExecuteAsync();
        errors.Should().HaveCount(2);
        errors[1].Should().BeOfType<InvalidOperationException>().Which.Message.Should().Contain("逐键输出未准许");
        scene.GroupZones.Should().ContainSingle();
    }

    [Fact]
    public async Task Lights_off_restore_and_turn_on_deny_an_incomplete_capability_before_any_write_and_keep_aggregate_output()
    {
        var (actions, rpc) = CasePower();
        var incomplete = Keyboard(Edited(Complete(PerKeyZone(ReadyMap)), Marked(C.Effect, PerKeyRestorationStates.Unproven)));
        rpc.Controllers = [CaseController, incomplete, WholeKeyboard, HeldKeyboard];
        var running = rpc.Scene;

        var off = await actions.OffAsync();

        off.Message.Should().EndWith("1 个逐键分区未准许逐键输出，未包含在熄灯指令中，保持原状。");
        rpc.Submitted.Should().ContainSingle().Which.Assignments.Should().ContainSingle().Which.Targets.Should().Equal(Case, Whole, Held);
        rpc.Backup!.Scene.Assignments.Should().BeSameAs(running.Assignments);
        var recovery = rpc.Backup;

        // The recovery point names the per-key zone, so restoring it (explicitly or as the 'on' intent) is refused before a write.
        rpc.Calls.Clear();
        (await actions.Awaiting(a => a.RestoreBackupAsync()).Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().StartWith("逐键输出未准许，未发送任何请求").And.Contain("（待证实：原始效果）");
        rpc.Calls.Should().Equal(LoadCall, ListCall);
        rpc.Calls.Clear();
        await actions.Awaiting(a => a.EnsureOnAsync()).Should().ThrowAsync<InvalidOperationException>().WithMessage("逐键输出未准许*");
        rpc.Calls.Should().Equal(GetCall, LoadCall, ListCall);
        rpc.Submitted.Should().ContainSingle("only the aggregate lights-off was sent");
        rpc.Backup.Should().BeSameAs(recovery);
        actions.Status.State.Should().Be(CasePowerRequestState.Failed);

        // Once the fake Host reports the complete capability, the kept recovery point is restored as a whole.
        rpc.Controllers = [CaseController, Keyboard(Complete(PerKeyZone(ReadyMap))), WholeKeyboard, HeldKeyboard];
        (await actions.EnsureOnAsync()).Changed.Should().BeTrue();
        rpc.Submitted.Should().HaveCount(2).And.Subject.Last().Assignments.Should().Equal(running.Assignments);

        // With the complete capability, black on the per-key zone is admitted output and lights-off includes it.
        var (admitted, all) = CasePower();
        all.Controllers = [CaseController, Keyboard(Complete(PerKeyZone(ReadyMap))), WholeKeyboard, HeldKeyboard];
        (await admitted.OffAsync()).Message.Should().NotContain("逐键分区未准许");
        all.Submitted.Should().ContainSingle().Which.Assignments.Single().Targets.Should().Equal(Case, Keys, Whole, Held);
    }

    private static ZoneDto Edited(ZoneDto zone, Func<PerKeyStatusDto, PerKeyStatusDto> edit) => zone with { PerKey = edit(zone.PerKey!) };

    private static Func<PerKeyStatusDto, PerKeyStatusDto> WithCapability(Func<PerKeyRestorationCapabilityDto, PerKeyRestorationCapabilityDto> edit) =>
        s => s with { RestorationCapability = edit(s.RestorationCapability!) };

    private static Func<PerKeyStatusDto, PerKeyStatusDto> EditComponents(
        Func<IReadOnlyList<PerKeyRestorationComponentDto>, IEnumerable<PerKeyRestorationComponentDto>> edit) =>
        WithCapability(c => c with { Components = [.. edit(c.Components)] });

    private static Func<PerKeyStatusDto, PerKeyStatusDto> Without(string component) =>
        EditComponents(c => c.Where(x => x.Component != component));

    private static Func<PerKeyStatusDto, PerKeyStatusDto> Marked(string component, string state) =>
        EditComponents(c => c.Select(x => x.Component == component ? x with { State = state } : x));

    /// <summary>The per-key status shape an App from before revision 3 deserializes.</summary>
    private sealed record OlderPerKeyStatus(string State, string Reason, string? Transport, int KeyCount, int MaximumKeys,
        string Restoration, string? RestorationReason);
}
