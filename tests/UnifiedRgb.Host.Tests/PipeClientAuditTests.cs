using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using UnifiedRgb.App.Ipc;
using UnifiedRgb.Contracts;

namespace UnifiedRgb.Host.Tests;

/// <summary>
/// Audit B.3: bounded PipeClient diagnostics, strict trust, gated Require* accessors and a monotonic WaitConnectedAsync.
/// Every case uses a unique local pipe name and a minimal fake server — never the production pipe or a hardware Host.
/// </summary>
public sealed class PipeClientAuditTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(15);

    private static string UniquePipe() => "UnifiedRgb-audit-" + Guid.NewGuid().ToString("N");

    [Fact]
    public async Task No_server_reports_a_bounded_connect_timeout()
    {
        await using var client = new PipeClient(UniquePipe(), PipeTrust.ElevatedOwnerOrCurrentUser);
        client.Start();

        await WaitForAsync(() => client.LastFailure == HostConnectionFailure.ConnectTimeout);
        client.LastError.Should().Be("连接后台超时（2 秒内无响应），后台可能未运行");
        client.State.Should().Be(ConnectionState.Disconnected);
    }

    [Fact]
    public async Task Pipe_that_denies_the_current_user_reports_access_denied()
    {
        var pipe = UniquePipe();
        var me = WindowsIdentity.GetCurrent().User!;
        await using var server = FakePipeServer.Start(pipe, () =>
        {
            var security = new PipeSecurity();
            security.SetOwner(me);
            security.AddAccessRule(new PipeAccessRule(me, PipeAccessRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new PipeAccessRule(me, PipeAccessRights.ReadWrite, AccessControlType.Deny));
            return security;
        }, FakePipeServer.HoldUntilClientClosesAsync);
        await using var client = new PipeClient(pipe, PipeTrust.ElevatedOwnerOrCurrentUser);
        client.Start();

        await WaitForAsync(() => client.LastFailure == HostConnectionFailure.AccessDenied);
        client.LastError.Should().StartWith("无权打开后台管道：").And.Contain("0x80070005");
        server.Accepted.Should().Be(0);
        client.Connections.Should().Be(0);
    }

    [Fact]
    public async Task Production_trust_rejects_a_pipe_owned_by_an_ordinary_user()
    {
        var pipe = UniquePipe();
        var me = WindowsIdentity.GetCurrent().User!;
        me.Should().NotBe(PipeServerIdentity.LocalSystem, "the rejection case needs a non-system test identity");
        await using var server = FakePipeServer.Start(pipe, () =>
        {
            var security = new PipeSecurity();
            security.SetOwner(me);
            security.AddAccessRule(new PipeAccessRule(me, PipeAccessRights.FullControl, AccessControlType.Allow));
            return security;
        }, FakePipeServer.HoldUntilClientClosesAsync);
        await using var client = new PipeClient(pipe); // production default: PipeTrust.ElevatedOwner
        client.Start();

        await WaitForAsync(() => client.LastFailure == HostConnectionFailure.TrustRejected);
        client.LastError.Should().StartWith("后台身份校验未通过，已拒绝连接：");
        server.Accepted.Should().BeGreaterThan(0, "the pipe was opened, then refused after the owner check");
        client.Connections.Should().Be(0);
        client.State.Should().Be(ConnectionState.Disconnected);
    }

    [Fact]
    public async Task Lost_session_is_reported_without_payload_and_a_reconnect_clears_it()
    {
        var pipe = UniquePipe();
        const string Secret = "SECRET-PAYLOAD-77";
        var sessions = 0;
        await using var server = FakePipeServer.Start(pipe, security: null, async (stream, ct) =>
        {
            if (Interlocked.Increment(ref sessions) == 1)
            {
                // a malformed frame carrying a token-like value, then hang up
                var body = Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"token\":\"" + Secret + "\"");
                var frame = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
                await stream.WriteAsync(frame, ct);
                await stream.WriteAsync(body, ct);
                await stream.FlushAsync(ct);
                await Task.Delay(100, ct);
                return;
            }

            await FakePipeServer.HoldUntilClientClosesAsync(stream, ct);
        });
        await using var client = new PipeClient(pipe, PipeTrust.ElevatedOwnerOrCurrentUser);
        var seen = new List<(ConnectionState State, HostConnectionFailure Failure, string? Error)>();
        client.StateChanged += (_, s) =>
        {
            lock (seen)
            {
                seen.Add((s, client.LastFailure, client.LastError));
            }
        };
        client.Start();

        await WaitForAsync(() => client.Connections == 2 && client.State == ConnectionState.Connected);
        client.LastError.Should().BeNull("a successful connect clears the previous diagnostic");
        client.LastFailure.Should().Be(HostConnectionFailure.None);
        lock (seen)
        {
            seen.Select(x => x.State).Should().ContainInOrder(ConnectionState.Connected, ConnectionState.Disconnected, ConnectionState.Connected);
            var lost = seen.First(x => x.State == ConnectionState.Disconnected);
            lost.Failure.Should().Be(HostConnectionFailure.SessionLost);
            lost.Error.Should().StartWith("与后台的会话已断开（").And.NotContain(Secret).And.NotContain("token");
            lost.Error!.Length.Should().BeLessThanOrEqualTo(PipeClientDiagnostics.MaxTextLength);
            seen.First(x => x.State == ConnectionState.Connected).Error.Should().BeNull();
        }
    }

    [Fact]
    public async Task Ui_fault_block_is_one_way_and_closes_the_session_without_reconnecting()
    {
        var pipe = UniquePipe();
        await using var server = FakePipeServer.Start(pipe, security: null, FakePipeServer.HoldUntilClientClosesAsync);
        await using var client = new PipeClient(pipe, PipeTrust.ElevatedOwnerOrCurrentUser);
        client.Start();
        (await client.WaitConnectedAsync(Patience)).Should().BeTrue();
        client.Require().Should().NotBeNull();

        client.BlockForUiFault("first reason");
        client.BlockForUiFault("second reason");

        client.IsBlockedForUiFault.Should().BeTrue();
        foreach (var require in AllRequires(client))
        {
            require.Should().Throw<HostAccessBlockedException>().WithMessage("first reason");
        }

        await WaitForAsync(() => client.State == ConnectionState.Disconnected);
        client.Service.Should().BeNull("raw access cannot bypass the fault barrier");
        (await client.WaitConnectedAsync(TimeSpan.FromSeconds(1))).Should().BeFalse();
        await Task.Delay(300);
        client.Connections.Should().Be(1);
    }

    [Fact]
    public async Task Disconnected_require_accessors_share_one_typed_message()
    {
        await using var client = new PipeClient(UniquePipe(), PipeTrust.ElevatedOwnerOrCurrentUser);
        foreach (var require in AllRequires(client))
        {
            require.Should().Throw<HostNotConnectedException>().WithMessage("后台未连接。")
                .Which.Should().BeAssignableTo<InvalidOperationException>("existing callers catch InvalidOperationException");
        }

        client.IsBlockedForUiFault.Should().BeFalse();
    }

    [Fact]
    public async Task Dispose_stops_reconnecting_reports_stopped_and_releases_waiters()
    {
        var client = new PipeClient(UniquePipe(), PipeTrust.ElevatedOwnerOrCurrentUser);
        client.Start();
        var waiter = client.WaitConnectedAsync(TimeSpan.FromMinutes(5));
        await Task.Delay(200);

        var clock = Stopwatch.StartNew();
        await client.DisposeAsync();
        (await waiter.WaitAsync(Patience)).Should().BeFalse();
        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));

        client.LastFailure.Should().Be(HostConnectionFailure.ClientStopped);
        client.LastError.Should().Be("界面已停止连接后台");
        (await client.WaitConnectedAsync(Timeout.InfiniteTimeSpan)).Should().BeFalse();
        await client.DisposeAsync(); // idempotent
        client.Invoking(c => c.Start()).Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public async Task Wait_connected_honours_timeout_cancellation_and_argument_bounds()
    {
        await using var client = new PipeClient(UniquePipe(), PipeTrust.ElevatedOwnerOrCurrentUser);

        var clock = Stopwatch.StartNew();
        (await client.WaitConnectedAsync(TimeSpan.FromMilliseconds(150))).Should().BeFalse();
        clock.Elapsed.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(140)).And.BeLessThan(TimeSpan.FromSeconds(5));

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await client.Awaiting(c => c.WaitConnectedAsync(Timeout.InfiniteTimeSpan, cts.Token))
            .Should().ThrowAsync<OperationCanceledException>();
        await client.Awaiting(c => c.WaitConnectedAsync(TimeSpan.FromSeconds(-2)))
            .Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData("Connect", "timeout", HostConnectionFailure.ConnectTimeout)]
    [InlineData("Connect", "denied", HostConnectionFailure.AccessDenied)]
    [InlineData("Connect", "io", HostConnectionFailure.PipeIo)]
    [InlineData("Connect", "invalid", HostConnectionFailure.Unexpected)]
    [InlineData("Verify", "denied", HostConnectionFailure.TrustRejected)]
    [InlineData("Verify", "io", HostConnectionFailure.PipeIo)]
    [InlineData("Verify", "invalid", HostConnectionFailure.Unexpected)]
    [InlineData("Session", "io", HostConnectionFailure.SessionLost)]
    [InlineData("Session", "invalid", HostConnectionFailure.SessionLost)]
    public void Failures_are_classified_by_phase(string phaseName, string kind, HostConnectionFailure expected)
    {
        var phase = Enum.Parse<PipeConnectPhase>(phaseName);
        PipeClientDiagnostics.Classify(phase, Make(kind), stopRequested: false).Should().Be(expected);
        PipeClientDiagnostics.Classify(phase, Make(kind), stopRequested: true).Should().Be(HostConnectionFailure.ClientStopped);
    }

    [Fact]
    public void Diagnostic_text_is_single_line_bounded_and_never_quotes_unexpected_or_session_messages()
    {
        var huge = new IOException("line one\r\nline two\t" + new string('x', 5000));
        var io = PipeClientDiagnostics.FromException(PipeConnectPhase.Connect, huge, stopRequested: false);
        io.Kind.Should().Be(HostConnectionFailure.PipeIo);
        io.Text.Should().StartWith("后台管道读写失败：line one line two xxx").And.EndWith("… (0x80131620)");
        io.Text.Length.Should().BeLessThanOrEqualTo(PipeClientDiagnostics.MaxTextLength);
        io.Text.Should().NotContainAny("\r", "\n", "\t");

        var payload = new InvalidOperationException("{\"token\":\"SECRET\"}", new FormatException("SECRET-INNER"));
        var unexpected = PipeClientDiagnostics.FromException(PipeConnectPhase.Connect, payload, stopRequested: false);
        unexpected.Text.Should().Contain("InvalidOperationException ← FormatException").And.NotContain("SECRET");

        var session = PipeClientDiagnostics.FromException(PipeConnectPhase.Session, payload, stopRequested: false, "ParseError\n{token}");
        session.Text.Should().Be("与后台的会话已断开（ParseError {token}，InvalidOperationException ← FormatException, 0x80131509）");

        PipeClientDiagnostics.Sanitize(" \u0007 ", 10).Should().BeEmpty();
        PipeClientDiagnostics.Sanitize(new string('y', 40), 10).Should().Be(new string('y', 9) + "…");
    }

    private static Exception Make(string kind) => kind switch
    {
        "timeout" => new OperationCanceledException(),
        "denied" => new UnauthorizedAccessException("denied"),
        "io" => new IOException("broken pipe"),
        _ => new InvalidOperationException("odd"),
    };

    private static IEnumerable<Action> AllRequires(PipeClient c) =>
    [
        () => c.Require(),
        () => c.RequireScreens(),
        () => c.RequireScreenSettings(),
        () => c.RequireScreenPerformance(),
        () => c.RequireScreenVideo(),
        () => c.RequirePackages(),
        () => c.RequireLibrary(),
    ];

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.Elapsed > Patience)
            {
                throw new TimeoutException("Condition not reached within " + Patience);
            }

            await Task.Delay(10);
        }
    }

    /// <summary>Accept loop on a unique local pipe; one instance at a time, each session handled by <c>serve</c>.</summary>
    private sealed class FakePipeServer : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private Task _loop = Task.CompletedTask;
        private int _accepted;

        public int Accepted => Volatile.Read(ref _accepted);

        public static async Task HoldUntilClientClosesAsync(NamedPipeServerStream stream, CancellationToken ct)
        {
            var buffer = new byte[4096];
            while (await stream.ReadAsync(buffer, ct) > 0)
            {
            }
        }

        public static FakePipeServer Start(string name, Func<PipeSecurity>? security, Func<NamedPipeServerStream, CancellationToken, Task> serve)
        {
            var server = new FakePipeServer();
            server._loop = Task.Run(() => server.RunAsync(name, security, serve));
            return server;
        }

        private async Task RunAsync(string name, Func<PipeSecurity>? security, Func<NamedPipeServerStream, CancellationToken, Task> serve)
        {
            var ct = _stop.Token;
            while (!ct.IsCancellationRequested)
            {
                await using var stream = security is null
                    ? new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous)
                    : NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security());
                try
                {
                    await stream.WaitForConnectionAsync(ct);
                    Interlocked.Increment(ref _accepted);
                    await serve(stream, ct);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (IOException)
                {
                    // client went away; accept the next one
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            await _loop.WaitAsync(Patience);
            _stop.Dispose();
        }
    }
}
