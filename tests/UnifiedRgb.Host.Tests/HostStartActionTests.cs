using System.ComponentModel;
using System.Diagnostics;
using UnifiedRgb.App.Ipc;
using UnifiedRgb.App.Mvvm;
using UnifiedRgb.App.ViewModels;
using UnifiedRgb.Contracts;

namespace UnifiedRgb.Host.Tests;

/// <summary>
/// The sidebar 启动后台 / 重新连接后台 action against callable fakes only: the task request, the connection wait and the
/// connection are scripted delegates writing one trace, and time moves only through <see cref="ManualTimeProvider"/>.
/// HostStartup's real task request is never passed in; the one MainViewModel is built on a pipe client that is never started.
/// </summary>
public sealed class HostStartActionTests
{
    private const string Requesting = "正在请求启动后台任务…";
    private const string Requested = "已请求启动后台任务";
    private const string Unknown = "启动请求未答复，结果未知，未再次请求";
    private const string Unanswered = "上一次启动请求仍未答复，未再次请求";
    private const string Waiting = "，正在等待后台连接…";
    private const string NoNewConnection = "；未等到新的后台连接（最多 30 秒），不会自动再次请求（连接仍在自动重试）。";
    private const string CameAndWent = "；后台连上后又断开，未再次请求。";
    private const string Connected = "后台已连接；设备状态以界面读取为准。";
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(1);

    [Fact]
    public async Task A_connected_Host_is_left_alone_and_one_that_connects_within_the_grace_is_not_started()
    {
        var rig = new Rig();
        rig.Connection.Connect();
        using var action = rig.Create();

        action.IsOffered.Should().BeFalse();
        action.StartCommand.CanExecute(null).Should().BeFalse();
        await action.StartCommand.ExecuteAsync().WaitAsync(Patience);
        rig.Snapshot().Should().Equal("connected #1");

        rig.Connection.Disconnect();
        action.IsOffered.Should().BeTrue();
        action.Label.Should().Be("重新连接后台");
        rig.Waits.Enqueue((_, _) => { rig.Connection.Connect(); return Task.FromResult(true); });
        await action.StartCommand.ExecuteAsync().WaitAsync(Patience);

        rig.Snapshot().Should().Equal("connected #1", "disconnected", "wait 2s", "connected #2");
        action.Status.Should().BeEmpty();
        action.IsOffered.Should().BeFalse();
        rig.Errors.Should().BeEmpty();
    }

    [Fact]
    public async Task A_disconnected_Host_gets_one_task_request_and_the_new_connection_is_reported()
    {
        var rig = new Rig();
        using var action = rig.Create();
        action.Label.Should().Be("启动后台");
        action.IsOffered.Should().BeTrue();
        var reconnect = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Waits.Enqueue((_, _) => Task.FromResult(false));
        rig.Waits.Enqueue((_, _) => reconnect.Task);

        var run = action.StartCommand.ExecuteAsync();
        await Until(() => rig.Snapshot().Length == 3);
        rig.Snapshot().Should().Equal("wait 2s", "start", "wait 30s");
        action.Status.Should().Be(Requested + Waiting);
        action.StartCommand.CanExecute(null).Should().BeFalse();

        rig.Connection.Connect();
        reconnect.SetResult(true);
        await run.WaitAsync(Patience);

        action.Status.Should().Be(Connected);
        action.IsOffered.Should().BeFalse();
        action.Label.Should().Be("重新连接后台");
        rig.Snapshot().Should().Equal("wait 2s", "start", "wait 30s", "connected #1");
        rig.Tokens.Should().ContainSingle().Which.Should().Match<CancellationToken>(t => t.CanBeCanceled && !t.IsCancellationRequested);
        rig.Errors.Should().BeEmpty();
    }

    [Fact]
    public async Task Clicks_during_a_run_including_the_launch_request_never_request_the_task_again()
    {
        var rig = new Rig();
        using var action = rig.Create();
        var answer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Starts.Enqueue(_ => answer.Task);

        var launch = action.StartCommand.ExecuteAsync(); // App's launch request takes the same gate
        await Until(() => rig.Snapshot().Contains("start"));
        action.StartCommand.CanExecute(null).Should().BeFalse();
        for (var i = 0; i < 3; i++)
        {
            action.StartCommand.Execute(null);
            await action.StartCommand.ExecuteAsync().WaitAsync(Patience);
        }

        action.Status.Should().Be(Requesting);
        answer.SetResult();
        await launch.WaitAsync(Patience);

        rig.Snapshot().Should().Equal("wait 2s", "start", "wait 30s");
        action.Status.Should().Be(Requested + NoNewConnection);
        action.StartCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public async Task A_request_that_ignores_cancellation_is_unknown_and_holds_back_every_later_request_until_it_ends()
    {
        var rig = new Rig();
        using var action = rig.Create();
        var answer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Starts.Enqueue(_ => answer.Task); // never looks at its token

        var first = action.StartCommand.ExecuteAsync();
        await Until(() => rig.Snapshot().Contains("start") && rig.Time.ActiveTimers == 1);
        rig.Time.Advance(HostStartViewModel.AnswerTimeout - Tick);
        action.Status.Should().Be(Requesting);
        first.IsCompleted.Should().BeFalse();
        rig.Time.Advance(Tick);
        await first.WaitAsync(Patience);
        action.Status.Should().Be(Unknown + NoNewConnection);
        rig.Snapshot().Should().Equal("wait 2s", "start", "wait 30s");

        await action.StartCommand.ExecuteAsync().WaitAsync(Patience); // its request is still unanswered: only waits
        action.Status.Should().Be(Unanswered + NoNewConnection);

        rig.Connection.Connecting();
        rig.Connection.Disconnect();
        rig.Time.Advance(TimeSpan.FromMinutes(10));
        answer.SetResult(); // the late answer shows nothing and retries nothing
        await Task.Delay(50);
        action.Status.Should().Be(Unanswered + NoNewConnection);
        rig.Snapshot().Should().Equal("wait 2s", "start", "wait 30s", "wait 30s", "connecting", "disconnected");

        await action.StartCommand.ExecuteAsync().WaitAsync(Patience); // a new explicit click after the answer: one new request
        rig.Snapshot().Should().Equal("wait 2s", "start", "wait 30s", "wait 30s", "connecting", "disconnected", "wait 2s", "start", "wait 30s");
        action.Status.Should().Be(Requested + NoNewConnection);
        rig.Errors.Should().BeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_unknown_answer_waits_for_the_reconnect_and_is_not_retried(bool foreignCancellation)
    {
        var rig = new Rig();
        using var action = rig.Create();
        rig.Starts.Enqueue(async _ =>
        {
            await Task.Yield();
            throw foreignCancellation ? new OperationCanceledException() : (Exception)new TimeoutException("请求启动后台超时；界面仍会自动重连，不会重复启动后台。");
        });
        var reconnect = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Waits.Enqueue((_, _) => Task.FromResult(false));
        rig.Waits.Enqueue((_, _) => reconnect.Task);

        var run = action.StartCommand.ExecuteAsync();
        await Until(() => rig.Snapshot().Length == 3);
        action.Status.Should().Be(Unknown + Waiting);
        rig.Connection.Connect();
        reconnect.SetResult(true);
        await run.WaitAsync(Patience);

        action.Status.Should().Be(Connected);
        rig.Snapshot().Should().Equal("wait 2s", "start", "wait 30s", "connected #1");
        rig.Errors.Should().BeEmpty();
    }

    [Fact]
    public async Task Both_waits_end_on_time_even_when_the_wait_ignores_its_timeout()
    {
        var rig = new Rig();
        using var action = rig.Create();
        rig.Waits.Enqueue((_, _) => new TaskCompletionSource<bool>().Task);
        rig.Waits.Enqueue((_, _) => new TaskCompletionSource<bool>().Task);

        var run = action.StartCommand.ExecuteAsync();
        await Until(() => rig.Time.ActiveTimers == 1);
        rig.Snapshot().Should().Equal("wait 2s");
        rig.Time.Advance(HostStartup.ConnectionGrace);
        await Until(() => rig.Snapshot().Length == 3 && rig.Time.ActiveTimers == 1);
        rig.Time.Advance(HostStartViewModel.ConnectWait - Tick);
        action.Status.Should().Be(Requested + Waiting);
        run.IsCompleted.Should().BeFalse();
        rig.Time.Advance(Tick);
        await run.WaitAsync(Patience);

        action.Status.Should().Be(Requested + NoNewConnection);
        rig.Snapshot().Should().Equal("wait 2s", "start", "wait 30s");
        action.StartCommand.CanExecute(null).Should().BeTrue();
        rig.Time.ActiveTimers.Should().Be(0);
    }

    [Fact]
    public async Task A_failed_request_is_shown_and_reported_once_and_never_retried()
    {
        var rig = new Rig();
        using var action = rig.Create();
        var failure = new IOException("无法启动后台任务（1）：错误: 系统找不到指定的文件。");
        rig.Starts.Enqueue(_ => Task.FromException(failure));

        await action.StartCommand.ExecuteAsync().WaitAsync(Patience);
        action.Status.Should().Be("后台启动请求失败，未重试：无法启动后台任务（1）：错误: 系统找不到指定的文件。");
        rig.Errors.Should().Equal(failure);

        rig.Connection.Connecting();
        rig.Connection.Disconnect();
        rig.Time.Advance(TimeSpan.FromMinutes(10));
        rig.Snapshot().Should().Equal("wait 2s", "start", "connecting", "disconnected");
        action.Status.Should().Be("后台启动请求失败，未重试：无法启动后台任务（1）：错误: 系统找不到指定的文件。");
        rig.Errors.Should().Equal(failure);
        action.StartCommand.CanExecute(null).Should().BeTrue(); // only another explicit click asks again
    }

    [Fact]
    public async Task A_connection_that_came_and_went_is_never_shown_as_current_and_an_older_line_is_withdrawn()
    {
        var rig = new Rig();
        rig.Connection.Connect();
        rig.Connection.Disconnect(); // the Host this window knew has stopped
        using var action = rig.Create();
        action.Label.Should().Be("重新连接后台");

        rig.Waits.Enqueue((_, _) => Task.FromResult(false));
        rig.Waits.Enqueue((_, _) => { rig.Connection.Connect(); rig.Connection.Disconnect(); return Task.FromResult(true); }); // a late "connected"
        await action.StartCommand.ExecuteAsync().WaitAsync(Patience);
        action.Status.Should().Be(Requested + CameAndWent);

        rig.Waits.Enqueue((_, _) => Task.FromResult(false));
        rig.Waits.Enqueue((_, _) => Task.FromResult(true)); // "connected" without a new connection
        await action.StartCommand.ExecuteAsync().WaitAsync(Patience);
        action.Status.Should().Be(Requested + NoNewConnection);

        rig.Connection.Connecting();
        action.Status.Should().Be(Requested + NoNewConnection, "a retry of the same connection changes nothing it said");
        rig.Connection.Connect();
        action.Status.Should().BeEmpty("outside a run, a line about an older connection is withdrawn");

        rig.Connection.Disconnect();
        rig.Waits.Enqueue((_, _) => Task.FromResult(false));
        rig.Waits.Enqueue((_, _) => { rig.Connection.Connect(); return Task.FromResult(true); });
        await action.StartCommand.ExecuteAsync().WaitAsync(Patience);
        action.Status.Should().Be(Connected);
        rig.Connection.Disconnect();
        action.Status.Should().BeEmpty("the connection it reported has ended");

        rig.Snapshot().Should().Equal(
            "connected #1", "disconnected",
            "wait 2s", "start", "wait 30s", "connected #2", "disconnected",
            "wait 2s", "start", "wait 30s",
            "connecting", "connected #3", "disconnected",
            "wait 2s", "start", "wait 30s", "connected #4", "disconnected");
        rig.Errors.Should().BeEmpty();
    }

    [Fact]
    public async Task Closing_the_app_cancels_the_pending_request_and_nothing_is_shown_or_requested_afterwards()
    {
        var rig = new Rig();
        var action = rig.Create();
        rig.Starts.Enqueue(ct => Task.Delay(Timeout.Infinite, ct));

        var run = action.StartCommand.ExecuteAsync();
        await Until(() => rig.Snapshot().Contains("start"));
        action.Dispose();
        await run.WaitAsync(Patience);

        rig.Tokens.Should().ContainSingle().Which.IsCancellationRequested.Should().BeTrue();
        action.IsOffered.Should().BeFalse();
        await action.StartCommand.ExecuteAsync().WaitAsync(Patience);
        rig.Connection.Connect();
        rig.Time.Advance(TimeSpan.FromMinutes(1));
        rig.Snapshot().Should().Equal("wait 2s", "start", "connected #1");
        action.Status.Should().Be(Requesting);
        rig.Errors.Should().BeEmpty();
        rig.Time.ActiveTimers.Should().Be(0);
    }

    [Fact]
    public async Task The_window_action_keeps_drafts_pages_and_configuration_mode_and_uses_no_Host_proxy()
    {
        var rig = new Rig();
        await using var client = new PipeClient("UnifiedRgb-audit-unused-" + Guid.NewGuid().ToString("N"), PipeTrust.ElevatedOwnerOrCurrentUser);
        using var main = new MainViewModel(client, InlineDispatcher.Instance, new AppLogicTests.MemoryAutostart(), "app.exe", UnusedDirectory(), UnusedDirectory(),
            startHostTask: rig.Start, waitHostConnected: rig.Wait, hostStartTime: rig.Time);
        main.Studio.Draft.Name = "未保存的草稿";
        main.WorkbenchSourceIndex = 1;
        main.Studio.Page = 3;
        var draft = (main.Studio.Draft.Name, main.Studio.Draft.Dirty, main.Studio.Draft.Revision);
        draft.Dirty.Should().BeTrue();
        var peripherals = main.Studio.ControlPanel.Peripherals!;
        var configuration = (peripherals.Message, peripherals.StateTitle, peripherals.HasStatus, peripherals.CanEnter, peripherals.CanExit);
        var changes = new List<string>();
        foreach (var (name, page) in Pages(main))
        {
            page.PropertyChanged += (_, e) => { lock (changes) changes.Add(name + "." + e.PropertyName); };
        }

        main.HostStart.IsOffered.Should().BeTrue();
        main.HostStart.Label.Should().Be("启动后台");
        await main.HostStart.StartCommand.ExecuteAsync().WaitAsync(Patience);

        rig.Snapshot().Should().Equal("wait 2s", "start", "wait 30s");
        main.HostStart.Status.Should().Be(Requested + NoNewConnection);
        changes.Should().BeEmpty();
        (main.Studio.Draft.Name, main.Studio.Draft.Dirty, main.Studio.Draft.Revision).Should().Be(draft);
        (main.WorkbenchSourceIndex, main.Studio.Page).Should().Be((1, 3));
        (peripherals.Message, peripherals.StateTitle, peripherals.HasStatus, peripherals.CanEnter, peripherals.CanExit).Should().Be(configuration);
        main.Error.Should().BeEmpty("no Host proxy was asked for (the client would refuse with HostNotConnectedException)");
        (client.State, client.Connections).Should().Be((ConnectionState.Disconnected, 0));
    }

    [Fact]
    public async Task A_blocked_window_or_one_without_the_App_start_delegate_offers_and_requests_nothing()
    {
        var rig = new Rig();
        await using var client = new PipeClient("UnifiedRgb-audit-unused-" + Guid.NewGuid().ToString("N"), PipeTrust.ElevatedOwnerOrCurrentUser);
        using var faulted = new MainViewModel(client, InlineDispatcher.Instance, new AppLogicTests.MemoryAutostart(), "app.exe", UnusedDirectory(), UnusedDirectory(),
            startHostTask: rig.Start, waitHostConnected: rig.Wait, hostStartTime: rig.Time);
        using var withoutDelegate = new MainViewModel(client, InlineDispatcher.Instance, new AppLogicTests.MemoryAutostart(), "app.exe", UnusedDirectory(), UnusedDirectory(),
            waitHostConnected: rig.Wait, hostStartTime: rig.Time);
        var offered = new List<bool>();
        faulted.HostStart.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(HostStartViewModel.IsOffered)) offered.Add(faulted.HostStart.IsOffered); };
        faulted.HostStart.IsOffered.Should().BeTrue();

        faulted.EnterUiFault("界面状态可能不一致");
        offered.Should().Equal(false);
        foreach (var main in new[] { faulted, withoutDelegate })
        {
            main.HostStart.IsOffered.Should().BeFalse();
            main.HostStart.StartCommand.CanExecute(null).Should().BeFalse();
            await main.HostStart.StartCommand.ExecuteAsync().WaitAsync(Patience);
            main.HostStart.Status.Should().BeEmpty();
        }

        rig.Snapshot().Should().BeEmpty();
    }

    [Fact]
    public async Task No_proxy_is_requested_while_the_action_runs_through_every_outcome()
    {
        var rig = new Rig();
        using var action = rig.Create();
        rig.Starts.Enqueue(_ => Task.FromException(new IOException("任务被拒绝")));
        rig.Starts.Enqueue(_ => Task.FromException(new TimeoutException()));
        rig.Waits.Enqueue((_, _) => Task.FromResult(false)); // grace of the failed request
        rig.Waits.Enqueue((_, _) => Task.FromResult(false)); // grace of the unknown one
        rig.Waits.Enqueue((_, _) => Task.FromResult(false)); // its reconnect wait
        rig.Waits.Enqueue((_, _) => Task.FromResult(false)); // grace of the answered one
        rig.Waits.Enqueue((_, _) => { rig.Connection.Connect(); return Task.FromResult(true); });

        await action.StartCommand.ExecuteAsync().WaitAsync(Patience);
        await action.StartCommand.ExecuteAsync().WaitAsync(Patience);
        await action.StartCommand.ExecuteAsync().WaitAsync(Patience);
        rig.Connection.Disconnect();
        await action.StartCommand.ExecuteAsync().WaitAsync(Patience);

        rig.Snapshot().Should().Equal(
            "wait 2s", "start",
            "wait 2s", "start", "wait 30s",
            "wait 2s", "start", "wait 30s", "connected #1", "disconnected",
            "wait 2s", "start", "wait 30s");
        rig.Snapshot().Should().NotContain(e => e.StartsWith("UNEXPECTED", StringComparison.Ordinal));

        // positive control: the same fake does record a proxy request
        rig.Connection.Invoking(c => c.Require()).Should().Throw<InvalidOperationException>();
        rig.Snapshot().Should().EndWith("UNEXPECTED Require");
    }

    private static string UnusedDirectory() => Path.Combine(Path.GetTempPath(), "UnifiedRgb-hoststart-unused-" + Guid.NewGuid().ToString("N"));

    private static IEnumerable<(string Name, INotifyPropertyChanged Page)> Pages(MainViewModel main) =>
    [
        ("Main", main), ("Studio", main.Studio), ("Draft", main.Studio.Draft), ("ControlPanel", main.Studio.ControlPanel),
        ("Peripherals", main.Studio.ControlPanel.Peripherals!), ("Screens", main.Screens), ("Packages", main.Packages),
        ("Profiles", main.Profiles), ("Scene", main.Scene), ("Devices", main.Devices), ("Effects", main.Effects),
        ("Settings", main.Settings), ("Diagnostics", main.Diagnostics), ("Bringup", main.Bringup),
    ];

    private static async Task Until(Func<bool> condition)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.Elapsed > Patience) throw new TimeoutException("The awaited state was not reached.");
            await Task.Delay(5);
        }
    }

    /// <summary>Scripted task request, connection wait and connection sharing one trace; unscripted calls answer at once.</summary>
    private sealed class Rig
    {
        private readonly List<string> _trace = [];

        public Rig() => Connection = new HostStartConnectionFake(Record);

        public HostStartConnectionFake Connection { get; }

        public ManualTimeProvider Time { get; } = new();

        public List<Exception> Errors { get; } = [];

        public List<CancellationToken> Tokens { get; } = [];

        /// <summary>Next task request answers; unscripted: answered at once.</summary>
        public Queue<Func<CancellationToken, Task>> Starts { get; } = new();

        /// <summary>Next connection waits; unscripted: whether the fake is connected right now.</summary>
        public Queue<Func<TimeSpan, CancellationToken, Task<bool>>> Waits { get; } = new();

        public HostStartViewModel Create(Func<bool>? blocked = null) =>
            new(Connection, InlineDispatcher.Instance, Start, Wait, ex => { lock (Errors) Errors.Add(ex); }, blocked, Time);

        public Task Start(CancellationToken ct)
        {
            Record("start");
            lock (Tokens) Tokens.Add(ct);
            return Starts.TryDequeue(out var start) ? start(ct) : Task.CompletedTask;
        }

        public Task<bool> Wait(TimeSpan timeout, CancellationToken ct)
        {
            Record($"wait {timeout.TotalSeconds:0}s");
            return Waits.TryDequeue(out var wait) ? wait(timeout, ct) : Task.FromResult(Connection.State == ConnectionState.Connected);
        }

        public string[] Snapshot()
        {
            lock (_trace) return [.. _trace];
        }

        private void Record(string entry)
        {
            lock (_trace) _trace.Add(entry);
        }
    }
}

/// <summary>Connection identity and state only; any proxy request is recorded as unexpected and refused.</summary>
internal sealed class HostStartConnectionFake(Action<string> record) : IHostConnection
{
    public event EventHandler<ConnectionState>? StateChanged;

    public ConnectionState State { get; private set; } = ConnectionState.Disconnected;

    public int Connections { get; private set; }

    public IRgbService Require()
    {
        record("UNEXPECTED Require");
        throw new InvalidOperationException("The start action must not use a Host proxy.");
    }

    public void Connect()
    {
        Connections++;
        Set(ConnectionState.Connected, $"connected #{Connections}");
    }

    public void Connecting() => Set(ConnectionState.Connecting, "connecting");

    public void Disconnect() => Set(ConnectionState.Disconnected, "disconnected");

    private void Set(ConnectionState state, string entry)
    {
        record(entry);
        State = state;
        StateChanged?.Invoke(this, state);
    }
}
