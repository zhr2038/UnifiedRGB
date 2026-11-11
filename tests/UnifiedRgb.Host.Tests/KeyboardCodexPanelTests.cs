using UnifiedRgb.App.Mvvm;
using UnifiedRgb.App.Workbench;
using UnifiedRgb.Contracts;
using UnifiedRgb.Contracts.Dto;
using StreamJsonRpc;
using UnifiedRgb.Host.Ipc;

namespace UnifiedRgb.Host.Tests;

public sealed class KeyboardCodexPanelTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Older_mixed_idle_Host_cannot_be_enabled_from_the_independent_page_but_can_be_stopped(bool enabled)
    {
        var service = new Service { Current = new(enabled, enabled ? "Ready" : "Off", null, null, null, null, null) };
        var vm = Vm(service);
        await vm.RefreshAsync();
        vm.CanShow.Should().BeFalse(); vm.Summary.Should().Contain("更新后台");
        await vm.SetEnabledAsync(true); service.Writes.Should().BeEmpty();
        if (enabled)
        {
            vm.CanStop.Should().BeTrue(); await vm.SetEnabledAsync(false); service.Writes.Should().Equal(false);
        }
    }

    [Fact]
    public async Task Desktop_preview_converts_account_reset_time_to_the_users_local_timezone()
    {
        var clock = new ManualTimeProvider();
        var reset = clock.GetUtcNow().AddDays(7).ToUniversalTime();
        var service = new Service { Current = KeyboardCodexProviderTests.Ready(clock) with { Primary = new(10080, "7天", 95, reset) } };
        var vm = new KeyboardCodexUsageViewModel(() => service, InlineDispatcher.Instance, _ => { }, clock.GetUtcNow);
        await vm.RefreshAsync();
        vm.PreviewWindows.Single().ResetsAt.Offset.Should().Be(TimeZoneInfo.Local.GetUtcOffset(reset));
        service.Writes.Should().BeEmpty();
    }

    [Fact]
    public async Task Separate_Codex_tab_binds_readonly_preview_and_action_status_without_last_success_line()
    {
        var clock = new ManualTimeProvider();
        var service = new Service { Current = KeyboardCodexProviderTests.Ready(clock) with { OpenActionStatus = "Requested" } };
        var vm = new KeyboardCodexUsageViewModel(() => service, InlineDispatcher.Instance, _ => { }, clock.GetUtcNow);
        await vm.RefreshAsync();
        vm.PreviewWindows.Should().NotBeEmpty(); vm.OpenActionStatus.Should().Contain("已提交打开请求");
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var panel = new App.Views.CaseControlPanelView();
                var tabs = Logical<System.Windows.Controls.TabItem>(panel).ToArray();
                var codex = tabs.Single(t => Equals(t.Header, "Codex"));
                Array.IndexOf(tabs, codex).Should().Be(Array.FindIndex(tabs, t => Equals(t.Header, "显示与动画")) + 1);
                codex.Content.Should().BeOfType<App.Views.CodexUsageView>();
                System.Windows.Data.BindingOperations.GetBinding((App.Views.CodexUsageView)codex.Content,
                    System.Windows.FrameworkElement.DataContextProperty)!.Path.Path.Should().Be("KeyboardAnimation.Codex");
                Logical<App.Views.CodexUsageView>(tabs.Single(t => Equals(t.Header, "显示与动画"))).Should().BeEmpty();
                var view = new App.Views.CodexUsageView { DataContext = vm };
                view.Measure(new(360, 1200)); view.Arrange(new(0, 0, 360, 1200)); view.UpdateLayout();
                var items = Logical<System.Windows.Controls.ItemsControl>(view).Single();
                items.Items.Count.Should().Be(vm.PreviewWindows.Count);
                System.Windows.Data.BindingOperations.GetBinding(items, System.Windows.Controls.ItemsControl.ItemsSourceProperty)!.Mode.Should().Be(System.Windows.Data.BindingMode.OneWay);
                var texts = Logical<System.Windows.Controls.TextBlock>(view).Select(t => t.Text).ToArray();
                texts.Should().Contain(vm.OpenActionStatus);
                texts.Should().NotContain(t => t.Contains("LastSuccess", StringComparison.Ordinal) || t.Contains("上次成功", StringComparison.Ordinal));
                Logical<System.Windows.Controls.Button>(view).Select(b => b.Command).Should().BeEquivalentTo([vm.ShowCommand, vm.StopCommand, vm.ReadCommand]);
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        thread.Join(TimeSpan.FromSeconds(30)).Should().BeTrue(); failure.Should().BeNull();
        service.Writes.Should().BeEmpty(); service.Reads.Should().Be(1);
    }

    private static IEnumerable<T> Logical<T>(System.Windows.DependencyObject root) where T : System.Windows.DependencyObject
    {
        foreach (var child in System.Windows.LogicalTreeHelper.GetChildren(root).OfType<System.Windows.DependencyObject>())
        {
            if (child is T value) yield return value;
            foreach (var nested in Logical<T>(child)) yield return nested;
        }
    }

    [Fact]
    public async Task Additive_contract_round_trips_sanitized_status_and_explicit_preference_over_in_memory_rpc()
    {
        var (serverStream, clientStream) = Nerdbank.Streams.FullDuplexStream.CreatePair();
        using var server = new JsonRpc(new HeaderDelimitedMessageHandler(serverStream, serverStream, PipeServer.CreateFormatter()));
        var service = new Service { Current = KeyboardCodexProviderTests.Ready(new ManualTimeProvider()) };
        server.AddLocalRpcTarget(service); server.StartListening();
        using var client = new JsonRpc(new HeaderDelimitedMessageHandler(clientStream, clientStream, PipeServer.CreateFormatter()));
        var proxy = client.Attach<IKeyboardDisplayService>(); client.StartListening();
        (await proxy.GetKeyboardCodexUsageAsync()).Should().Be(service.Current);
        service.Writes.Should().BeEmpty();
        (await proxy.SetKeyboardCodexUsageEnabledAsync(false)).Enabled.Should().BeFalse();
        service.Writes.Should().Equal(false);
    }

    [Fact]
    public async Task Missing_remote_method_is_gracefully_unsupported_without_any_write()
    {
        var (serverStream, clientStream) = Nerdbank.Streams.FullDuplexStream.CreatePair();
        using var server = new JsonRpc(new HeaderDelimitedMessageHandler(serverStream, serverStream, PipeServer.CreateFormatter()));
        server.AddLocalRpcTarget(new OldService()); server.StartListening();
        using var client = new JsonRpc(new HeaderDelimitedMessageHandler(clientStream, clientStream, PipeServer.CreateFormatter()));
        var proxy = client.Attach<IKeyboardDisplayService>(); client.StartListening();
        var requests = 0;
        var vm = new KeyboardCodexUsageViewModel(() => { requests++; return proxy; }, InlineDispatcher.Instance, _ => { });
        await vm.RefreshAsync(); await vm.RefreshAsync(); await vm.SetEnabledAsync(true);
        requests.Should().Be(1); vm.Message.Should().Contain("不支持"); vm.CanShow.Should().BeFalse();
    }

    [Fact]
    public async Task Read_and_refresh_never_write_and_only_explicit_show_and_stop_change_preference()
    {
        var service = new Service(); var vm = Vm(service);
        service.Reads.Should().Be(0); service.Writes.Should().BeEmpty();
        await vm.RefreshAsync(); await vm.RefreshAsync();
        service.Writes.Should().BeEmpty(); vm.CanShow.Should().BeTrue(); vm.CanStop.Should().BeFalse();
        await vm.SetEnabledAsync(true);
        service.Writes.Should().Equal(true); vm.CanStop.Should().BeTrue();
        await vm.RefreshAsync(); await vm.SetEnabledAsync(false);
        service.Writes.Should().Equal(true, false); vm.CanShow.Should().BeTrue();
    }

    [Fact]
    public async Task Old_host_default_not_supported_is_read_only_and_not_reprobed_until_new_connection()
    {
        var service = new Service { Old = true }; var vm = Vm(service);
        await vm.RefreshAsync(); await vm.RefreshAsync(); await vm.SetEnabledAsync(true);
        service.Reads.Should().Be(1); service.Writes.Should().BeEmpty();
        vm.Message.Should().Contain("不支持"); vm.CanShow.Should().BeFalse();
        vm.SetConnected(false); vm.SetConnected(true); await vm.RefreshAsync(); service.Reads.Should().Be(2);
        IKeyboardDisplayService old = new OldService();
        await ((Func<Task>)(async () => await old.GetKeyboardCodexUsageAsync())).Should().ThrowAsync<NotSupportedException>();
        await ((Func<Task>)(async () => await old.SetKeyboardCodexUsageEnabledAsync(true))).Should().ThrowAsync<NotSupportedException>();
    }

    [Fact]
    public async Task Lost_write_answer_is_not_resent_and_readback_does_not_claim_success()
    {
        var service = new Service { LostWrite = true }; var vm = Vm(service);
        await vm.RefreshAsync(); await vm.SetEnabledAsync(true);
        service.Writes.Should().Equal(true); service.Reads.Should().Be(2);
        vm.Status!.Enabled.Should().BeTrue(); vm.Message.Should().Contain("未确认").And.Contain("未自动重发");
        await vm.RefreshAsync(); service.Writes.Should().Equal(true);
    }

    [Fact]
    public async Task Late_reply_from_previous_connection_cannot_replace_new_status_or_authorize_write()
    {
        var service = new Service(); var vm = Vm(service);
        var late = new TaskCompletionSource<KeyboardCodexUsageDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.NextRead = () => late.Task;
        var reading = vm.RefreshAsync();
        vm.SetConnected(false); vm.CanShow.Should().BeFalse();
        vm.SetConnected(true); await vm.RefreshAsync();
        late.SetResult(service.Current with { Enabled = true }); await reading;
        vm.Status!.Enabled.Should().BeFalse(); service.Writes.Should().BeEmpty();
    }

    [Fact]
    public async Task Expired_ui_observation_cannot_write_and_past_reset_numbers_are_not_shown_as_current()
    {
        var clock = new ManualTimeProvider(); var service = new Service();
        service.Current = KeyboardCodexProviderTests.Ready(clock) with { Primary = new(300, "5小时", 42, clock.GetUtcNow().AddSeconds(-1)) };
        var vm = new KeyboardCodexUsageViewModel(() => service, InlineDispatcher.Instance, _ => { }, clock.GetUtcNow);
        await vm.RefreshAsync(); vm.Summary.Should().Contain("旧数值失效").And.NotContain("42%");
        clock.Advance(TimeSpan.FromSeconds(11));
        await vm.SetEnabledAsync(false); service.Writes.Should().BeEmpty(); vm.Status.Should().BeNull();
    }

    [Fact]
    public async Task Integration_uses_existing_keyboard_animation_area_and_does_not_require_newer_animation_endpoint()
    {
        var service = new Service();
        var animation = new KeyboardAnimationViewModel(() => service, InlineDispatcher.Instance, _ => { });
        await animation.RefreshAsync();
        await animation.Codex.RefreshAsync();
        animation.IsUnsupported.Should().BeTrue();
        animation.Codex.Status.Should().NotBeNull(); service.Writes.Should().BeEmpty();
        animation.SetConnected(false); animation.Codex.Status.Should().BeNull(); animation.Codex.CanShow.Should().BeFalse();
    }

    private static KeyboardCodexUsageViewModel Vm(Service service) => new(() => service, InlineDispatcher.Instance, _ => { }, timeout: TimeSpan.FromMilliseconds(80));

    private sealed class Service : IKeyboardDisplayService
    {
        public int Reads { get; private set; }
        public List<bool> Writes { get; } = [];
        public bool Old { get; init; }
        public bool LostWrite { get; init; }
        public Func<Task<KeyboardCodexUsageDto>>? NextRead { get; set; }
        public KeyboardCodexUsageDto Current { get; set; } = new(false, "Off", null, null, null, null, null) { IndependentPageRouting = true };
        public Task<KeyboardCodexUsageDto> GetKeyboardCodexUsageAsync(CancellationToken ct = default)
        {
            Reads++; if (Old) throw new NotSupportedException();
            var next = NextRead; NextRead = null;
            return next is null ? Task.FromResult(Current) : next();
        }
        public Task<KeyboardCodexUsageDto> SetKeyboardCodexUsageEnabledAsync(bool enabled, CancellationToken ct = default)
        {
            Writes.Add(enabled); Current = Current with { Enabled = enabled, State = enabled ? "Loading" : "Off" };
            return LostWrite ? new TaskCompletionSource<KeyboardCodexUsageDto>().Task : Task.FromResult(Current);
        }
        public Task<KeyboardDisplayStatusDto> GetKeyboardDisplayStatusAsync(CancellationToken ct = default) => throw new InvalidOperationException("No device status");
        public Task SetKeyboardDisplayEnabledAsync(bool enabled, CancellationToken ct = default) => throw new InvalidOperationException("No device writes");
    }

    private sealed class OldService : IKeyboardDisplayService
    {
        public Task<KeyboardDisplayStatusDto> GetKeyboardDisplayStatusAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task SetKeyboardDisplayEnabledAsync(bool enabled, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
