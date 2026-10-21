using System.Reflection;
using StreamJsonRpc;
using UnifiedRgb.App;
using UnifiedRgb.App.Ipc;
using UnifiedRgb.App.Mvvm;
using UnifiedRgb.App.ViewModels;
using UnifiedRgb.Contracts;

namespace UnifiedRgb.Host.Tests;

/// <summary>
/// Audit R8: conservative dispatcher-exception policy and the controlled UI fault state. Fake callbacks and an unstarted
/// PipeClient on a unique unused pipe only; no window, dialog, Host or hardware.
/// </summary>
public sealed class UiExceptionPolicyAuditTests
{
    public static TheoryData<string, Exception?> UnsafeCases => new()
    {
        { "null", null },
        { "io", new IOException("disk") },
        { "invalid-operation", new InvalidOperationException("Collection was modified") },
        { "key-not-found", new KeyNotFoundException() },
        { "cancelled", new OperationCanceledException() },
        { "host-rejected", new RemoteInvocationException("bad request", 1, "data") },
        { "access-blocked", new HostAccessBlockedException() },
        { "aggregate-mixed", new AggregateException(new HostNotConnectedException(), new IOException("x")) },
        { "aggregate-empty", new AggregateException() },
        { "aggregate-too-many", new AggregateException(Enumerable.Range(0, 17).Select(_ => (Exception)new HostNotConnectedException())) },
        { "invocation-wrapping-unsafe", new TargetInvocationException(new ArgumentException("x")) },
    };

    public static TheoryData<string, Exception> RecoverableCases => new()
    {
        { "not-connected", new HostNotConnectedException() },
        { "connection-lost", new ConnectionLostException() },
        { "invocation-wrapped", new TargetInvocationException(new ConnectionLostException()) },
        { "aggregate-all-connection", new AggregateException(new HostNotConnectedException(), new ConnectionLostException()) },
    };

    [Theory]
    [MemberData(nameof(UnsafeCases))]
    public void Unknown_and_ambiguous_exceptions_are_unsafe(string name, Exception? ex)
    {
        UiExceptionPolicy.Classify(ex).Should().Be(UiExceptionDisposition.Unsafe, name);
    }

    [Theory]
    [MemberData(nameof(RecoverableCases))]
    public void Only_the_host_disconnect_family_is_recoverable(string name, Exception ex)
    {
        UiExceptionPolicy.Classify(ex).Should().Be(UiExceptionDisposition.Recoverable, name);
    }

    [Fact]
    public void Unwrapping_is_depth_bounded()
    {
        Exception shallow = new ConnectionLostException();
        for (var i = 0; i < 8; i++)
        {
            shallow = new TargetInvocationException(shallow);
        }

        Exception deep = shallow;
        for (var i = 0; i < 12; i++)
        {
            deep = new TargetInvocationException(deep);
        }

        UiExceptionPolicy.Classify(shallow).Should().Be(UiExceptionDisposition.Recoverable);
        UiExceptionPolicy.Classify(deep).Should().Be(UiExceptionDisposition.Unsafe);
    }

    [Fact]
    public void Recoverable_exception_is_reported_and_does_not_fault()
    {
        var fake = new Fakes();
        var coordinator = fake.Create();

        coordinator.Handle(new HostNotConnectedException()).Should().BeTrue();

        fake.Events.Should().Equal("log:Warning", "report:HostNotConnectedException");
        coordinator.IsFaulted.Should().BeFalse();
        coordinator.FaultText.Should().BeNull();
    }

    [Fact]
    public void First_unsafe_exception_blocks_host_before_showing_the_fault_exactly_once()
    {
        var fake = new Fakes();
        var coordinator = fake.Create();

        coordinator.Handle(new KeyNotFoundException()).Should().BeTrue();
        coordinator.Handle(new IOException("again")).Should().BeTrue();
        coordinator.Handle(new HostNotConnectedException()).Should().BeTrue();

        fake.Events.Should().Equal(
            "block", "log:Fatal", "show",
            "log:Error", "log:Error");
        coordinator.IsFaulted.Should().BeTrue();
        coordinator.FaultText.Should().StartWith(UiExceptionCoordinator.FaultMessage).And.EndWith("（错误类型：KeyNotFoundException）");
        fake.Blocked.Should().Be(coordinator.FaultText);
        fake.Shown.Should().Be(coordinator.FaultText);
    }

    [Fact]
    public void Exception_storms_are_logged_only_up_to_the_bounds()
    {
        var fake = new Fakes();
        var coordinator = fake.Create();

        for (var i = 0; i < 120; i++)
        {
            coordinator.Handle(new ConnectionLostException());
        }

        fake.Count("log:Warning").Should().Be(UiExceptionCoordinator.MaxLoggedRecoverable);
        fake.Count("report:ConnectionLostException").Should().Be(120, "the view still shows the latest connection error");
        coordinator.SuppressedCount.Should().Be(70);

        coordinator.Handle(new InvalidOperationException());
        for (var i = 0; i < 200; i++)
        {
            coordinator.Handle(new InvalidOperationException());
        }

        fake.Count("show").Should().Be(1);
        fake.Count("log:Fatal").Should().Be(1);
        fake.Count("log:Error").Should().Be(UiExceptionCoordinator.MaxLoggedAfterFault);
        coordinator.SuppressedCount.Should().Be(70 + 150);
    }

    [Fact]
    public void Throwing_callbacks_never_escape_and_do_not_skip_later_steps()
    {
        var shown = 0;
        var coordinator = new UiExceptionCoordinator(
            (_, _, _) => throw new InvalidOperationException("log broken"),
            _ => throw new InvalidOperationException("block broken"),
            _ => { shown++; throw new InvalidOperationException("show broken"); },
            _ => throw new InvalidOperationException("report broken"),
            () => throw new InvalidOperationException("shutdown flag broken"));

        coordinator.Invoking(c => c.Handle(new HostNotConnectedException())).Should().NotThrow();
        coordinator.IsFaulted.Should().BeFalse();
        coordinator.Invoking(c => c.Handle(new FormatException())).Should().NotThrow();
        coordinator.IsFaulted.Should().BeTrue("an unreadable shutdown flag is treated as running");
        shown.Should().Be(1, "a failed block must not prevent the visible fault state");
        coordinator.Handle(null).Should().BeTrue();
        shown.Should().Be(1);
    }

    [Fact]
    public void Re_entrant_exceptions_from_callbacks_are_counted_not_recursed()
    {
        UiExceptionCoordinator? coordinator = null;
        var shown = 0;
        var blocked = 0;
        coordinator = new UiExceptionCoordinator(
            (_, _, _) => { },
            _ => { blocked++; coordinator!.Handle(new InvalidOperationException("from block")); },
            _ => { shown++; coordinator!.Handle(new InvalidOperationException("from show")); },
            _ => { },
            () => false);

        coordinator.Handle(new ArgumentException()).Should().BeTrue();

        (blocked, shown).Should().Be((1, 1));
        coordinator.SuppressedCount.Should().Be(2);
    }

    [Fact]
    public void Exceptions_during_shutdown_are_only_logged()
    {
        var fake = new Fakes { ShuttingDown = true };
        var coordinator = fake.Create();

        coordinator.Handle(new KeyNotFoundException()).Should().BeTrue();

        fake.Events.Should().Equal("log:Warning");
        coordinator.IsFaulted.Should().BeFalse();
    }

    [Fact]
    public async Task Unsafe_fault_blocks_the_client_and_freezes_the_view_without_losing_page_state()
    {
        await using var client = new PipeClient("UnifiedRgb-audit-unused-" + Guid.NewGuid().ToString("N"), PipeTrust.ElevatedOwnerOrCurrentUser);
        var main = new MainViewModel(client, InlineDispatcher.Instance, new AppLogicTests.MemoryAutostart(), "app.exe", Path.GetTempPath(), Path.GetTempPath());
        var screens = main.Screens;
        var coordinator = new UiExceptionCoordinator((_, _, _) => { }, client.BlockForUiFault, main.EnterUiFault, main.ReportError, () => false);

        coordinator.Handle(new HostNotConnectedException());
        main.Error.Should().Be("HostNotConnectedException: 后台未连接。");
        main.IsUiFaulted.Should().BeFalse();
        client.IsBlockedForUiFault.Should().BeFalse();

        coordinator.Handle(new InvalidOperationException("Collection was modified"));

        main.IsUiFaulted.Should().BeTrue();
        main.Error.Should().Be(coordinator.FaultText);
        main.ConnectionText.Should().Be(MainViewModel.UiFaultConnectionText);
        client.Invoking(c => c.Require()).Should().Throw<HostAccessBlockedException>().WithMessage(coordinator.FaultText!);
        main.Screens.Should().BeSameAs(screens, "page view models and their drafts stay in memory");

        main.ReportError(new IOException("later"));
        main.DismissErrorCommand.Execute(null);
        main.Error.Should().Be(coordinator.FaultText, "the fault notice cannot be dismissed or overwritten");
        main.ConnectionText.Should().Be(MainViewModel.UiFaultConnectionText);

        main.EnterUiFault("second fault text");
        main.Error.Should().Be(coordinator.FaultText, "first fault wins");
    }

    [Theory]
    [InlineData(null, "后台未连接 · 自动重试中")]
    [InlineData("  ", "后台未连接 · 自动重试中")]
    [InlineData("连接后台超时（2 秒内无响应），后台可能未运行", "连接后台超时（2 秒内无响应），后台可能未运行 · 自动重试中")]
    public void Disconnected_sidebar_text_keeps_the_retry_notice(string? lastError, string expected)
    {
        MainViewModel.DisconnectedText(lastError).Should().Be(expected);
    }

    private sealed class Fakes
    {
        public List<string> Events { get; } = [];

        public string? Blocked { get; private set; }

        public string? Shown { get; private set; }

        public bool ShuttingDown { get; init; }

        public int Count(string name) => Events.Count(e => e == name);

        public UiExceptionCoordinator Create() => new(
            (level, _, _) => Events.Add("log:" + level),
            reason => { Events.Add("block"); Blocked = reason; },
            text => { Events.Add("show"); Shown = text; },
            ex => Events.Add("report:" + ex.GetType().Name),
            () => ShuttingDown);
    }
}
