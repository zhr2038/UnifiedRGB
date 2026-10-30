using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using UnifiedRgb.App.Ipc;
using UnifiedRgb.App.Mvvm;
using UnifiedRgb.App.ViewModels;
using UnifiedRgb.App.Workbench;
using UnifiedRgb.Contracts;
using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Devices;
using UnifiedRgb.Devices.AsusRog;

namespace UnifiedRgb.Host.Tests;

/// <summary>
/// Fake-only per-key calibration panel: one red raw-LED marker through the existing leased bring-up session on a single service
/// reference, restored and released in finally with CancellationToken.None; explicit recording of only that marker's own
/// selection; and a local observed-subset settings fragment that the real binder and planner read route by route. Nothing here
/// touches a device, the Host, a file or a setting, and nothing is optical proof.
/// </summary>
public sealed class PerKeyCalibrationTests
{
    private const string KeyboardId = "asusr:wired:" + "AAAAAAAA";
    private const string OtherKeyboardId = "asusr:wired:" + "BBBBBBBB";
    private const string Usb = RogPerKeyLighting.WiredTransport;
    private const string Rf = RogPerKeyLighting.WirelessTransport;
    private const string Begin = nameof(IRgbService.BeginBringupAsync);
    private const string Probe = nameof(IRgbService.RunNativeProbeAsync);
    private const string Mark = nameof(IRgbService.BeginMarkerAsync);
    private const string Unmark = nameof(IRgbService.EndMarkerAsync);
    private const string End = nameof(IRgbService.EndBringupAsync);
    private const string Hold = "Hold";
    private static readonly DateTimeOffset Now = new(2026, 10, 30, 8, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task Highlight_lights_one_red_raw_led_on_the_actual_route_with_one_service_then_restores_and_releases()
    {
        var panel = Panel(switching: true);
        var vm = panel.Vm;
        vm.SetControllers([Keyboard()]);
        vm.SelectedKeyboard!.Id.Should().Be(KeyboardId);
        vm.SelectedRoute.Should().BeNull("the route is never guessed");
        vm.HighlightCommand.CanExecute(null).Should().BeFalse();
        vm.SelectedRoute = Route(Usb);
        vm.HighlightCommand.CanExecute(null).Should().BeTrue();
        panel.Connection.Requires.Should().Be(0, "listing and selecting make no request");

        (await vm.HighlightAsync()).Should().BeTrue();

        panel.Connection.Requires.Should().Be(1);
        panel.Other.Calls.Should().BeEmpty("the whole operation stays on the service it required once");
        panel.Rpc.Calls.Should().Equal(Begin, Probe, Mark, Hold, Unmark, End);
        panel.Rpc.Probes.Should().Equal("per-key-status");
        panel.Rpc.Markers.Should().Equal(new MarkerDto(KeyboardId, 0, 1, "#FF0000", 1));
        panel.Rpc.Holds.Should().Equal(TimeSpan.FromSeconds(2));
        panel.Rpc.Tokens.Should().OnlyContain(t => !t.CanBeCanceled, "cleanup and every step use CancellationToken.None");
        vm.LastMarker.Should().Be(new PerKeyMarkerSelection(KeyboardId, Usb, 0));
        vm.Status.Should().Contain("已恢复当前场景").And.Contain("记录观察到的按键");
        vm.Busy.Should().BeFalse();
        vm.RecordCommand.CanExecute(null).Should().BeTrue();
        vm.Observed.Should().BeEmpty("a successful send confirms nothing by itself");
    }

    [Fact]
    public async Task A_refused_marker_is_restored_and_released_and_a_session_ended_by_the_host_is_not_restored_twice()
    {
        var refused = Ready(switching: true);
        refused.Rpc.MarkerResult = Result("marker", false, "LED write rejected");
        (await refused.Vm.HighlightAsync()).Should().BeFalse();
        refused.Rpc.Calls.Should().Equal(Begin, Probe, Mark, Unmark, End);
        refused.Other.Calls.Should().BeEmpty();
        refused.Rpc.Holds.Should().BeEmpty("nothing lit, so nothing is held");
        refused.Vm.LastMarker.Should().BeNull();
        refused.Vm.Status.Should().Contain("标记未成功：LED write rejected");

        var ended = Ready();
        ended.Rpc.MarkerResult = Result("marker", false, "device stopped answering", ended: true);
        (await ended.Vm.HighlightAsync()).Should().BeFalse();
        ended.Rpc.Calls.Should().Equal(Begin, Probe, Mark, End);
        ended.Vm.Status.Should().Contain("device stopped answering").And.Contain("后台已结束会话");
        ended.Vm.LastMarker.Should().BeNull();
    }

    [Fact]
    public async Task A_throwing_marker_or_hold_still_restores_and_releases_and_reports_the_original_error()
    {
        var marker = Ready(switching: true);
        marker.Rpc.MarkerError = new IOException("pipe broken");
        await marker.Vm.Awaiting(v => v.HighlightAsync()).Should().ThrowAsync<IOException>().WithMessage("pipe broken");
        marker.Rpc.Calls.Should().Equal(Begin, Probe, Mark, Unmark, End);
        marker.Other.Calls.Should().BeEmpty("restore and release use the service the marker went to");
        marker.Rpc.Tokens.Should().OnlyContain(t => !t.CanBeCanceled);
        marker.Vm.Status.Should().Contain("pipe broken");
        marker.Vm.Busy.Should().BeFalse();
        marker.Vm.LastMarker.Should().BeNull();

        var hold = Ready((_, _) => throw new TimeoutException("hold interrupted"), switching: true);
        await hold.Vm.Awaiting(v => v.HighlightAsync()).Should().ThrowAsync<TimeoutException>();
        hold.Rpc.Calls.Should().Equal(Begin, Probe, Mark, Hold, Unmark, End);
        hold.Other.Calls.Should().BeEmpty();
        hold.Vm.LastMarker.Should().BeNull();

        // Through the command the error reaches the UI boundary and the command re-enables.
        var command = Ready();
        command.Rpc.MarkerError = new IOException("pipe broken");
        await command.Vm.HighlightCommand.ExecuteAsync();
        command.Errors.Should().ContainSingle().Which.Should().BeOfType<IOException>();
        command.Rpc.Calls.Should().Equal(Begin, Probe, Mark, Unmark, End);
        command.Vm.HighlightCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public async Task A_failed_begin_sends_nothing_else_and_never_ends_a_session_it_did_not_start()
    {
        var panel = Ready();
        panel.Rpc.BeginError = new InvalidOperationException("session owned by another connection");

        await panel.Vm.HighlightCommand.ExecuteAsync();

        panel.Errors.Should().ContainSingle().Which.Message.Should().Be("session owned by another connection");
        panel.Rpc.Calls.Should().Equal(Begin);
        panel.Vm.Status.Should().Contain("未能开始校准会话，未发送标记");
        panel.Vm.Busy.Should().BeFalse();
        panel.Vm.LastMarker.Should().BeNull();
    }

    [Fact]
    public async Task Restore_and_release_failures_are_reported_and_never_called_success()
    {
        var restore = Ready();
        restore.Rpc.UnmarkResult = Result("end-marker", false, "restore write failed");
        (await restore.Vm.HighlightAsync()).Should().BeFalse();
        restore.Rpc.Calls.Should().Equal(Begin, Probe, Mark, Hold, Unmark, End);
        restore.Vm.Status.Should().Contain("恢复或释放未成功").And.Contain("恢复标记失败：restore write failed");
        restore.Vm.LastMarker.Should().NotBeNull("the LED did light, so the owner may still record what they saw");

        var release = Ready();
        release.Rpc.Release = Result("release", false, "release frame not acknowledged");
        (await release.Vm.HighlightAsync()).Should().BeFalse();
        release.Vm.Status.Should().Contain("释放会话失败：release frame not acknowledged");

        var thrown = Ready();
        thrown.Rpc.EndError = new IOException("pipe closed during release");
        (await thrown.Vm.HighlightAsync()).Should().BeFalse();
        thrown.Rpc.Calls.Should().Equal(Begin, Probe, Mark, Hold, Unmark, End);
        thrown.Vm.Status.Should().Contain("释放会话失败：pipe closed during release");
        thrown.Errors.Should().BeEmpty();
    }

    [Theory]
    [InlineData(Rf, Usb, "后台当前实际路由为 USB 有线")]
    [InlineData(Usb, Rf, "后台当前实际路由为 2.4G 无线")]
    [InlineData(Usb, "unavailable", "后台当前实际路由为 unavailable")]
    [InlineData(Usb, null, "后台当前实际路由为 未知")]
    public async Task The_selected_route_must_be_the_actual_route_before_any_marker(string selected, string? actual, string text)
    {
        var panel = Ready(route: selected);
        panel.Rpc.Transport = actual;

        (await panel.Vm.HighlightAsync()).Should().BeFalse();

        panel.Rpc.Calls.Should().Equal(Begin, Probe, End);
        panel.Rpc.Markers.Should().BeEmpty();
        panel.Vm.Status.Should().Contain(text).And.Contain("未发送标记");
        panel.Vm.LastMarker.Should().BeNull();
        panel.Vm.RecordCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public async Task A_failed_status_probe_or_a_black_only_host_sends_no_red_marker()
    {
        var probe = Ready();
        probe.Rpc.ProbeResult = Result("per-key-status", false, "keyboard owner not attached");
        (await probe.Vm.HighlightAsync()).Should().BeFalse();
        probe.Rpc.Calls.Should().Equal(Begin, Probe, End);
        probe.Vm.Status.Should().Contain("状态探测未成功").And.Contain("keyboard owner not attached");

        var black = Ready();
        black.Rpc.HoldBlack = true;
        (await black.Vm.HighlightAsync()).Should().BeFalse();
        black.Rpc.Calls.Should().Equal(Begin, Probe, End);
        black.Vm.Status.Should().Contain("只接受黑色标记");
    }

    [Fact]
    public async Task Recording_takes_only_the_marker_s_own_selection_and_any_change_before_recording_refuses_it()
    {
        var panel = Ready(led: 5);
        (await panel.Vm.HighlightAsync()).Should().BeTrue();
        panel.Vm.KeyName = "Q";
        panel.Vm.LedId = 6;
        panel.Vm.LastMarker.Should().BeNull();
        panel.Vm.RecordCommand.CanExecute(null).Should().BeFalse();
        panel.Vm.Status.Should().Contain("上一次标记不可再记录");
        panel.Vm.LedId = 5;
        panel.Vm.Invoking(v => v.Record()).Should().Throw<InvalidOperationException>().WithMessage("没有可记录的成功标记*");

        (await panel.Vm.HighlightAsync()).Should().BeTrue();
        panel.Vm.SelectedRoute = Route(Rf);
        panel.Vm.Invoking(v => v.Record()).Should().Throw<InvalidOperationException>();
        panel.Vm.SelectedRoute = Route(Usb);
        (await panel.Vm.HighlightAsync()).Should().BeTrue();
        panel.Vm.SetControllers([Keyboard(OtherKeyboardId)]);
        panel.Vm.SelectedKeyboard!.Id.Should().Be(OtherKeyboardId);
        panel.Vm.Invoking(v => v.Record()).Should().Throw<InvalidOperationException>();
        panel.Vm.Observed.Should().BeEmpty();

        // A change while the LED is held (inputs are disabled in the view, but a refresh can still replace the keyboard).
        var held = Panel((_, _) => Task.CompletedTask);
        held.Vm.SetControllers([Keyboard()]);
        held.Vm.SelectedRoute = Route(Usb);
        held.Rpc.OnHold = () => held.Vm.SetControllers([Keyboard(OtherKeyboardId)]);
        (await held.Vm.HighlightAsync()).Should().BeFalse();
        held.Vm.LastMarker.Should().BeNull();
        held.Vm.Status.Should().Contain("点亮期间所选键盘、传输或 LED 已改变");

        // Unchanged: the record carries the marker's controller, route and LED plus what the owner typed.
        var ok = Ready();
        (await ok.Vm.HighlightAsync()).Should().BeTrue();
        ok.Vm.KeyName = " Esc ";
        var row = ok.Vm.Record();
        (row.ControllerId, row.Transport, row.LedId, row.Key, row.Row, row.Column).Should().Be((KeyboardId, Usb, 0, "Esc", 0, 0));
        row.ObservedAt.Should().Be(Now);
        ok.Vm.LastMarker.Should().BeNull("one marker records at most one key");
        ok.Vm.Invoking(v => v.Record()).Should().Throw<InvalidOperationException>();
        ok.Vm.Observed.Should().ContainSingle();
    }

    [Fact]
    public async Task Busy_blocks_every_other_command_while_a_marker_is_held()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var panel = Ready((_, _) => gate.Task);
        var observed = await Observe(Ready(), Usb, 1, "F1", 0, 18);
        var running = panel.Vm.HighlightAsync();

        panel.Vm.Busy.Should().BeTrue();
        panel.Vm.IsIdle.Should().BeFalse();
        foreach (var command in new[] { panel.Vm.HighlightCommand, panel.Vm.RecordCommand, panel.Vm.ExportCommand, panel.Vm.RemoveCommand })
            command.CanExecute(observed).Should().BeFalse();
        await panel.Vm.Awaiting(v => v.HighlightAsync()).Should().ThrowAsync<InvalidOperationException>().WithMessage("逐键校准正在进行*");
        panel.Vm.Invoking(v => v.Record()).Should().Throw<InvalidOperationException>().WithMessage("逐键校准正在进行*");
        panel.Vm.Invoking(v => v.Export()).Should().Throw<InvalidOperationException>().WithMessage("逐键校准正在进行*");
        panel.Rpc.Calls.Should().Equal(Begin, Probe, Mark, Hold);

        gate.SetResult();
        (await running).Should().BeTrue();
        panel.Rpc.Calls.Should().Equal(Begin, Probe, Mark, Hold, Unmark, End);
        panel.Vm.Busy.Should().BeFalse();
        panel.Vm.ExportCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public async Task Export_holds_only_observed_keys_per_route_and_binds_through_the_real_planner()
    {
        var panel = Ready();
        await Observe(panel, Usb, 0, "Esc", 0, 0);
        await Observe(panel, Rf, 0, "Esc", 0, 0);
        var calls = panel.Rpc.Calls.Count;

        var text = panel.Vm.Export();

        panel.Rpc.Calls.Should().HaveCount(calls, "exporting makes no request");
        panel.Vm.ExportText.Should().Be(text);
        text.Should().Contain("\n", "the fragment is multiline");
        var root = JsonNode.Parse(text)!.AsObject();
        root.Select(p => p.Key).Should().Equal("UnifiedRgb");
        var perKey = root["UnifiedRgb"]!["AsusRog"]!["KeyboardPerKey"]!;
        perKey["Enabled"]!.GetValue<bool>().Should().BeTrue();
        var maps = perKey["Maps"]!.AsArray();
        maps.Select(m => m!["Transport"]!.GetValue<string>()).Should().Equal(Usb, Rf);
        foreach (var map in maps)
        {
            map!["Model"]!.GetValue<string>().Should().Contain("1 key(s)").And.Contain("not full keyboard");
            map["Evidence"]!.GetValue<string>().Should().Contain("1 observed key(s) (Esc)").And.Contain("only this observed subset").And.Contain("2026-10-30");
            map["Keys"]!.AsArray().Should().ContainSingle();
            map["Keys"]![0]!.ToJsonString().Should().Be("""{"LedId":0,"Key":"Esc","Row":0,"Column":0}""");
        }
        panel.Vm.ExportSummary.Should().Contain("USB 有线 1 键（Esc）").And.Contain("2.4G 无线 1 键（Esc）").And.Contain("不是整把键盘")
            .And.NotContain("注意");

        var options = Bind(text);
        foreach (var active in new[] { Usb, Rf })
        {
            var plan = RogPerKeyPlanner.Plan(options, [Usb, Rf], active)!;
            plan.Ready.Should().BeTrue("the {0} observed subset is a valid map", active);
            plan.Capability.KeyCount.Should().Be(1);
            plan.Map!.Keys.Should().Equal(new PerKeyCell(0, "Esc", 0, 0));
            plan.RouteMaps.Keys.Should().BeEquivalentTo(Usb, Rf);
        }
    }

    [Fact]
    public async Task Export_keeps_routes_and_keyboards_apart_and_invents_no_map_for_an_unobserved_route()
    {
        var panel = Ready();
        await Observe(panel, Usb, 0, "Esc", 0, 0);
        await Observe(panel, Usb, 1, "F1", 0, 18);

        var text = panel.Vm.Export();

        text.Should().NotContain(Rf, "nothing was observed on 2.4G");
        var options = Bind(text);
        options.Maps.Should().ContainSingle().Which.Keys.Select(k => k.Key).Should().Equal("Esc", "F1");
        RogPerKeyPlanner.Plan(options, [Usb, Rf], Usb)!.Ready.Should().BeTrue();
        RogPerKeyPlanner.Plan(options, [Usb, Rf], Rf)!.Ready.Should().BeFalse("a USB observation says nothing about 2.4G");
        RogPerKeyPlanner.Plan(options, [Rf])!.Ready.Should().BeFalse();
        panel.Vm.ExportSummary.Should().Contain("USB 有线 2 键（Esc、F1）").And.NotContain("2.4G");

        // A different subset per route is exported as observed, and the geometry difference is stated.
        await Observe(panel, Rf, 0, "Esc", 0, 0);
        panel.Vm.Export();
        panel.Vm.ExportSummary.Should().Contain("2.4G 无线 1 键（Esc）").And.Contain("注意：USB 与 2.4G 子集的按键或位置不同");

        // Rows belong to the keyboard they were observed on.
        panel.Vm.SetControllers([Keyboard(), Keyboard(OtherKeyboardId)]);
        panel.Vm.SelectedKeyboard = panel.Vm.Keyboards.Single(k => k.Id == OtherKeyboardId);
        panel.Vm.Invoking(v => v.Export()).Should().Throw<InvalidOperationException>().WithMessage("该键盘没有已记录的观察按键*");
        panel.Vm.ExportText.Should().BeEmpty();
    }

    [Fact]
    public async Task Duplicates_and_empty_input_produce_no_fragment_and_rows_can_be_edited_and_removed()
    {
        var panel = Ready();
        panel.Vm.Invoking(v => v.Export()).Should().Throw<InvalidOperationException>();
        panel.Vm.ExportText.Should().BeEmpty();
        panel.Vm.ExportSummary.Should().Contain("不会生成任何映射");

        var esc = await Observe(panel, Usb, 0, "Esc", 0, 0);
        var f1 = await Observe(panel, Usb, 1, "F1", 0, 18);

        // Recording refuses a second row for the same LED, name or position on the route.
        await Lit(panel, Usb, 0);
        panel.Vm.KeyName = "Tab";
        panel.Vm.Invoking(v => v.Record()).Should().Throw<InvalidOperationException>().WithMessage("*LED 0 已记录为“Esc”*");
        await Lit(panel, Usb, 2);
        panel.Vm.KeyName = "Esc";
        panel.Vm.Invoking(v => v.Record()).Should().Throw<InvalidOperationException>().WithMessage("*已记录按键“Esc”*");
        await Lit(panel, Usb, 2);
        (panel.Vm.KeyName, panel.Vm.Row, panel.Vm.Column) = ("F2", 0, 18);
        panel.Vm.Invoking(v => v.Record()).Should().Throw<InvalidOperationException>().WithMessage("*行 0 列 18 已被“F1”占用*");
        panel.Vm.Observed.Should().HaveCount(2);

        panel.Vm.Export().Should().NotBeEmpty();
        f1.Key = "Esc";
        panel.Vm.ExportText.Should().BeEmpty("an edit makes the shown fragment stale");
        panel.Vm.Invoking(v => v.Export()).Should().Throw<InvalidOperationException>().WithMessage("*按键名称“Esc”重复*");
        panel.Vm.ExportText.Should().BeEmpty();
        f1.Key = "F1";
        (f1.Row, f1.Column) = (0, 0);
        panel.Vm.Invoking(v => v.Export()).Should().Throw<InvalidOperationException>().WithMessage("*行 0 列 0 重复*");
        f1.Column = -1;
        panel.Vm.Invoking(v => v.Export()).Should().Throw<InvalidOperationException>().WithMessage("*行/列为负数*");
        f1.Key = " ";
        panel.Vm.Invoking(v => v.Export()).Should().Throw<InvalidOperationException>().WithMessage("*缺少按键名称*");

        await panel.Vm.RemoveCommand.ExecuteAsync(f1);
        panel.Vm.Observed.Should().Equal(esc);
        Bind(panel.Vm.Export()).Maps.Single().Keys.Should().ContainSingle().Which.Key.Should().Be("Esc");
    }

    [Fact]
    public async Task The_devices_page_lists_only_rog_keyboards_and_makes_no_request_on_refresh_or_selection()
    {
        await using var client = new PipeClient("UnifiedRgb-unused-" + Guid.NewGuid().ToString("N"));
        var errors = new List<Exception>();
        var devices = new DevicesViewModel(client, InlineDispatcher.Instance, errors.Add);
        var receiver = Keyboard("asusr:rf:receiver") with { Zones = [] };
        var mouse = Keyboard("asusr:mouse:1") with { DeviceType = "Mouse" };
        var other = Keyboard("openrgb:0") with { DeviceClass = "OpenRgb" };

        devices.Update([other, receiver, mouse, Keyboard()]);

        var calibration = devices.Calibration;
        calibration.Keyboards.Select(k => k.Id).Should().Equal(KeyboardId);
        calibration.SelectedKeyboard!.Name.Should().Contain("ROG 夜魔 X").And.Contain(KeyboardId);
        calibration.SelectedRoute = calibration.Routes.Single(r => r.Label == "2.4G 无线");
        calibration.LedId = 3;
        var selected = calibration.SelectedKeyboard;
        devices.Update([Keyboard(), Keyboard(OtherKeyboardId)]);
        calibration.SelectedKeyboard.Should().BeSameAs(selected, "a refresh keeps the selection");
        calibration.Keyboards.Should().HaveCount(2);
        calibration.Routes.Select(r => (r.Transport, r.Label)).Should().Equal((Usb, "USB 有线"), (Rf, "2.4G 无线"));
        errors.Should().BeEmpty("nothing required the (absent) Host");
        PerKeyCalibrationViewModel.NextStep.Should().Contain("UnifiedRgb:AsusRog:KeyboardPerKey").And.Contain("共享时间轴").And.Contain("完整键位布局需后续继续扫描");
    }

    private static PerKeyCalibrationRoute Route(string transport) => PerKeyCalibrationRoute.All.Single(r => r.Transport == transport);

    private static ControllerDto Keyboard(string id = KeyboardId) => new(id, "ASUS ROG", "ROG Azoth X", "AsusRog", "Keyboard", "",
        [new(0, "Keyboard", "Point", 1, 0, 0)], 30, ControllerStates.Active, null, true);

    private static ProbeResultDto Result(string probe, bool ok, string summary, IReadOnlyDictionary<string, string>? fields = null, bool ended = false) =>
        new(KeyboardId, probe, ok, summary, [], fields ?? new Dictionary<string, string>(), ended);

    private static RogPerKeyOptions Bind(string json)
    {
        var options = new UnifiedRgbOptions();
        using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
            new ConfigurationBuilder().AddJsonStream(stream).Build().GetSection(UnifiedRgbOptions.Section).Bind(options);
        return options.AsusRog.KeyboardPerKey;
    }

    /// <summary>A panel whose keyboard, route and LED are selected; the hold completes at once unless a delay is given.</summary>
    private static CalibrationPanel Ready(Func<TimeSpan, CancellationToken, Task>? hold = null, string route = Usb, int led = 0, bool switching = false)
    {
        var panel = Panel(hold ?? ((_, _) => Task.CompletedTask), switching);
        panel.Vm.SetControllers([Keyboard()]);
        panel.Vm.SelectedRoute = Route(route);
        panel.Vm.LedId = led;
        panel.Rpc.Transport = route;
        return panel;
    }

    /// <param name="switching">Every Require after the first returns a different service, so a second Require is caught.</param>
    private static CalibrationPanel Panel(Func<TimeSpan, CancellationToken, Task>? hold = null, bool switching = false)
    {
        var rpc = CalibrationRpc.New();
        var other = CalibrationRpc.New();
        var connection = new SwitchingConnection(rpc.Service, switching ? other.Service : null);
        var errors = new List<Exception>();
        hold ??= (_, _) => Task.CompletedTask;
        var vm = new PerKeyCalibrationViewModel(connection, errors.Add, (wait, ct) =>
        {
            rpc.Calls.Add(Hold);
            rpc.Holds.Add(wait);
            rpc.Tokens.Add(ct);
            rpc.OnHold?.Invoke();
            return hold(wait, ct);
        }, new FixedTime(Now));
        return new(vm, rpc, other, connection, errors);
    }

    /// <summary>Lights the LED on the route the fake reports as actual, without recording.</summary>
    private static async Task Lit(CalibrationPanel panel, string route, int led)
    {
        panel.Vm.SelectedRoute = Route(route);
        panel.Vm.LedId = led;
        panel.Rpc.Transport = route;
        (await panel.Vm.HighlightAsync()).Should().BeTrue(panel.Vm.Status);
    }

    private static async Task<PerKeyObservedKey> Observe(CalibrationPanel panel, string route, int led, string key, int row, int column)
    {
        await Lit(panel, route, led);
        (panel.Vm.KeyName, panel.Vm.Row, panel.Vm.Column) = (key, row, column);
        return panel.Vm.Record();
    }

    private sealed record CalibrationPanel(PerKeyCalibrationViewModel Vm, CalibrationRpc Rpc, CalibrationRpc Other, SwitchingConnection Connection,
        List<Exception> Errors);

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    /// <summary>Connected; the first Require returns the first service and, when a later service is given, every later one returns it.</summary>
    private sealed class SwitchingConnection(IRgbService first, IRgbService? later) : IHostConnection
    {
        public event EventHandler<ConnectionState>? StateChanged { add { } remove { } }

        public ConnectionState State => ConnectionState.Connected;

        public int Connections => 1;

        public int Requires { get; private set; }

        public IRgbService Require() => Requires++ == 0 || later is null ? first : later;
    }
}

/// <summary>
/// Scripted bring-up answers only (the adapter's per-key-status fields and marker results); any other RPC fails loudly. Records
/// every call and token in order.
/// </summary>
public class CalibrationRpc : DispatchProxy
{
    public IRgbService Service { get; private set; } = null!;
    public List<string> Calls { get; } = [];
    public List<CancellationToken> Tokens { get; } = [];
    public List<string> Probes { get; } = [];
    public List<MarkerDto> Markers { get; } = [];
    public List<TimeSpan> Holds { get; } = [];
    public Action? OnHold { get; set; }
    /// <summary>The probe's actual route field; null omits it.</summary>
    public string? Transport { get; set; } = RogPerKeyLighting.WiredTransport;
    public bool HoldBlack { get; set; }
    public Exception? BeginError { get; set; }
    public Exception? MarkerError { get; set; }
    public Exception? EndError { get; set; }
    public ProbeResultDto? ProbeResult { get; set; }
    public ProbeResultDto? MarkerResult { get; set; }
    public ProbeResultDto? UnmarkResult { get; set; }
    public ProbeResultDto? Release { get; set; }

    public static CalibrationRpc New()
    {
        var service = Create<IRgbService, CalibrationRpc>();
        var rpc = (CalibrationRpc)(object)service;
        rpc.Service = service;
        return rpc;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        var method = targetMethod?.Name ?? throw new InvalidOperationException();
        if (method.StartsWith("add_", StringComparison.Ordinal) || method.StartsWith("remove_", StringComparison.Ordinal)) return null;
        Calls.Add(method);
        Tokens.Add(args?.OfType<CancellationToken>().FirstOrDefault() ?? default);
        switch (method)
        {
            case nameof(IRgbService.BeginBringupAsync):
                return BeginError is { } begin ? Task.FromException<BringupSessionDto>(begin)
                    : Task.FromResult(new BringupSessionDto((string)args![0]!, (int)args[1]!, DateTimeOffset.UnixEpoch, ["per-key-status"], HoldBlack));
            case nameof(IRgbService.RunNativeProbeAsync):
                Probes.Add((string)args![1]!);
                var fields = new Dictionary<string, string> { ["channel"] = "per-key-led", ["markerChannel"] = "1", ["physicalMap"] = "unverified" };
                if (Transport is not null) fields["transport"] = Transport;
                return Task.FromResult(ProbeResult ?? Result((string)args[0]!, "per-key-status", true, "per-key LED markers available", fields));
            case nameof(IRgbService.BeginMarkerAsync):
                var marker = (MarkerDto)args![0]!;
                Markers.Add(marker);
                return MarkerError is { } failure ? Task.FromException<ProbeResultDto>(failure)
                    : Task.FromResult(MarkerResult ?? Result(marker.ControllerId, "marker", true, "per-key LED marker sent"));
            case nameof(IRgbService.EndMarkerAsync):
                return Task.FromResult(UnmarkResult ?? Result((string)args![0]!, "end-marker", true, "normal frame restored"));
            case nameof(IRgbService.EndBringupAsync):
                var id = (string)args![0]!;
                return EndError is { } end ? Task.FromException<BringupEndDto>(end)
                    : Task.FromResult(new BringupEndDto(id, false, Release ?? Result(id, "release", true, "released")));
            default: throw new InvalidOperationException("Unexpected fake RPC: " + method);
        }
    }

    private static ProbeResultDto Result(string id, string probe, bool ok, string summary, IReadOnlyDictionary<string, string>? fields = null) =>
        new(id, probe, ok, summary, [], fields ?? new Dictionary<string, string>());
}
