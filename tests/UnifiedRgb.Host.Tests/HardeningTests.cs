using System.IO.Pipes;
using System.Security.Principal;
using UnifiedRgb.App.Ipc;
using UnifiedRgb.Cli;
using UnifiedRgb.Contracts;
using UnifiedRgb.Devices;
using UnifiedRgb.Host.Ipc;
using UnifiedRgb.Transports.Safety;

namespace UnifiedRgb.Host.Tests;

/// <summary>M8: diagnostics packet ring buffer and client-side pipe-server identity.</summary>
public sealed class PacketLogTests
{
    [Fact]
    public void Ring_keeps_the_newest_frames_oldest_first()
    {
        var log = new PacketLog(3);
        log.Snapshot().Should().BeEmpty();
        for (var i = 0; i < 5; i++)
        {
            log.Record("c", i, 64);
        }

        log.Total.Should().Be(5);
        log.Snapshot().Select(p => p.Opcode).Should().Equal(2, 3, 4);
        new PacketLog().Capacity.Should().Be(2000);
    }

    [Fact]
    public async Task Tap_records_only_frames_the_transport_accepted_and_never_firewall_blocked_ones()
    {
        var log = new PacketLog();
        var raw = new SinkTransport();
        var tap = new PacketTap(raw, "LianLi", f => OpcodeLayout.LianLi.OpcodeOf(f.Span), log);
        var chain = new OpcodeFirewall(tap, OpcodeLayout.LianLi);

        var relay = new byte[64];
        relay[0] = 16; // chunk 0 of an RF packet: [16][0][channel][rx][18][RF_CMD]…
        relay[4] = 18;
        relay[5] = 5;
        await chain.SendAsync(relay, default);
        var plain = new byte[64];
        plain[0] = 17;
        await chain.SendAsync(plain, default);

        var denied = new byte[64];
        denied[0] = 32;
        var send = async () => await chain.SendAsync(denied, default);
        await send.Should().ThrowAsync<ForbiddenOpcodeException>();

        raw.Fail = true;
        var failing = async () => await chain.SendAsync(plain, default);
        await failing.Should().ThrowAsync<IOException>();

        log.Snapshot().Select(p => (p.Channel, p.Opcode, p.Length)).Should().Equal(("LianLi", (16 << 8) | 5, 64), ("LianLi", 17, 64));
    }

    [Fact]
    public async Task Status_with_diagnostics_returns_the_packet_ring()
    {
        await using var host = await TestHost.StartAsync();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (PacketLog.Shared.Total == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        var status = host.Engine.BuildStatus(diagnostics: true);
        status.Packets.Should().NotBeNull().And.NotBeEmpty();
        status.Packets!.Count.Should().BeLessThanOrEqualTo(PacketLog.DefaultCapacity);
        status.Packets.Should().Contain(p => p.Controller == "Virtual");
        host.Engine.BuildStatus(diagnostics: false).Packets.Should().BeNull();
    }

    private sealed class SinkTransport : ITransport
    {
        public bool Fail { get; set; }

        public ValueTask SendAsync(ReadOnlyMemory<byte> frame, CancellationToken ct) =>
            Fail ? ValueTask.FromException(new IOException("unplugged")) : ValueTask.CompletedTask;

        public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct) => ValueTask.FromResult(0);
    }
}

[Collection("Host")]
public sealed class PipeIdentityTests
{
    private static readonly SecurityIdentifier SomeUser = new("S-1-5-21-1-2-3-1001");

    private static bool Elevated => PipeAcl.IsElevated();

    [Fact]
    public void Only_administrators_or_system_owned_pipes_are_trusted_in_production()
    {
        PipeServerIdentity.IsTrusted(PipeServerIdentity.Administrators, PipeTrust.ElevatedOwner).Should().BeTrue();
        PipeServerIdentity.IsTrusted(PipeServerIdentity.LocalSystem, PipeTrust.ElevatedOwner).Should().BeTrue();
        PipeServerIdentity.IsTrusted(SomeUser, PipeTrust.ElevatedOwner, SomeUser).Should().BeFalse("a squatter can only own its pipe as itself");
        PipeServerIdentity.IsTrusted(null, PipeTrust.ElevatedOwnerOrCurrentUser).Should().BeFalse();
        PipeServerIdentity.IsTrusted(SomeUser, PipeTrust.ElevatedOwnerOrCurrentUser, SomeUser).Should().BeTrue();
        PipeServerIdentity.IsTrusted(new SecurityIdentifier("S-1-5-21-1-2-3-1002"), PipeTrust.ElevatedOwnerOrCurrentUser, SomeUser).Should().BeFalse();
    }

    [Fact]
    public void Elevated_host_makes_administrators_the_pipe_owner()
    {
        PipeAcl.Create(SomeUser, elevated: true).GetOwner(typeof(SecurityIdentifier)).Should().Be(PipeServerIdentity.Administrators);
        PipeAcl.Create(SomeUser, elevated: false).GetOwner(typeof(SecurityIdentifier)).Should().BeNull("the kernel assigns the token owner");
    }

    [Fact]
    public async Task Clients_refuse_a_pipe_owned_by_an_ordinary_user()
    {
        // An unelevated "squatter" (this test process) serves the pipe name; the production trust policy must refuse it.
        if (Elevated)
        {
            return; // an elevated test run owns the pipe as Administrators, so it cannot play the squatter
        }

        await using var host = await TestHost.StartAsync();
        await using (var probe = await OpenAsync(host.Options.PipeName))
        {
            PipeServerIdentity.OwnerOf(probe).Should().Be(WindowsIdentity.GetCurrent().User!);
        }

        await using var gui = new PipeClient(host.Options.PipeName);
        gui.Start();
        (await gui.WaitConnectedAsync(TimeSpan.FromSeconds(2))).Should().BeFalse();
        gui.LastError.Should().Contain("impostor");

        var o = new StringWriter();
        var e = new StringWriter();
        (await CliApp.RunAsync(["--pipe", host.Options.PipeName, "list"], o, e)).Should().Be(1);
        e.ToString().Should().Contain("impostor");
    }

    private static async Task<NamedPipeClientStream> OpenAsync(string name)
    {
        var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(5000);
        return pipe;
    }
}

public sealed class LogRotationTests
{
    [Fact]
    public void Host_log_rolls_on_size_and_keeps_only_the_configured_number_of_files()
    {
        var options = new UnifiedRgbOptions
        {
            DataDirectory = Path.Combine(Path.GetTempPath(), "urgb-tests", Guid.NewGuid().ToString("N")),
            LogFileSizeLimitBytes = 4096,
            LogRetainedFileCount = 3,
        };
        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build();
        try
        {
            using (var log = HostLogging.Configure(new Serilog.LoggerConfiguration(), config, options).CreateLogger())
            {
                for (var i = 0; i < 2000; i++)
                {
                    log.Information("line {Index} {Padding}", i, new string('x', 40));
                }
            }

            var files = Directory.GetFiles(Path.Combine(options.ResolvedDataDirectory, "logs"), "host-*.log");
            files.Should().HaveCount(3, "older files beyond the retention limit are deleted");
            files.Should().OnlyContain(f => new FileInfo(f).Length <= 4096 + 512);
            File.ReadAllText(files.OrderBy(f => f, StringComparer.Ordinal).Last()).Should().Contain("line 1999");
        }
        finally
        {
            Directory.Delete(options.ResolvedDataDirectory, recursive: true);
        }
    }

    [Fact]
    public void Shipped_defaults_are_5MB_by_10_files()
    {
        var o = new UnifiedRgbOptions();
        o.LogFileSizeLimitBytes.Should().Be(5 * 1024 * 1024);
        o.LogRetainedFileCount.Should().Be(10);
    }
}

/// <summary>
/// M8 SIM soak: render + RPC session churn + scene/profile changes; managed heap, handles and threads must stay flat.
/// Duration = URGB_SOAK_SECONDS (default 60; the owner runs tools/soak.ps1 for 24 h).
/// </summary>
[Collection("Host")]
public sealed class SoakTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Fact]
    public async Task Memory_handles_and_threads_stay_flat_under_render_and_rpc_churn()
    {
        var seconds = int.TryParse(Environment.GetEnvironmentVariable("URGB_SOAK_SECONDS"), System.Globalization.CultureInfo.InvariantCulture, out var s) ? s : 60;
        await using var host = await TestHost.StartAsync();
        var effects = new[] { "Meteor", "Runway", "Rainbow", "StaticColor", "Breathing" };

        async Task ChurnAsync(int round)
        {
            var pipe = new NamedPipeClientStream(".", host.Options.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await using (pipe)
            {
                await pipe.ConnectAsync(5000);
                using var rpc = new StreamJsonRpc.JsonRpc(new StreamJsonRpc.HeaderDelimitedMessageHandler(pipe, pipe, PipeServer.CreateFormatter()));
                var svc = rpc.Attach<IRgbService>();
                rpc.StartListening();
                var controllers = await svc.ListControllersAsync();
                var targets = controllers.SelectMany(c => c.Zones.Select(z => new Contracts.Dto.ZoneRefDto(c.Id, z.Id))).ToArray();
                await svc.SetSceneAsync(new Contracts.Dto.SceneDto([new Contracts.Dto.AssignmentDto(targets, effects[round % effects.Length], [])]));
                await svc.GetStatusAsync(diagnostics: true);
            }
        }

        // warm-up: JIT, pools, first sessions
        for (var i = 0; i < 20; i++)
        {
            await ChurnAsync(i);
        }

        var (heap0, handles0, threads0) = Measure();
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        var rounds = 0;
        while (DateTime.UtcNow < deadline)
        {
            await ChurnAsync(rounds++);
            await Task.Delay(50);
        }

        await Task.Delay(500); // let closed sessions finish
        var (heap1, handles1, threads1) = Measure();

        output.WriteLine($"SOAK {seconds}s: {rounds} sessions; heap {heap0 / 1024} KiB -> {heap1 / 1024} KiB; handles {handles0} -> {handles1}; threads {threads0} -> {threads1}");
        rounds.Should().BeGreaterThan(seconds * 5);
        (heap1 - heap0).Should().BeLessThan(8 * 1024 * 1024, $"managed heap {heap0} → {heap1} after {rounds} sessions");
        (handles1 - handles0).Should().BeLessThan(100, $"handles {handles0} → {handles1}");
        (threads1 - threads0).Should().BeLessThan(10, $"threads {threads0} → {threads1}");
        var status = host.Engine.BuildStatus(false);
        status.Devices.Should().OnlyContain(d => d.Dropped == 0 && d.LastError == null);
    }

    private static (long Heap, int Handles, int Threads) Measure()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        using var p = System.Diagnostics.Process.GetCurrentProcess();
        return (GC.GetTotalMemory(forceFullCollection: true), p.HandleCount, p.Threads.Count);
    }
}
