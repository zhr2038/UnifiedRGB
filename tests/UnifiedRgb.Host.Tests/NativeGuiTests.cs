using UnifiedRgb.App.Ipc;
using UnifiedRgb.App.Mvvm;
using UnifiedRgb.App.ViewModels;
using UnifiedRgb.Contracts;
using UnifiedRgb.Contracts.Dto;

namespace UnifiedRgb.Host.Tests;

/// <summary>A pipe connection the test drives: connect/disconnect raise StateChanged like PipeClient does.</summary>
internal sealed class FakeConnection(IRgbService service) : IHostConnection
{
    public event EventHandler<ConnectionState>? StateChanged;

    public ConnectionState State { get; private set; } = ConnectionState.Disconnected;

    public int Connections { get; private set; }

    public IRgbService Require() => State == ConnectionState.Connected ? service : throw new InvalidOperationException("Host not connected.");

    public FakeConnection Connect()
    {
        Connections++;
        State = ConnectionState.Connected;
        StateChanged?.Invoke(this, State);
        return this;
    }

    public void Disconnect()
    {
        State = ConnectionState.Disconnected;
        StateChanged?.Invoke(this, State);
    }
}

/// <summary>Records every call; only the bring-up methods have scripted answers, everything else fails loudly.</summary>
internal sealed class RecordingRgbService : IRgbService
{
    public List<string> Calls { get; } = [];

    public List<MarkerDto> Markers { get; } = [];

    public bool HoldBlack { get; set; }

    public IReadOnlyList<string> Probes { get; set; } = ["fwinfo", "family"];

    public ProbeResultDto? NextProbe { get; set; }

    public Exception? RenewError { get; set; }

    public Exception? EndError { get; set; }

    public TaskCompletionSource<BringupSessionDto>? PendingBegin { get; set; }

    public DateTimeOffset Now { get; set; } = new(2026, 10, 18, 6, 0, 0, TimeSpan.Zero);

    event EventHandler<IReadOnlyList<ControllerDto>>? IRgbService.ControllersChanged
    {
        add { }
        remove { }
    }

    event EventHandler<StatusDto>? IRgbService.StatusChanged
    {
        add { }
        remove { }
    }

    event EventHandler<IReadOnlyList<TelemetryDto>>? IRgbService.TelemetryTick
    {
        add { }
        remove { }
    }

    public Task<BringupSessionDto> BeginBringupAsync(string controllerId, int leaseSeconds, CancellationToken ct = default)
    {
        Calls.Add($"BeginBringup {controllerId} {leaseSeconds}");
        var s = Session(controllerId, leaseSeconds);
        return PendingBegin?.Task ?? Task.FromResult(s);
    }

    public BringupSessionDto Session(string controllerId, int leaseSeconds) =>
        new(controllerId, leaseSeconds, Now.AddSeconds(leaseSeconds), Probes, HoldBlack);

    public Task<ProbeResultDto> RunNativeProbeAsync(string controllerId, string probe, CancellationToken ct = default)
    {
        Calls.Add($"RunNativeProbe {controllerId} {probe}");
        return Task.FromResult(NextProbe ?? Result(controllerId, probe, true, "read 4 bytes"));
    }

    public Task<ProbeResultDto> BeginMarkerAsync(MarkerDto marker, CancellationToken ct = default)
    {
        Calls.Add($"BeginMarker {marker.ControllerId}");
        Markers.Add(marker);
        return Task.FromResult(Result(marker.ControllerId, "marker", true, "marker sent"));
    }

    public Task<ProbeResultDto> EndMarkerAsync(string controllerId, CancellationToken ct = default)
    {
        Calls.Add($"EndMarker {controllerId}");
        return Task.FromResult(Result(controllerId, "end-marker", true, "black frame sent"));
    }

    public Task<BringupSessionDto> RenewBringupAsync(string controllerId, CancellationToken ct = default)
    {
        Calls.Add($"RenewBringup {controllerId}");
        return RenewError is { } e ? Task.FromException<BringupSessionDto>(e) : Task.FromResult(Session(controllerId, 60));
    }

    public Task<BringupEndDto> EndBringupAsync(string controllerId, CancellationToken ct = default)
    {
        Calls.Add($"EndBringup {controllerId}");
        return EndError is { } e ? Task.FromException<BringupEndDto>(e) : Task.FromResult(new BringupEndDto(controllerId, false, Result(controllerId, "release", true, "released")));
    }

    public Task<IReadOnlyList<ControllerDto>> ListControllersAsync(CancellationToken ct = default) => UnexpectedAsync<IReadOnlyList<ControllerDto>>();

    public Task<PlaybackCalibrationDto> GetPlaybackCalibrationAsync(CancellationToken ct = default) => UnexpectedAsync<PlaybackCalibrationDto>();

    public Task<PlaybackCalibrationDto> SetPlaybackCalibrationAsync(PlaybackCalibrationDto calibration, CancellationToken ct = default) => UnexpectedAsync<PlaybackCalibrationDto>();

    public Task<IReadOnlyList<EffectDto>> ListEffectsAsync(CancellationToken ct = default) => UnexpectedAsync<IReadOnlyList<EffectDto>>();

    public Task<SceneDto> GetSceneAsync(CancellationToken ct = default) => UnexpectedAsync<SceneDto>();

    public Task SetSceneAsync(SceneDto scene, CancellationToken ct = default) => UnexpectedAsync<bool>();

    public Task SetAssignmentAsync(AssignmentDto assignment, CancellationToken ct = default) => UnexpectedAsync<bool>();

    public Task<IReadOnlyList<ProfileInfoDto>> ListProfilesAsync(CancellationToken ct = default) => UnexpectedAsync<IReadOnlyList<ProfileInfoDto>>();

    public Task SaveProfileAsync(ProfileDto profile, CancellationToken ct = default) => UnexpectedAsync<bool>();

    public Task<ProfileDto> LoadProfileAsync(string name, CancellationToken ct = default) => UnexpectedAsync<ProfileDto>();

    public Task ApplyProfileAsync(string name, CancellationToken ct = default) => UnexpectedAsync<bool>();

    public Task<StatusDto> GetStatusAsync(bool diagnostics = false, CancellationToken ct = default) => UnexpectedAsync<StatusDto>();

    public Task<IReadOnlyList<TelemetryDto>> GetTelemetryAsync(CancellationToken ct = default) => UnexpectedAsync<IReadOnlyList<TelemetryDto>>();

    public Task<TakeoverDto> SetTakeoverAsync(string deviceClass, bool enabled, CancellationToken ct = default) => UnexpectedAsync<TakeoverDto>();

    public Task<IReadOnlyList<ControllerDto>> RescanAsync(CancellationToken ct = default) => UnexpectedAsync<IReadOnlyList<ControllerDto>>();

    public Task<int> SaveLianLiConfigAsync(CancellationToken ct = default) => UnexpectedAsync<int>();

    public Task<int> ResyncLianLiAsync(CancellationToken ct = default) => UnexpectedAsync<int>();

    public Task<IReadOnlyList<MountingDto>> ListMountingsAsync(CancellationToken ct = default) => UnexpectedAsync<IReadOnlyList<MountingDto>>();

    public Task<MountingDto> SetMountingAsync(MountingDto mounting, CancellationToken ct = default) => UnexpectedAsync<MountingDto>();

    public Task<MountingDto> ResetMountingAsync(string controllerId, CancellationToken ct = default) => UnexpectedAsync<MountingDto>();

    public Task<MountingDto> BeginRingCalibrationAsync(RingCalibrationDto calibration, CancellationToken ct = default) => UnexpectedAsync<MountingDto>();

    public Task<MountingDto> EndRingCalibrationAsync(string controllerId, bool apply, CancellationToken ct = default) => UnexpectedAsync<MountingDto>();

    private static ProbeResultDto Result(string id, string probe, bool ok, string summary) =>
        new(id, probe, ok, summary, [], new Dictionary<string, string> { ["comm"] = ok ? "ok" : "failed", ["playback"] = "unobserved", ["optical"] = "pending-owner" });

    private Task<T> UnexpectedAsync<T>([System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        Calls.Add($"UNEXPECTED {name}");
        return Task.FromException<T>(new NotSupportedException(name));
    }
}

/// <summary>WORKORDER-NATIVE-0 scope 9: the Native bring-up expander, gate labels and the Vendor/Program strategy, without a window.</summary>
public sealed class NativeGuiTests
{
    private const string Gpu = "msigpu:10DE-2B85-53011462";
    private const string Aura = "asusaura:0B05-19AF";
    private const string EnePort = "enedram:p0";

    private readonly RecordingRgbService _svc = new();
    private readonly ManualTimeProvider _time = new();
    private readonly List<Exception> _errors = [];

    [Fact]
    public void Nothing_is_sent_on_construction_selection_controller_updates_or_reconnect()
    {
        var conn = new FakeConnection(_svc).Connect();
        using var vm = New(conn);
        vm.SetControllers(Controllers());
        vm.Targets.Select(t => t.Id).Should().Equal([Gpu, Aura, EnePort], "native classes only; Lian Li and virtual controllers are not targets");
        vm.Targets.Should().OnlyContain(t => t.Label.Contains("control not verified", StringComparison.Ordinal));

        vm.SelectedTarget = vm.Targets[0];
        vm.SelectedProbe = "fwinfo";
        vm.MarkerStart = 5;
        vm.StepForwardCommand.Execute(null);
        vm.StepBackCommand.Execute(null);
        vm.StepBackCommand.Execute(null);
        vm.MarkerStart.Should().Be(4, "the stepper is local: ±count, never below 0");
        vm.SetControllers(Controllers());
        vm.SelectedTarget!.Id.Should().Be(Gpu, "the selection survives a controller update by id");
        conn.Disconnect();
        vm.CanBegin.Should().BeFalse();
        conn.Connect();
        vm.SetControllers(Controllers());
        _time.Advance(TimeSpan.FromMinutes(30));

        _svc.Calls.Should().BeEmpty("no probe, marker, renew or begin runs without an explicit button");
        vm.HasSession.Should().BeFalse("a reconnect never begins a session by itself");
        _time.ActiveTimers.Should().Be(0);
        vm.CanBegin.Should().BeTrue();
        vm.MarkerColor.Should().Be(NativeBringupViewModel.Black);
        vm.TargetText.Should().Contain("Control not verified").And.NotContainAny("close", "Close");
    }

    [Fact]
    public async Task Explicit_actions_call_the_connection_owned_rpcs_and_the_default_marker_is_black()
    {
        var conn = new FakeConnection(_svc).Connect();
        using var vm = Selected(conn, Gpu);

        await vm.BeginCommand.ExecuteAsync();
        _svc.Calls.Should().Equal($"BeginBringup {Gpu} 120");
        vm.HasSession.Should().BeTrue();
        vm.Probes.Should().Equal("fwinfo", "family");
        vm.SelectedProbe.Should().Be("fwinfo", "selecting a probe is not running it");
        vm.HeartbeatRunning.Should().BeTrue();
        vm.CanBegin.Should().BeFalse("one session at a time");
        vm.CanEditColor.Should().BeTrue("hold-black is off in this session");

        await vm.RunProbeCommand.ExecuteAsync();
        vm.Results[0].Should().Contain("fwinfo: ok").And.Contain("playback=unobserved").And.Contain("optical=pending-owner");
        await vm.MarkerCommand.ExecuteAsync();
        _svc.Markers[0].Should().Be(new MarkerDto(Gpu, 0, 1, "#000000", 0), "black unless the user types a colour");

        vm.MarkerColor = "#ff0000";
        vm.MarkerStart = 3;
        vm.MarkerCount = 2;
        vm.MarkerChannel = 1;
        await vm.MarkerCommand.ExecuteAsync();
        _svc.Markers[1].Should().Be(new MarkerDto(Gpu, 3, 2, "#FF0000", 1));
        vm.MarkerColor = "white";
        await vm.MarkerCommand.ExecuteAsync();
        _errors.Should().ContainSingle().Which.Should().BeOfType<FormatException>();
        _svc.Markers.Should().HaveCount(2, "an invalid colour is refused before any RPC");

        await vm.EndMarkerCommand.ExecuteAsync();
        await vm.RenewCommand.ExecuteAsync();
        await vm.EndCommand.ExecuteAsync();
        _svc.Calls.Should().Equal(
            $"BeginBringup {Gpu} 120", $"RunNativeProbe {Gpu} fwinfo", $"BeginMarker {Gpu}", $"BeginMarker {Gpu}",
            $"EndMarker {Gpu}", $"RenewBringup {Gpu}", $"EndBringup {Gpu}");
        vm.HasSession.Should().BeFalse();
        vm.HeartbeatRunning.Should().BeFalse();
        _time.ActiveTimers.Should().Be(0);
        vm.MarkerColor.Should().Be(NativeBringupViewModel.Black, "a new session starts black again");
        vm.Results[0].Should().Contain("release: ok");

        vm.EndCommand.CanExecute(null).Should().BeFalse();
        await vm.EndCommand.ExecuteAsync();
        _svc.Calls.Should().HaveCount(7, "End without a session sends nothing");
    }

    [Fact]
    public async Task Hold_black_session_sends_only_black_and_locks_the_colour()
    {
        _svc.HoldBlack = true;
        var conn = new FakeConnection(_svc).Connect();
        using var vm = Selected(conn, Gpu);
        vm.MarkerColor = "#FFFFFF";

        await vm.BeginCommand.ExecuteAsync();
        vm.HoldBlack.Should().BeTrue();
        vm.CanEditColor.Should().BeFalse();
        vm.MarkerColor.Should().Be(NativeBringupViewModel.Black, "the session's hold-black forces the colour");
        vm.SessionText.Should().Contain("hold-black: black markers only");
        vm.MarkerColor = "#FFFFFF";
        vm.MarkerColor.Should().Be(NativeBringupViewModel.Black);
        await vm.MarkerCommand.ExecuteAsync();
        vm.MarkerStart = 12;
        await vm.MarkerCommand.ExecuteAsync();
        await vm.EndMarkerCommand.ExecuteAsync();

        _svc.Markers.Should().HaveCount(2).And.OnlyContain(m => m.Rgb == "#000000");
        _svc.Calls.Should().NotContain(c => c.Contains("UNEXPECTED", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Heartbeat_renews_its_own_session_and_stops_on_disconnect_without_rebeginning()
    {
        var conn = new FakeConnection(_svc).Connect();
        using var vm = Selected(conn, Gpu);
        vm.LeaseSeconds = 60;
        await vm.BeginCommand.ExecuteAsync();
        NativeBringupViewModel.HeartbeatPeriod(60).Should().Be(TimeSpan.FromSeconds(20));

        _time.Advance(TimeSpan.FromSeconds(19));
        Renews().Should().Be(0);
        _time.Advance(TimeSpan.FromSeconds(41));
        Renews().Should().Be(3, "one renew per third of the lease");

        conn.Disconnect();
        vm.HasSession.Should().BeFalse("the Host ends a closed connection's session");
        _time.ActiveTimers.Should().Be(0);
        vm.Results[0].Should().Contain("connection lost");
        _time.Advance(TimeSpan.FromMinutes(10));
        conn.Connect();
        _time.Advance(TimeSpan.FromMinutes(10));
        Renews().Should().Be(3);
        _svc.Calls.Count(c => c.StartsWith("BeginBringup", StringComparison.Ordinal)).Should().Be(1, "never re-begun by itself");
        vm.HasSession.Should().BeFalse();
    }

    [Fact]
    public async Task Heartbeat_stops_after_a_failed_renew_without_retrying()
    {
        var conn = new FakeConnection(_svc).Connect();
        using var vm = Selected(conn, Gpu);
        await vm.BeginCommand.ExecuteAsync();
        _svc.RenewError = new InvalidOperationException("no bring-up session");

        _time.Advance(TimeSpan.FromSeconds(40));
        Renews().Should().Be(1);
        vm.HeartbeatRunning.Should().BeFalse();
        _time.ActiveTimers.Should().Be(0);
        _errors.Should().ContainSingle().Which.Message.Should().Be("no bring-up session");
        vm.Results[0].Should().Contain("heartbeat stopped (no retry)");
        _time.Advance(TimeSpan.FromMinutes(10));
        Renews().Should().Be(1, "no retry");

        vm.HasSession.Should().BeTrue("End stays available to the user");
        await vm.EndCommand.ExecuteAsync();
        _svc.Calls[^1].Should().Be($"EndBringup {Gpu}");
    }

    [Fact]
    public async Task A_failed_end_keeps_the_session_so_end_can_be_pressed_again()
    {
        var conn = new FakeConnection(_svc).Connect();
        using var vm = Selected(conn, Gpu);
        await vm.BeginCommand.ExecuteAsync();
        _svc.EndError = new TimeoutException("pipe stalled");

        await vm.EndCommand.ExecuteAsync();
        vm.HasSession.Should().BeTrue("the Host may still hold it");
        vm.HeartbeatRunning.Should().BeFalse("without End or Renew the lease lapses");
        _time.ActiveTimers.Should().Be(0);
        vm.Results[0].Should().Contain("End failed");
        _errors.Should().ContainSingle().Which.Should().BeOfType<TimeoutException>();

        _svc.EndError = null;
        await vm.EndCommand.ExecuteAsync();
        vm.HasSession.Should().BeFalse();
        _svc.Calls.Count(c => c.StartsWith("EndBringup", StringComparison.Ordinal)).Should().Be(2);
    }

    [Fact]
    public async Task Dispose_stops_the_heartbeat_and_sends_nothing()
    {
        var conn = new FakeConnection(_svc).Connect();
        var vm = Selected(conn, Gpu);
        await vm.BeginCommand.ExecuteAsync();
        var calls = _svc.Calls.Count;

        vm.Dispose();
        _time.ActiveTimers.Should().Be(0);
        _time.Advance(TimeSpan.FromMinutes(10));
        conn.Disconnect();
        conn.Connect();
        _svc.Calls.Should().HaveCount(calls, "Dispose sends no RPC; the Host ends the session when the pipe closes");
        vm.CanBegin.Should().BeFalse();
    }

    [Fact]
    public async Task A_failed_step_that_ended_the_session_resets_it()
    {
        var conn = new FakeConnection(_svc).Connect();
        using var vm = Selected(conn, Gpu);
        await vm.BeginCommand.ExecuteAsync();
        _svc.NextProbe = new ProbeResultDto(Gpu, "fwinfo", false, "native status -5", [],
            new Dictionary<string, string> { ["comm"] = "failed", ["release"] = "done" }, SessionEnded: true);

        await vm.RunProbeCommand.ExecuteAsync();
        vm.HasSession.Should().BeFalse();
        _time.ActiveTimers.Should().Be(0);
        vm.Results[0].Should().Contain("ended the session; nothing is retried");
        vm.Results[1].Should().Contain("fwinfo: failed").And.Contain("comm=failed");
        _time.Advance(TimeSpan.FromMinutes(5));
        Renews().Should().Be(0);
    }

    [Fact]
    public async Task Begin_is_unavailable_for_a_target_another_session_holds()
    {
        var conn = new FakeConnection(_svc).Connect();
        using var vm = New(conn);
        vm.SetControllers([Native(Gpu, "MsiGpu", ControllerStates.BringUp, "bring-up session (pipe:7)")]);
        vm.SelectedTarget = vm.Targets.Single();

        vm.CanBegin.Should().BeFalse();
        vm.BeginCommand.CanExecute(null).Should().BeFalse();
        vm.TargetText.Should().Contain("another bring-up session");
        await vm.BeginCommand.ExecuteAsync();
        _errors.Should().ContainSingle().Which.Should().BeOfType<InvalidOperationException>();
        _svc.Calls.Should().BeEmpty("another client's session is never taken over");
    }

    [Fact]
    public async Task A_begin_answered_after_the_connection_changed_is_not_adopted()
    {
        var conn = new FakeConnection(_svc).Connect();
        using var vm = Selected(conn, Gpu);
        _svc.PendingBegin = new TaskCompletionSource<BringupSessionDto>(TaskCreationOptions.RunContinuationsAsynchronously);

        var begin = vm.BeginCommand.ExecuteAsync();
        conn.Disconnect();
        conn.Connect();
        _svc.PendingBegin.SetResult(_svc.Session(Gpu, 120));
        await begin;

        vm.HasSession.Should().BeFalse("the Host ended the first connection's session when it closed");
        _time.ActiveTimers.Should().Be(0);
        vm.Results[0].Should().Contain("connection changed");
    }

    [Fact]
    public async Task Devices_page_has_five_takeover_rows_and_gate_labels_that_name_no_process_to_close()
    {
        await using var client = new PipeClient("UnifiedRgb-unused-" + Guid.NewGuid().ToString("N"));
        var devices = new DevicesViewModel(client, InlineDispatcher.Instance, _errors.Add);
        devices.Takeover.Select(t => t.DeviceClass).Should().Equal("LianLi", "MsiGpu", "OpenRgb", "AsusAura", "EneDram");
        devices.Takeover.Should().OnlyContain(t => t.Enabled == null, "unknown until set; nothing is applied on construction");

        var unverified = new ControllerItem(Native(Aura, "AsusAura"));
        unverified.Hint.Should().Contain("Control not verified (AsusAura control not verified)").And.Contain("Native bring-up")
            .And.NotContain("close it").And.NotContain("Blocked by");
        unverified.Zones.Should().Be("2 zone(s), 9 LEDs, 1 unmapped");
        new ControllerItem(Native(EnePort, "EneDram", blockedBy: "takeover off for EneDram")).Hint
            .Should().StartWith("Takeover is off for EneDram").And.Contain("Control not verified").And.NotContain("close it");
        new ControllerItem(Native(Gpu, "MsiGpu", ControllerStates.BringUp, "bring-up session (pipe:3)")).Hint
            .Should().StartWith("Native bring-up session");
        new ControllerItem(Native(Gpu, "MsiGpu", blockedBy: "takeover off for MsiGpu", verified: true)).Hint
            .Should().Be("Takeover is off for MsiGpu: turn it on below.");
        new ControllerItem(AppLogicTests.Controller("l", ControllerStates.Blocked) with { BlockedBy = "L-Connect 3.exe" }).Hint
            .Should().Contain("close it", "a real vendor process keeps the existing wording");

        var dto = Native(Aura, "AsusAura", ControllerStates.Active, verified: true);
        new ZoneItem(dto, dto.Zones[0]).Label.Should().EndWith("· unmapped");
        new ZoneItem(dto, dto.Zones[1]).Label.Should().NotContain("unmapped");
        _errors.Should().BeEmpty();
    }

    [Fact]
    public async Task Effects_and_scene_send_the_strategy_with_the_legacy_loop_policy_and_offer_no_frames()
    {
        await using var client = new PipeClient("UnifiedRgb-unused-" + Guid.NewGuid().ToString("N"));
        using var fx = new EffectsViewModel(client, InlineDispatcher.Instance, _errors.Add) { LiveApply = false };
        fx.Strategies.Should().Equal("Vendor", "Program");
        fx.Effects.Add(new EffectDto("StaticColor", "Static", 1, 1, false, false, true, ["#FF0000"]));
        fx.SelectedEffect = fx.Effects[0];
        fx.SetControllers([LianLi()]);
        fx.ShowStrategy.Should().BeFalse("nothing selected yet");
        fx.Zones[0].IsSelected = true;
        fx.ShowStrategy.Should().BeTrue();

        Pair(fx.BuildAssignment()).Should().Be(("LConnect", "Vendor"), "Vendor is the default, as the old L-Connect switch was");
        fx.LConnectLoops = false;
        (fx.Strategy, Pair(fx.BuildAssignment())).Should().Be(("Program", ("Generic", "Program")));
        fx.Strategy = "Vendor";
        fx.LConnectLoops.Should().BeTrue();
        fx.Strategy = "Frames";
        fx.Strategy.Should().Be("Vendor", "Frames has no asset source in the GUI");

        var scene = new SceneViewModel(client, InlineDispatcher.Instance, _errors.Add);
        scene.Strategies.Should().Equal("Vendor", "Program");
        scene.GroupZones.Add(fx.Zones[0]);
        Pair(scene.BuildGroup()).Should().Be(("LConnect", "Vendor"));
        scene.GroupStrategy = "program";
        Pair(scene.BuildGroup()).Should().Be(("Generic", "Program"));
        scene.GroupStrategy = "Frames";
        scene.GroupStrategy.Should().Be("Vendor");

        var legacy = new AssignmentDto([new ZoneRefDto("c", 0)], "Runway", []);
        AssignmentItem.StrategyOf(legacy).Should().Be("Vendor", "an old profile without a policy is Vendor");
        AssignmentItem.StrategyOf(legacy with { LoopPolicy = "Generic" }).Should().Be("Program");
        AssignmentItem.StrategyOf(legacy with { LoopPolicy = "LConnect", Strategy = "Program" }).Should().Be("Program", "Strategy wins, as in the Host");
        AssignmentItem.StrategyOf(legacy with { Strategy = "Frames", FrameSetId = "heartflow-gpu" }).Should().Be("Frames: heartflow-gpu");
        new AssignmentItem(legacy with { GroupName = "All" }, "c/0", null).Title.Should().Be("All: Runway · Vendor");
        _errors.Should().BeEmpty();
    }

    [Fact]
    public void Diagnostics_shows_native_frame_statistics_and_writer_candidates_as_informational()
    {
        var conn = new FakeConnection(_svc);
        using var bringup = New(conn);
        var diag = new DiagnosticsViewModel(InlineDispatcher.Instance, Path.GetTempPath(), Path.GetTempPath(), _errors.Add, bringup: bringup);
        diag.Bringup.Should().BeSameAs(bringup);
        DiagnosticsViewModel.FormatWriterCandidates(null).Should().BeEmpty();

        var device = new DeviceStatusDto(Gpu, "RTX 5090", ControllerStates.Blocked, "MsiGpu control not verified", 0, 20, 0, 0, 0, null,
            FrameMsP50: 3.5, FrameMsP99: 40, EffectiveFps: 16, FpsSource: "measured", BudgetSkips: 7);
        diag.Update(new StatusDto(DateTimeOffset.UnixEpoch, "t", 1, 0, new ClockStatusDto(60, 60, 0, 0, 0, 0, 0), [device],
            WriterCandidates: [new WriterCandidateDto("SignalRgb.exe", ["MsiGpu", "AsusAura"], "known lighting app")]));

        diag.Devices.Single().Should().Be(device, "the native columns bind FrameMsP50/P99, EffectiveFps, FpsSource and BudgetSkips");
        diag.WriterCandidates.Should().Contain("not confirmed lighting writers").And.Contain("nothing is stopped")
            .And.Contain("SignalRgb.exe → MsiGpu/AsusAura (known lighting app)");
        _svc.Calls.Should().BeEmpty();
    }

    [Fact]
    public void The_changed_views_parse_and_bind_without_a_window()
    {
        Exception? failure = null;
        var calls = -1;
        var thread = new Thread(() =>
        {
            try
            {
                var svc = new RecordingRgbService();
                using var bringup = new NativeBringupViewModel(new FakeConnection(svc).Connect(), InlineDispatcher.Instance, _ => { }, new ManualTimeProvider());
                bringup.SetControllers(Controllers());
                bringup.SelectedTarget = bringup.Targets[0];
                var diag = new App.Views.DiagnosticsView
                {
                    DataContext = new DiagnosticsViewModel(InlineDispatcher.Instance, Path.GetTempPath(), Path.GetTempPath(), _ => { }, bringup: bringup),
                };
                diag.Measure(new System.Windows.Size(1400, 900));
                diag.Arrange(new System.Windows.Rect(0, 0, 1400, 900));
                _ = new App.Views.EffectsView();
                _ = new App.Views.SceneView();
                calls = svc.Calls.Count;
            }
#pragma warning disable CA1031 // reported to the test thread
            catch (Exception ex)
#pragma warning restore CA1031
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(60)).Should().BeTrue();
        failure.Should().BeNull();
        calls.Should().Be(0, "laying out the expander runs no bring-up step");
    }

    private static (string?, string?) Pair(AssignmentDto a) => (a.LoopPolicy, a.Strategy);

    private int Renews() => _svc.Calls.Count(c => c.StartsWith("RenewBringup", StringComparison.Ordinal));

    private NativeBringupViewModel New(FakeConnection conn) => new(conn, InlineDispatcher.Instance, _errors.Add, _time);

    private NativeBringupViewModel Selected(FakeConnection conn, string id)
    {
        var vm = New(conn);
        vm.SetControllers(Controllers());
        vm.SelectedTarget = vm.Targets.Single(t => t.Id == id);
        return vm;
    }

    private static ControllerDto Native(string id, string cls, string state = ControllerStates.Blocked, string? blockedBy = null, bool verified = false) =>
        new(id, "Native", id, cls, "Motherboard", id,
            [new ZoneDto(0, "Onboard (aggregate, unmapped)", "Point", 1, 0, 0, Mapped: false), new ZoneDto(1, "ARGB header 1", "Linear", 8, 0, 0)],
            20, state, blockedBy ?? $"{cls} control not verified", verified);

    private static ControllerDto LianLi() =>
        new("lianli:rf:AA", "Lian Li", "TL AA", "LianLi", "Fan", "AA", [new ZoneDto(0, "TL", "Linear", 78, 0, 0, "TL", 3)], 50, ControllerStates.Active, null);

    private static ControllerDto[] Controllers() =>
    [
        Native(Gpu, "MsiGpu"),
        Native(Aura, "AsusAura"),
        Native(EnePort, "EneDram", blockedBy: "enedram:p0 is a bring-up target (SMBus port 0), never a writer"),
        LianLi(),
        AppLogicTests.Controller("virtual:fan1", ControllerStates.Active),
    ];
}
