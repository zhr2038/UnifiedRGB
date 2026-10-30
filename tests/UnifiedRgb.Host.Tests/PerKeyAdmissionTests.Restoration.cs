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
/// Restoration reports are diagnostics: the perkey-restoration-v1 capability, its components, contract, transport and scope never
/// authorise or refuse a Ready map. Only an actual Failed word (status, capability or a component) refuses, before any request,
/// with zero sends, leaving aggregate output, the draft, the running scene and brightness as they are. These are fake reports
/// only: nothing here is a restore command or a key map, or evidence that any keyboard can restore its lighting.
/// </summary>
public sealed partial class PerKeyAdmissionTests
{
    private const string SyntheticScope = "synthetic device/firmware/profile/RF scope (fake)";
    private const string RestoreFailed = "原始灯光恢复失败";
    private static readonly JsonSerializerOptions WebOptions = new(JsonSerializerDefaults.Web);

    // Each case edits the complete Ready status of PerKeyZone(ReadyMap): the expected verdict and a fragment of the Chinese text.
    private static readonly Dictionary<string, (Func<PerKeyStatusDto, PerKeyStatusDto> Edit, V Verdict, string Text)> Reports = new()
    {
        ["complete"] = (s => s, V.Admitted, ReadyLabel),
        ["verified-word-without-capability"] = (s => s with { RestorationCapability = null }, V.Admitted, ReadyLabel),
        ["missing-effect"] = (Without(C.Effect), V.Admitted, ReadyLabel),
        ["missing-inactive-static-bank"] = (Without(C.InactiveStaticBank), V.Admitted, ReadyLabel),
        ["missing-brightness"] = (Without(C.Brightness), V.Admitted, ReadyLabel),
        ["missing-profile"] = (Without(C.Profile), V.Admitted, ReadyLabel),
        ["missing-rf-scope"] = (Without(C.RfScope), V.Admitted, ReadyLabel),
        ["no-components"] = (EditComponents(_ => []), V.Admitted, ReadyLabel),
        ["components-omitted-on-the-wire"] = (WithCapability(c => c with { Components = null! }), V.Admitted, ReadyLabel),
        ["profile-unproven"] = (Marked(C.Profile, PerKeyRestorationStates.Unproven), V.Admitted, ReadyLabel),
        ["brightness-word-lowercase"] = (Marked(C.Brightness, "verified"), V.Admitted, ReadyLabel),
        ["rf-scope-reported-twice"] = (EditComponents(c => c.Append(c.Single(x => x.Component == C.RfScope))), V.Admitted, ReadyLabel),
        ["capability-not-verified"] = (WithCapability(c => c with { State = PerKeyRestorationStates.Unproven }), V.Admitted, ReadyLabel),
        ["capability-failed"] = (WithCapability(c => c with { State = PerKeyRestorationStates.Failed }), V.RestorationFailed, RestoreFailed),
        ["effect-failed"] = (Marked(C.Effect, PerKeyRestorationStates.Failed), V.RestorationFailed, RestoreFailed),
        ["status-word-unproven"] = (s => s with { Restoration = PerKeyRestorationStates.Unproven }, V.Admitted, ReadyLabel),
        ["status-word-failed"] = (s => s with { Restoration = PerKeyRestorationStates.Failed }, V.RestorationFailed, RestoreFailed),
        ["other-contract-version"] = (WithCapability(c => c with { Contract = "perkey-restoration-v2" }), V.Admitted, ReadyLabel),
        ["contract-case-differs"] = (WithCapability(c => c with { Contract = "PERKEY-RESTORATION-V1" }), V.Admitted, ReadyLabel),
        ["other-transport"] = (WithCapability(c => c with { Transport = "rog-wired-1c24-v1" }), V.Admitted, ReadyLabel),
        ["capability-transport-missing"] = (WithCapability(c => c with { Transport = null }), V.Admitted, ReadyLabel),
        ["status-transport-missing"] = (s => s with { Transport = null, RestorationCapability = s.RestorationCapability! with { Transport = null } }, V.Admitted, ReadyLabel),
        ["no-scope"] = (WithCapability(c => c with { Scope = " " }), V.Admitted, ReadyLabel),
        ["unknown-component"] = (EditComponents(c => c.Append(new("Macro", PerKeyRestorationStates.Verified))), V.Admitted, ReadyLabel),
        ["null-component-entry"] = (EditComponents(c => c.Append(null!)), V.Admitted, ReadyLabel),
        ["map-not-ready"] = (s => s with { State = nameof(PerKeyState.KeyOrderUnverified) }, V.CapabilityBlocked, "逐键需校准 · 键位顺序待校验"),
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
    public void Restoration_diagnostics_never_gate_a_ready_map_and_only_an_actual_failure_refuses_it(string report)
    {
        var (edit, verdict, text) = Reports[report];
        var zone = Edited(Complete(PerKeyZone(ReadyMap)), edit);

        var result = PerKeyAdmission.Evaluate(zone);

        result.Verdict.Should().Be(verdict);
        result.AllowsOutput.Should().Be(verdict == V.Admitted);
        var label = PerKeyDisplay.Zone(zone);
        label.Should().Contain(text);
        if (verdict != V.Admitted)
            label.Should().NotContain("逐键可用").And.Contain(verdict == V.CapabilityBlocked ? "逐键需校准" : "不发送逐键输出");
        PerKeyAdmission.Blocked([Keys], [Keyboard(zone)]).Should().HaveCount(verdict == V.Admitted ? 0 : 1);

        // The same report on the aggregate whole-keyboard zone never blocks its single colour.
        var aggregate = AggregateZoneDto(ReadyMap) with { PerKey = zone.PerKey };
        PerKeyAdmission.Evaluate(aggregate).Verdict.Should().Be(V.NotPerKey);
        PerKeyDisplay.Zone(aggregate).Should().Be(verdict == V.Admitted ? HeldLabel : label);
    }

    [Fact]
    public void Restoration_contract_words_and_verdict_values_are_fixed_and_reasons_stay_diagnostic()
    {
        C.Version.Should().Be("perkey-restoration-v1");
        C.Required.Should().Equal("Effect", "InactiveStaticBank", "Brightness", "Profile", "RfScope");
        // Retired verdicts keep their numbers so older wire values and logs read the same.
        Enum.GetValues<V>().Select(v => ((int)v, v.ToString())).Should().Equal((0, "NotPerKey"), (1, "CapabilityBlocked"),
            (2, "RestorationUnproven"), (3, "RestorationFailed"), (4, "Admitted"), (5, "RestorationIncomplete"), (6, "RestorationMismatch"));

        var complete = Complete(PerKeyZone(ReadyMap)).PerKey!;
        PerKeyAdmission.UnprovenComponents(complete).Should().BeEmpty();
        PerKeyAdmission.EvaluateStatus(complete).Should().Be(new PerKeyAdmissionResult(V.Admitted,
            "Per-key map is Ready for rog-rf64-c081-v1 (2 keys); restoration diagnostics report Verified; optical output is not certified."));

        // Unproven components are still listed for diagnostics, and never refuse.
        var bare = complete with { RestorationCapability = null };
        PerKeyAdmission.UnprovenComponents(bare).Should().Equal(C.Required);
        PerKeyAdmission.EvaluateStatus(bare).Verdict.Should().Be(V.Admitted);
        var partial = Without(C.Brightness)(Without(C.Effect)(complete));
        PerKeyAdmission.UnprovenComponents(partial).Should().Equal(C.Effect, C.Brightness);
        PerKeyAdmission.EvaluateStatus(partial).Verdict.Should().Be(V.Admitted);
        PerKeyAdmission.EvaluateStatus(complete with { Restoration = PerKeyRestorationStates.Unproven }).Reason.Should()
            .Contain("restoration diagnostics report Unproven (informational only)");
        PerKeyAdmission.EvaluateStatus(complete with { Restoration = "" }).Reason.Should().Contain("restoration diagnostics report nothing (informational only)");

        // An actual failure anywhere refuses, whatever else the report says; readiness is still decided first.
        PerKeyAdmission.EvaluateStatus(Marked(C.Profile, PerKeyRestorationStates.Failed)(complete)).Should().Be(new PerKeyAdmissionResult(V.RestorationFailed,
            "The restoration capability reports a failure (Profile): synthetic snapshot and restore preflight"));
        PerKeyAdmission.EvaluateStatus(WithCapability(c => c with { State = PerKeyRestorationStates.Failed })(complete)).Reason.Should()
            .Be("The restoration capability reports a failure: synthetic snapshot and restore preflight");
        var failedElsewhere = Marked(C.Effect, PerKeyRestorationStates.Failed)(WithCapability(c => c with { Contract = "perkey-restoration-v2" })(complete));
        PerKeyAdmission.EvaluateStatus(failedElsewhere).Verdict.Should().Be(V.RestorationFailed);
        PerKeyAdmission.EvaluateStatus(failedElsewhere with { Restoration = PerKeyRestorationStates.Unproven }).Verdict.Should().Be(V.RestorationFailed);
        PerKeyAdmission.EvaluateStatus(complete with { Restoration = PerKeyRestorationStates.Failed, RestorationReason = "snapshot read failed" })
            .Should().Be(new PerKeyAdmissionResult(V.RestorationFailed, "snapshot read failed"));
        PerKeyAdmission.EvaluateStatus(complete with { State = nameof(PerKeyState.MissingMap), Restoration = PerKeyRestorationStates.Failed })
            .Verdict.Should().Be(V.CapabilityBlocked);
        PerKeyDisplay.Component("Macro").Should().Be("Macro");
    }

    [Fact]
    public void Restoration_capability_is_additive_json_that_round_trips_and_its_absence_does_not_refuse_a_ready_map()
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

        // An older App reads the same object and skips the unknown member.
        var older = status.Deserialize<OlderPerKeyStatus>(WebOptions)!;
        older.Should().Be(new OlderPerKeyStatus("Ready", ReadyMap.Reason, "rog-rf64-c081-v1", 2, 128, "Verified", null));

        // A report that carries the Verified word alone, or loses the component list, is still a Ready map.
        status.Remove("restorationCapability").Should().BeTrue();
        var bare = JsonSerializer.Deserialize<ControllerDto>(status.Root.ToJsonString(), Options)!.Zones.Single();
        bare.PerKey!.RestorationCapability.Should().BeNull();
        PerKeyAdmission.Evaluate(bare).Verdict.Should().Be(V.Admitted);

        var lost = JsonNode.Parse(json)!;
        lost["zones"]![0]!["perKey"]!["restorationCapability"]!.AsObject().Remove("components").Should().BeTrue();
        var partial = JsonSerializer.Deserialize<ControllerDto>(lost.ToJsonString(), Options)!.Zones.Single();
        PerKeyAdmission.Evaluate(partial).Verdict.Should().Be(V.Admitted);
        PerKeyDisplay.Zone(partial).Should().Be(ReadyLabel);

        // The current Host maps no restoration capability for any state: its Ready map is admitted, every other state is not.
        foreach (var state in Enum.GetValues<PerKeyState>())
        {
            var mapped = PerKeyZone(new PerKeyCapability(state, "fake", "rog-rf64-c081-v1", 2, 128));
            mapped.PerKey!.RestorationCapability.Should().BeNull();
            JsonSerializer.Serialize(mapped, Options).Should().NotContain("restorationCapability");
            PerKeyAdmission.Evaluate(mapped).AllowsOutput.Should().Be(state == PerKeyState.Ready, "state {0}", state);
        }
    }

    [Fact]
    public async Task Studio_refuses_an_actual_restore_failure_before_reading_the_scene_and_sends_once_with_incomplete_diagnostics()
    {
        var (vm, rpc, errors) = Studio(Keyboard(Edited(Complete(PerKeyZone(ReadyMap)), Marked(C.Effect, PerKeyRestorationStates.Failed))));
        await vm.RefreshAsync();
        vm.Zones.Single(z => z.Ref == Keys).Detail.Should().Contain(RestoreFailed);
        vm.Draft.Brightness = 70;
        var running = rpc.Scene;
        rpc.Calls.Clear();

        await vm.ApplyCommand.ExecuteAsync();

        errors.Should().ContainSingle().Which.Should().BeOfType<InvalidOperationException>()
            .Which.Message.Should().StartWith("逐键输出未准许，未发送任何请求").And.Contain(RestoreFailed);
        rpc.Calls.Should().Equal(ListCall);
        rpc.Submitted.Should().BeEmpty();
        rpc.Scene.Should().BeSameAs(running);
        (vm.Draft.Dirty, vm.Draft.Brightness).Should().Be((true, 70));

        // A capability that only lacks a component is diagnostics, not a failure: the same draft is sent once.
        rpc.Controllers = [CaseController, Keyboard(Edited(Complete(PerKeyZone(ReadyMap)), Without(C.InactiveStaticBank)))];
        rpc.Calls.Clear();
        await vm.ApplyCommand.ExecuteAsync();
        errors.Should().ContainSingle();
        rpc.Calls.Take(3).Should().Equal(ListCall, GetCall, SetCall);
        rpc.Submitted.Should().ContainSingle().Which.Assignments.Should().ContainSingle().Which.Brightness.Should().BeApproximately(.7f, .001f);
    }

    [Fact]
    public async Task Studio_checks_the_fresh_controller_read_so_a_failure_reported_after_refresh_still_sends_nothing()
    {
        var (vm, rpc, errors) = Studio(Keyboard(PerKeyZone(ReadyMap)));
        await vm.RefreshAsync();
        vm.Zones.Single(z => z.Ref == Keys).Detail.Should().Contain(ReadyLabel);
        vm.Draft.Brightness = 55;
        var running = rpc.Scene;
        // The Host reports an actual restore failure (for example a fresh preflight could not read the profile) before the click.
        rpc.Controllers = [CaseController, Keyboard(Edited(Complete(PerKeyZone(ReadyMap)), Marked(C.Profile, PerKeyRestorationStates.Failed)))];
        rpc.Calls.Clear();

        await vm.ApplyCommand.ExecuteAsync();

        errors.Should().ContainSingle().Which.Message.Should().Contain(RestoreFailed);
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
    public async Task Saved_scene_and_legacy_pages_deny_an_actual_failure_before_any_request_and_pass_incomplete_diagnostics_on()
    {
        var failed = Keyboard(Edited(Complete(PerKeyZone(ReadyMap)), WithCapability(c => c with { State = PerKeyRestorationStates.Failed })));
        var incomplete = Keyboard(Edited(Complete(PerKeyZone(ReadyMap)), Without(C.RfScope)));
        var service = DispatchProxy.Create<IRgbService, PerKeyRpc>();
        var rpc = (PerKeyRpc)(object)service;
        rpc.Controllers = [CaseController, failed, HeldKeyboard];
        rpc.Backup = new("Evening", new([new([Case, Keys], "Breathing", ["#FFCC00"])]));

        (await FluentActions.Awaiting(() => PerKeyDisplay.RequireProfileAdmittedAsync(service, "Evening"))
            .Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain(RestoreFailed);
        rpc.Calls.Should().Equal(LoadCall, ListCall);
        rpc.Controllers = [CaseController, incomplete, HeldKeyboard];
        await PerKeyDisplay.RequireProfileAdmittedAsync(service, "Evening");
        rpc.Calls.Should().Equal(LoadCall, ListCall, LoadCall, ListCall);
        rpc.Submitted.Should().BeEmpty();

        await using var client = new PipeClient("UnifiedRgb-unused-" + Guid.NewGuid().ToString("N"));
        var errors = new List<Exception>();
        using var fx = new EffectsViewModel(client, InlineDispatcher.Instance, errors.Add) { LiveApply = false };
        fx.Effects.Add(Static);
        fx.SelectedEffect = Static;
        fx.SetControllers([CaseController, failed]);
        foreach (var z in fx.Zones) z.IsSelected = true;
        await fx.ApplyCommand.ExecuteAsync();
        // Refused before the (absent) Host connection is even required.
        errors.Should().ContainSingle().Which.Should().BeOfType<InvalidOperationException>().Which.Message.Should().Contain(RestoreFailed);
        fx.Zones.Should().HaveCount(2).And.OnlyContain(z => z.IsSelected);
        // Incomplete diagnostics are not refused: the request goes on to the (absent) Host connection.
        fx.SetControllers([CaseController, incomplete]);
        foreach (var z in fx.Zones) z.IsSelected = true;
        await fx.ApplyCommand.ExecuteAsync();
        errors.Should().HaveCount(2);
        errors[1].Should().BeOfType<HostNotConnectedException>();

        var scene = new SceneViewModel(client, InlineDispatcher.Instance, errors.Add);
        scene.Show([failed], new([]));
        scene.Candidate = scene.AvailableZones.Single();
        scene.AddToGroupCommand.Execute(null);
        await scene.ApplyGroupCommand.ExecuteAsync();
        errors.Should().HaveCount(3);
        errors[2].Should().BeOfType<InvalidOperationException>().Which.Message.Should().Contain("逐键输出未准许");
        scene.GroupZones.Should().ContainSingle();
    }

    [Fact]
    public async Task Lights_off_restore_and_turn_on_deny_an_actual_failure_before_any_write_and_admit_unproven_diagnostics()
    {
        var (actions, rpc) = CasePower(PerKeyZone(ReadyMap));
        var failed = Keyboard(Edited(Complete(PerKeyZone(ReadyMap)), Marked(C.Effect, PerKeyRestorationStates.Failed)));
        rpc.Controllers = [CaseController, failed, WholeKeyboard, HeldKeyboard];
        var running = rpc.Scene;

        var off = await actions.OffAsync();

        off.Message.Should().EndWith("1 个逐键分区未准许逐键输出，未包含在熄灯指令中，保持原状。");
        rpc.Submitted.Should().ContainSingle().Which.Assignments.Should().ContainSingle().Which.Targets.Should().Equal(Case, Whole, Held);
        rpc.Backup!.Scene.Assignments.Should().BeSameAs(running.Assignments);
        var recovery = rpc.Backup;

        // The recovery point names the per-key zone, so restoring it (explicitly or as the 'on' intent) is refused before a write.
        rpc.Calls.Clear();
        (await actions.Awaiting(a => a.RestoreBackupAsync()).Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().StartWith("逐键输出未准许，未发送任何请求").And.Contain(RestoreFailed);
        rpc.Calls.Should().Equal(LoadCall, ListCall);
        rpc.Calls.Clear();
        await actions.Awaiting(a => a.EnsureOnAsync()).Should().ThrowAsync<InvalidOperationException>().WithMessage("逐键输出未准许*");
        rpc.Calls.Should().Equal(GetCall, LoadCall, ListCall);
        rpc.Submitted.Should().ContainSingle("only the aggregate lights-off was sent");
        rpc.Backup.Should().BeSameAs(recovery);
        actions.Status.State.Should().Be(CasePowerRequestState.Failed);

        // Once the fake Host reports the effect only Unproven (a diagnostic, not a failure), the kept point is restored as a whole.
        rpc.Controllers = [CaseController, Keyboard(Edited(Complete(PerKeyZone(ReadyMap)), Marked(C.Effect, PerKeyRestorationStates.Unproven))), WholeKeyboard, HeldKeyboard];
        (await actions.EnsureOnAsync()).Changed.Should().BeTrue();
        rpc.Submitted.Should().HaveCount(2).And.Subject.Last().Assignments.Should().Equal(running.Assignments);
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
